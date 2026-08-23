using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace MiVertexAnimation
{

    /*
     * A vertex shader has no memory between frames, so it cannot notice that the clip index changed.
     * This records the outgoing clip and the moment the switch happened, and the shader does the rest,
     * cross-fading over _VATBlendDuration.
     *
     * State is written through a MaterialPropertyBlock, so every instance can play a different clip
     * while sharing one material. That routes drawing through GPU instancing rather than the SRP Batcher,
     * which is what you want for a crowd, and requires "Enable GPU Instancing" on the material.
     * The baker sets that for you.
     */
    /// <summary>
    /// Drives clip selection on a VAT renderer: which clip plays, and the cross-fade when it changes.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class VATAnimator : MonoBehaviour
    {

        private static readonly int CLIP_ID = Shader.PropertyToID("_VATClip");
        private static readonly int CLIP_START_ID = Shader.PropertyToID("_VATClipStart");
        private static readonly int CLAMP_ID = Shader.PropertyToID("_VATClamp");
        private static readonly int PREVIOUS_CLIP_ID = Shader.PropertyToID("_VATPreviousClip");
        private static readonly int PREVIOUS_START_ID = Shader.PropertyToID("_VATPreviousStart");
        private static readonly int PREVIOUS_CLAMP_ID = Shader.PropertyToID("_VATPreviousClamp");
        private static readonly int BLEND_START_ID = Shader.PropertyToID("_VATBlendStart");
        private static readonly int CLIP_COUNT_ID = Shader.PropertyToID("_VATClipCount");

        [FormerlySerializedAs("_clipSet")]
        [SerializeField]
        [Tooltip("The clip list the baker generated. Lets the inspector show names instead of indices.")]
        private VATClipSet clipSet;

        [FormerlySerializedAs("_clip")]
        [SerializeField]
        [Tooltip("Slice index of the clip to play. Shown as a name dropdown when a Clip Set is assigned.")]
        private int clipIndex;

        [FormerlySerializedAs("_randomizeStartPhase")]
        [SerializeField]
        [Tooltip("Start each instance at a random point in its loop, so a crowd does not march " +
                 "in step. Explicit Play calls always start the new clip at its first frame.")]
        private bool randomizeStartPhase = true;

        /// <summary>Raised when a clip started with PlayOnce reaches its end.</summary>
        public event Action<VATAnimator, string> ClipFinished;

        /// <summary>
        /// Raised when playback crosses a marker baked into the clip. This is what an attack's hit
        /// frame hangs off, since damage almost never lands on the last frame.
        /// </summary>
        public event Action<VATAnimator, VATClipEvent> ClipEventFired;

        /*
         * Every renderer underneath, not just one on this object. An LOD Group bake puts a renderer per
         * level under a root that has none of its own, and the group enables one at a time - so the
         * state has to be written to all of them or a character freezes the moment it changes level.
         */
        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;

        private float _clipStart;
        private bool _clamp;
        private int _previousClip;
        private float _previousStart;
        private bool _previousClamp;
        private float _blendStart;

        private bool _oneShot;
        private int _returnClip = -1;
        private float _lastNormalized;
        private bool _fireFromStart;

        /// <summary>Assigning a new value cross-fades into that clip, looping.</summary>
        public int Clip
        {
            get => clipIndex;
            set => Play(value);
        }

        /// <summary>The clip list this animator plays from, or null when none was assigned.</summary>
        public VATClipSet ClipSet => clipSet;

        /// <summary>Number of clips baked into the material's texture array.</summary>
        public int ClipCount
        {
            get
            {
                if (clipSet && clipSet.Count > 0) return clipSet.Count;

                Material material = _renderers != null && _renderers.Length > 0 && _renderers[0]
                    ? _renderers[0].sharedMaterial
                    : null;
                return material ? Mathf.Max(1, Mathf.RoundToInt(material.GetFloat(CLIP_COUNT_ID))) : 1;
            }
        }

        /// <summary>Name of the clip currently playing, when a Clip Set is assigned.</summary>
        public string CurrentClipName => clipSet ? clipSet.NameAt(clipIndex) : $"slice {clipIndex}";

        private void OnEnable()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);

            float now = Now;
            // Offsetting the start time backwards lands the loop at an arbitrary phase. Kept small:
            // the shader does frac() on this, and large values lose precision there.
            _clipStart = randomizeStartPhase ? now - UnityEngine.Random.value * 100f : now;
            _clamp = false;

            // "No fade running" is expressed by pointing the outgoing slot at the current clip,
            // so any blend that does run interpolates a clip with itself and is invisible.
            // A sentinel like negative infinity would risk a NaN reaching a vertex position,
            // and one NaN vertex makes the whole mesh disappear.
            _previousClip = clipIndex;
            _previousStart = _clipStart;
            _previousClamp = false;
            _blendStart = now;

            BeginPlayback(false, -1);
            Apply();
        }

        private void OnDisable()
        {
            VATAnimatorDriver.Unregister(this);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) Apply();
        }

        /// <summary>
        /// Cross-fades into a looping clip, which starts at its first frame.
        /// </summary>
        /// <param name="clip">Slice index, clamped into the range that was baked.</param>
        /// <param name="restartIfSame">Restart the clip already playing instead of ignoring the call.</param>
        public void Play(int clip, bool restartIfSame = false)
        {
            clip = Mathf.Clamp(clip, 0, ClipCount - 1);
            if (clip == clipIndex && !_oneShot && !restartIfSame) return;

            HandOverToPrevious();

            clipIndex = clip;
            _clipStart = Now;
            _clamp = false;

            BeginPlayback(false, -1);
            Apply();
        }

        /// <summary>
        /// Cross-fades into a clip by name, doing nothing at all rather than silently playing the
        /// wrong animation when the name does not match anything baked.
        /// </summary>
        /// <param name="clipName">Name of the source AnimationClip that was baked.</param>
        /// <param name="restartIfSame">Restart the clip already playing instead of ignoring the call.</param>
        /// <returns>True when a clip of that name was found and started.</returns>
        public bool Play(string clipName, bool restartIfSame = false)
        {
            int index = ResolveName(clipName);
            if (index < 0) return false;

            Play(index, restartIfSame);
            return true;
        }

        /// <summary>
        /// Plays a clip through once and holds its last frame, then raises ClipFinished.
        /// </summary>
        /// <param name="clip">Slice index of the clip to play through.</param>
        /// <param name="returnTo">Slice to cross-fade into once it ends, or -1 to hold the last frame.</param>
        public void PlayOnce(int clip, int returnTo = -1)
        {
            HandOverToPrevious();

            clipIndex = Mathf.Clamp(clip, 0, ClipCount - 1);
            _clipStart = Now;
            _clamp = true;

            BeginPlayback(true, returnTo);
            Apply();
        }

        /// <summary>
        /// Plays a named clip through once and holds its last frame, then raises ClipFinished.
        /// </summary>
        /// <param name="clipName">Name of the clip to play through.</param>
        /// <param name="returnTo">Name of the clip to cross-fade into once it ends, or null to hold.</param>
        /// <returns>True when a clip of that name was found and started.</returns>
        public bool PlayOnce(string clipName, string returnTo = null)
        {
            int index = ResolveName(clipName);
            if (index < 0) return false;

            int returnIndex = string.IsNullOrEmpty(returnTo) ? -1 : ResolveName(returnTo);
            PlayOnce(index, returnIndex);
            return true;
        }

        /// <summary>
        /// Switches to a clip with no cross-fade at all.
        /// </summary>
        /// <param name="clip">Slice index to switch to, clamped into the range that was baked.</param>
        public void Snap(int clip)
        {
            clipIndex = Mathf.Clamp(clip, 0, ClipCount - 1);
            _clipStart = Now;
            _clamp = false;
            _previousClip = clipIndex;
            _previousStart = _clipStart;
            _previousClamp = false;
            _blendStart = _clipStart;

            BeginPlayback(false, -1);
            Apply();
        }

        /// <summary>
        /// Pushes the current playback state to the renderer. Called for you by every Play method.
        /// </summary>
        [ContextMenu("Apply")]
        public void Apply()
        {
            if (_renderers == null || _renderers.Length == 0)
                _renderers = GetComponentsInChildren<Renderer>(true);

            if (_renderers.Length == 0) return;

            _block ??= new MaterialPropertyBlock();

            foreach (Renderer renderer in _renderers)
            {
                if (!renderer) continue;

                renderer.GetPropertyBlock(_block);
                _block.SetFloat(CLIP_ID, clipIndex);
                _block.SetFloat(CLIP_START_ID, _clipStart);
                _block.SetFloat(CLAMP_ID, _clamp ? 1f : 0f);
                _block.SetFloat(PREVIOUS_CLIP_ID, _previousClip);
                _block.SetFloat(PREVIOUS_START_ID, _previousStart);
                _block.SetFloat(PREVIOUS_CLAMP_ID, _previousClamp ? 1f : 0f);
                _block.SetFloat(BLEND_START_ID, _blendStart);
                renderer.SetPropertyBlock(_block);
            }
        }

        // Hand the current state to the outgoing slot before overwriting it,
        // so the fade starts from exactly the pose that was on screen.
        private void HandOverToPrevious()
        {
            _previousClip = clipIndex;
            _previousStart = _clipStart;
            _previousClamp = _clamp;
            _blendStart = Now;
        }

        private int ResolveName(string clipName)
        {
            if (!clipSet)
            {
                Debug.LogWarning($"{name}: VATAnimator has no Clip Set, so clips cannot be used by name.", this);
                return -1;
            }

            int index = clipSet.IndexOf(clipName);
            if (index < 0)
                Debug.LogWarning($"{name}: no baked clip called '{clipName}'.", this);

            return index;
        }

        /*
         * Watching starts from wherever the clip actually is, not from zero.
         * A randomized start phase drops a looping instance into the middle of its cycle,
         * and seeding this at zero would make the first tick fire every event before that point,
         * so a crowd spawning together would discharge most of its events on frame one.
         *
         * Starting exactly at the first frame is the one case where the range has to include its own start,
         * or an event sitting on frame 0 could never fire at all.
         */
        private void BeginPlayback(bool oneShot, int returnTo)
        {
            _oneShot = oneShot;
            _returnClip = returnTo;
            _lastNormalized = CurrentNormalized();
            _fireFromStart = _lastNormalized <= 0f;

            // Only ask for ticks when there is something to tick for,
            // so a settled crowd of loopers costs no per-frame CPU at all.
            if (oneShot || HasEvents(clipIndex)) VATAnimatorDriver.Register(this);
            else VATAnimatorDriver.Unregister(this);
        }

        /// <summary>Where the clip is right now, as a fraction of one cycle.</summary>
        private float CurrentNormalized()
        {
            if (!clipSet || clipIndex < 0 || clipIndex >= clipSet.Count) return 0f;

            float length = clipSet.LengthAt(clipIndex);
            if (length <= 0f) return 0f;

            float raw = (Now - _clipStart) / length;
            return _oneShot ? Mathf.Clamp01(raw) : raw - Mathf.Floor(raw);
        }

        private bool HasEvents(int clip)
        {
            VATClipEvent[] events = clipSet ? clipSet.EventsAt(clip) : null;
            return events?.Length > 0;
        }

        /// <summary>
        /// Advances event bookkeeping. Returns false when there is nothing left to watch,
        /// which is how the driver knows to drop this animator from its list.
        /// </summary>
        internal bool Tick()
        {
            if (!clipSet || clipIndex < 0 || clipIndex >= clipSet.Count) return false;

            float length = clipSet.LengthAt(clipIndex);
            if (length <= 0f) return false;

            bool fromStart = _fireFromStart;
            _fireFromStart = false;

            float raw = (Now - _clipStart) / length;

            if (_oneShot)
            {
                float normalized = Mathf.Min(raw, 1f);
                FireEventsBetween(_lastNormalized, normalized, fromStart);
                _lastNormalized = normalized;

                if (raw < 1f) return true;

                // The shader is holding the last frame, so there is no rush and no wrap to race.
                string finishedName = clipSet.NameAt(clipIndex);
                int returnTo = _returnClip;

                _oneShot = false;
                _returnClip = -1;

                ClipFinished?.Invoke(this, finishedName);

                // A listener may have started something else, so only fall back if it did not.
                if (!_oneShot && returnTo >= 0 && clipIndex == clipSet.IndexOf(finishedName))
                    Play(returnTo, true);

                return _oneShot || HasEvents(clipIndex);
            }

            if (!HasEvents(clipIndex)) return false;

            float loopTime = raw - Mathf.Floor(raw);
            if (loopTime < _lastNormalized)
            {
                // Wrapped, so finish the previous cycle before starting the new one. The new cycle
                // includes its own start, so an event on frame 0 fires once every time round.
                FireEventsBetween(_lastNormalized, 1f, fromStart);
                FireEventsBetween(0f, loopTime, true);
            }
            else
                FireEventsBetween(_lastNormalized, loopTime, fromStart);

            _lastNormalized = loopTime;
            return true;
        }

        /// <summary>
        /// Raises every event whose time falls in a slice of the cycle.
        /// </summary>
        /// <param name="from">Start of the slice, normally exclusive so nothing fires twice.</param>
        /// <param name="to">End of the slice, always inclusive.</param>
        /// <param name="includeStart">Make the start inclusive, for a cycle beginning at its first frame.</param>
        private void FireEventsBetween(float from, float to, bool includeStart)
        {
            if (ClipEventFired == null) return;

            VATClipEvent[] events = clipSet.EventsAt(clipIndex);
            if (events == null) return;

            for (int i = 0; i < events.Length; i++)
            {
                float t = events[i].normalizedTime;
                bool started = includeStart ? t >= from : t > from;
                if (started && t <= to) ClipEventFired.Invoke(this, events[i]);
            }
        }

        /*
         * Must be the same clock the shader's _Time.y runs on.
         * EditorApplication.timeSinceStartup is a different one, editor uptime, often tens of thousands of seconds,
         * and the gap between them wrecks the precision of the frac() in the shader.
         */
        private static float Now => Time.timeSinceLevelLoad;

    }
}
