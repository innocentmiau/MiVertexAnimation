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
        private static readonly int HOLD_ID = Shader.PropertyToID("_VATHold");
        private static readonly int PREVIOUS_CLIP_ID = Shader.PropertyToID("_VATPreviousClip");
        private static readonly int PREVIOUS_START_ID = Shader.PropertyToID("_VATPreviousStart");
        private static readonly int PREVIOUS_HOLD_ID = Shader.PropertyToID("_VATPreviousHold");
        private static readonly int BLEND_START_ID = Shader.PropertyToID("_VATBlendStart");
        private static readonly int SPEED_ID = Shader.PropertyToID("_VATSpeed");
        private static readonly int CLIP_COUNT_ID = Shader.PropertyToID("_VATClipCount");

        /*
         * The shader stops a clip at a fraction of itself rather than at a flag, so looping,
         * stopping on the last frame and stopping anywhere in between are all one number.
         * Zero has to mean looping, because that is what an instance nothing has written reads as.
         */
        private const float HOLD_NONE = 0f;
        private const float HOLD_END = 1f;

        // A freeze on the very first frame still has to read as a freeze rather than as looping.
        private const float FREEZE_MIN = 1e-4f;

        private const float NOT_FROZEN = -1f;

        /*
         * Speed divides into every conversion between seconds and position in a clip, and rebasing a
         * start time across a change scales the gap by the ratio of the two - so a speed near zero
         * both loses precision and pushes the start time somewhere absurd. Freeze is how playback
         * stops; this is only how fast it runs.
         */
        private const float MIN_SPEED = 0.01f;

        [FormerlySerializedAs("_clipSet")]
        [SerializeField]
        [Tooltip("The clip list the baker generated. Lets the inspector show names instead of indices.")]
        private VATClipSet clipSet;

        [FormerlySerializedAs("_clip")]
        [SerializeField]
        [Tooltip("Slice index of the clip to play. Shown as a name dropdown when a Clip Set is assigned.")]
        private int clipIndex;

        [SerializeField]
        [Tooltip("Off plays the clip above once when this object is enabled and then holds its last " +
                 "frame, for something that spawns already dead or arrives mid-pose. Play always loops " +
                 "and PlayOnce always holds, whatever this is set to.")]
        private bool loop = true;

        [SerializeField]
        [Min(MIN_SPEED)]
        [Tooltip("Multiplies playback for this instance, on top of any per-clip speed. Overrides the " +
                 "material's Playback Speed, which is what a renderer with no VATAnimator uses. " +
                 "Zero does not stop anything - Freeze does that.")]
        private float speed = 1f;

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
        private bool _holdsEnd;
        private float _frozenPhase = NOT_FROZEN;
        private int _previousClip;
        private float _previousStart;
        private bool _previousHoldsEnd;
        private float _previousFrozenPhase = NOT_FROZEN;
        private float _blendStart;

        /*
         * One entry per slice, and only allocated once something asks for a per-clip speed, so the
         * common case of a crowd that all runs at one speed carries no array at all.
         */
        private float[] _clipSpeeds;

        // What Apply last wrote, which is the "from" side of the next rebase.
        private float _appliedSpeed = 1f;

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

        /// <summary>
        /// Multiplies playback for this instance, on top of whatever the current clip's own speed is.
        /// Changing it keeps the clip where it is rather than jumping it somewhere else.
        /// </summary>
        public float Speed
        {
            get => speed;
            set
            {
                float clamped = Mathf.Max(value, MIN_SPEED);
                if (clamped == speed) return;

                speed = clamped;
                Apply();
            }
        }

        /// <summary>What the shader is actually playing at: this instance's speed times the clip's own.</summary>
        public float CurrentSpeed => EffectiveSpeed;

        /// <summary>True while Freeze is holding one pose.</summary>
        public bool IsFrozen => _frozenPhase >= 0f;

        /// <summary>Where the current clip is, as a fraction of one cycle.</summary>
        public float NormalizedTime => CurrentNormalized();

        private float EffectiveSpeed => Mathf.Max(speed, MIN_SPEED) * ClipSpeedOf(clipIndex);

        /// <summary>
        /// Sets the speed one clip plays at, whether or not it is the clip playing now. This is the
        /// one to reach for when a run cycle has to keep up with a movement speed and nothing else
        /// should change: set it once when the movement speed changes, not every frame, and never
        /// mind which clip is on screen.
        /// </summary>
        /// <param name="clip">Slice index, ignored when it is outside the range that was baked.</param>
        /// <param name="clipSpeed">Multiplier for that clip alone, on top of Speed.</param>
        public void SetClipSpeed(int clip, float clipSpeed)
        {
            int count = ClipCount;
            if (clip < 0 || clip >= count) return;

            float clamped = Mathf.Max(clipSpeed, MIN_SPEED);
            if (clamped == ClipSpeedOf(clip)) return;

            if (_clipSpeeds == null || _clipSpeeds.Length < count)
            {
                float[] grown = new float[count];
                for (int i = 0; i < grown.Length; i++)
                    grown[i] = _clipSpeeds != null && i < _clipSpeeds.Length ? _clipSpeeds[i] : 1f;

                _clipSpeeds = grown;
            }

            _clipSpeeds[clip] = clamped;

            // Apply reconciles the change, and does nothing at all when the clip set is not the
            // one playing - which is what makes this safe to call on anything at any time.
            Apply();
        }

        /// <summary>
        /// Sets the speed one named clip plays at.
        /// </summary>
        /// <param name="clipName">Name of the source AnimationClip that was baked.</param>
        /// <param name="clipSpeed">Multiplier for that clip alone, on top of Speed.</param>
        /// <returns>True when a clip of that name was found.</returns>
        public bool SetClipSpeed(string clipName, float clipSpeed)
        {
            int index = ResolveName(clipName);
            if (index < 0) return false;

            SetClipSpeed(index, clipSpeed);
            return true;
        }

        /// <summary>The speed set for one clip, which is 1 unless something set it.</summary>
        /// <param name="clip">Slice index.</param>
        public float GetClipSpeed(int clip) => ClipSpeedOf(clip);

        /// <summary>The speed set for one named clip, which is 1 unless something set it.</summary>
        /// <param name="clipName">Name of the source AnimationClip that was baked.</param>
        public float GetClipSpeed(string clipName)
        {
            int index = clipSet ? clipSet.IndexOf(clipName) : -1;
            return index < 0 ? 1f : ClipSpeedOf(index);
        }

        private float ClipSpeedOf(int clip)
        {
            return _clipSpeeds != null && clip >= 0 && clip < _clipSpeeds.Length ? _clipSpeeds[clip] : 1f;
        }

        private void OnEnable()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);

            float now = VATTime.Now;
            // Offsetting the start time backwards lands the loop at an arbitrary phase. Kept small:
            // the shader does frac() on this, and large values lose precision there.
            // Never applied to a clip that is not looping, which would arrive part way through
            // the single run it gets.
            _clipStart = randomizeStartPhase && loop ? now - UnityEngine.Random.value * 100f : now;
            _holdsEnd = !loop;
            _frozenPhase = NOT_FROZEN;

            // "No fade running" is expressed by pointing the outgoing slot at the current clip,
            // so any blend that does run interpolates a clip with itself and is invisible.
            // A sentinel like negative infinity would risk a NaN reaching a vertex position,
            // and one NaN vertex makes the whole mesh disappear.
            _previousClip = clipIndex;
            _previousStart = _clipStart;
            _previousHoldsEnd = _holdsEnd;
            _previousFrozenPhase = NOT_FROZEN;
            _blendStart = now;

            // Nothing to reconcile on a fresh start, and seeding this stops the first Apply
            // rebasing against a speed left over from whatever this instance was doing before.
            _appliedSpeed = EffectiveSpeed;

            BeginPlayback(!loop, -1);
            Apply();
        }

        private void OnDisable()
        {
            VATAnimatorDriver.Unregister(this);
        }

        private void OnValidate()
        {
            if (!isActiveAndEnabled) return;

            // Loop describes how the clip runs, not only how it started, so flipping it in the
            // inspector has to reach the state the shader is actually reading.
            if (!_oneShot && !IsFrozen) _holdsEnd = !loop;

            Apply();
        }

        /// <summary>
        /// Cross-fades into a looping clip, which starts at its first frame.
        /// </summary>
        /// <param name="clip">Slice index, clamped into the range that was baked.</param>
        /// <param name="restartIfSame">Restart the clip already playing instead of ignoring the call.</param>
        public void Play(int clip, bool restartIfSame = false)
        {
            clip = Mathf.Clamp(clip, 0, ClipCount - 1);
            if (clip == clipIndex && !_oneShot && !IsFrozen && !restartIfSame) return;

            HandOverToPrevious();

            clipIndex = clip;
            _clipStart = VATTime.Now;
            _holdsEnd = false;
            _frozenPhase = NOT_FROZEN;

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
        /// Cross-fades into a looping clip at a given speed, which becomes that clip's speed from
        /// then on - the same thing SetClipSpeed sets, reached from the call that starts it.
        /// </summary>
        /// <param name="clip">Slice index, clamped into the range that was baked.</param>
        /// <param name="clipSpeed">Multiplier for this clip alone, on top of Speed.</param>
        /// <param name="restartIfSame">Restart the clip already playing instead of ignoring the call.</param>
        public void Play(int clip, float clipSpeed, bool restartIfSame = false)
        {
            SetClipSpeed(Mathf.Clamp(clip, 0, ClipCount - 1), clipSpeed);
            Play(clip, restartIfSame);
        }

        /// <summary>
        /// Cross-fades into a named looping clip at a given speed, which becomes that clip's speed
        /// from then on.
        /// </summary>
        /// <param name="clipName">Name of the source AnimationClip that was baked.</param>
        /// <param name="clipSpeed">Multiplier for this clip alone, on top of Speed.</param>
        /// <param name="restartIfSame">Restart the clip already playing instead of ignoring the call.</param>
        /// <returns>True when a clip of that name was found and started.</returns>
        public bool Play(string clipName, float clipSpeed, bool restartIfSame = false)
        {
            int index = ResolveName(clipName);
            if (index < 0) return false;

            Play(index, clipSpeed, restartIfSame);
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
            _clipStart = VATTime.Now;
            _holdsEnd = true;
            _frozenPhase = NOT_FROZEN;

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
        /// Plays a clip through once at a given speed, which becomes that clip's speed from then on.
        /// </summary>
        /// <param name="clip">Slice index of the clip to play through.</param>
        /// <param name="returnTo">Slice to cross-fade into once it ends, or -1 to hold the last frame.</param>
        /// <param name="clipSpeed">Multiplier for this clip alone, on top of Speed.</param>
        public void PlayOnce(int clip, int returnTo, float clipSpeed)
        {
            SetClipSpeed(Mathf.Clamp(clip, 0, ClipCount - 1), clipSpeed);
            PlayOnce(clip, returnTo);
        }

        /// <summary>
        /// Plays a named clip through once at a given speed, which becomes that clip's speed from
        /// then on.
        /// </summary>
        /// <param name="clipName">Name of the clip to play through.</param>
        /// <param name="returnTo">Name of the clip to cross-fade into once it ends, or null to hold.</param>
        /// <param name="clipSpeed">Multiplier for this clip alone, on top of Speed.</param>
        /// <returns>True when a clip of that name was found and started.</returns>
        public bool PlayOnce(string clipName, string returnTo, float clipSpeed)
        {
            int index = ResolveName(clipName);
            if (index < 0) return false;

            SetClipSpeed(index, clipSpeed);
            return PlayOnce(clipName, returnTo);
        }

        /// <summary>
        /// Switches to a clip with no cross-fade at all.
        /// </summary>
        /// <param name="clip">Slice index to switch to, clamped into the range that was baked.</param>
        public void Snap(int clip)
        {
            clipIndex = Mathf.Clamp(clip, 0, ClipCount - 1);
            _clipStart = VATTime.Now;
            _holdsEnd = false;
            _frozenPhase = NOT_FROZEN;
            _previousClip = clipIndex;
            _previousStart = _clipStart;
            _previousHoldsEnd = false;
            _previousFrozenPhase = NOT_FROZEN;
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

            RebaseForSpeed();

            _block ??= new MaterialPropertyBlock();

            foreach (Renderer renderer in _renderers)
            {
                if (!renderer) continue;

                renderer.GetPropertyBlock(_block);
                _block.SetFloat(CLIP_ID, clipIndex);
                _block.SetFloat(CLIP_START_ID, _clipStart);
                _block.SetFloat(HOLD_ID, HoldOf(_holdsEnd, _frozenPhase));
                _block.SetFloat(PREVIOUS_CLIP_ID, _previousClip);
                _block.SetFloat(PREVIOUS_START_ID, _previousStart);
                _block.SetFloat(PREVIOUS_HOLD_ID, HoldOf(_previousHoldsEnd, _previousFrozenPhase));
                _block.SetFloat(BLEND_START_ID, _blendStart);
                _block.SetFloat(SPEED_ID, _appliedSpeed);
                renderer.SetPropertyBlock(_block);
            }
        }

        /*
         * Where playback is is (now - start) * speed, so doubling the speed doubles everything already
         * elapsed and the clip jumps to somewhere it was never going to be. Scaling the gap back by the
         * ratio of the two speeds leaves the clip exactly where it is and only changes how fast it
         * leaves - which is the difference between a run cycle that keeps up with a character and one
         * that snaps to a new pose every time the character speeds up.
         *
         * Both slots move, because one speed feeds the clip playing and the clip fading out of. The
         * outgoing one therefore finishes its fade at the incoming clip's speed rather than its own,
         * which costs a fraction of a second of slightly wrong rate and saves a per-instance float.
         *
         * Runs from Apply, so every path that pushes state to the GPU reconciles this first and no
         * caller has to remember to.
         */
        private void RebaseForSpeed()
        {
            float to = EffectiveSpeed;
            if (Mathf.Approximately(_appliedSpeed, to)) return;

            float now = VATTime.Now;
            float scale = _appliedSpeed / to;

            _clipStart = now - ((now - _clipStart) * scale);
            _previousStart = now - ((now - _previousStart) * scale);
            _appliedSpeed = to;
        }

        /// <summary>
        /// Holds the pose on screen right now, for as long as it takes to call Resume.
        /// </summary>
        public void Freeze()
        {
            if (IsFrozen) return;

            float phase = CurrentNormalized();

            // A one-shot that already ran out is holding its last frame on its own, and restating
            // that as a freeze just short of the end would hand frame blending back the wrap that
            // holding the end exists to avoid.
            if (_holdsEnd && phase >= 1f) return;

            FreezeAt(phase);
        }

        /// <summary>
        /// Holds a chosen pose from the current clip, seeking to it first.
        /// </summary>
        /// <param name="normalizedTime">Point in the clip, 0 at the first frame and 1 at the last.</param>
        public void Freeze(float normalizedTime)
        {
            float phase = Mathf.Clamp01(normalizedTime);

            _clipStart = VATTime.Now - phase * CycleSecondsOf(clipIndex);
            FreezeAt(phase);
        }

        /// <summary>
        /// Carries on from the pose Freeze stopped on. Does nothing when nothing is frozen.
        /// </summary>
        public void Resume()
        {
            if (!IsFrozen) return;

            // Playback is nothing but a function of when the clip started, so resuming is a matter
            // of moving that start to whatever would put the clip exactly where it was left.
            _clipStart = VATTime.Now - _frozenPhase * CycleSecondsOf(clipIndex);
            _frozenPhase = NOT_FROZEN;
            _previousFrozenPhase = NOT_FROZEN;

            BeginPlayback(_oneShot, _returnClip);
            Apply();
        }

        private void FreezeAt(float phase)
        {
            _frozenPhase = phase;

            /*
             * The outgoing clip stops too, or a freeze landing during a cross-fade would go on being
             * fed a moving pose from underneath. The fade itself still runs out - it is a fraction of
             * a second and it ends on the frozen pose either way.
             */
            if (_previousFrozenPhase < 0f)
                _previousFrozenPhase = NormalizedOf(_previousClip, _previousStart, _previousHoldsEnd);

            // Nothing is advancing any more, so stop asking to be ticked.
            // This is what keeps a field of bodies as free as a field of loopers.
            VATAnimatorDriver.Unregister(this);
            Apply();
        }

        /*
         * Two unrelated reasons a clip can stop - a one-shot reaching its end, and a freeze holding
         * the pose on screen - collapsed into the one number the shader reads.
         *
         * A freeze that lands exactly on the end is expressed as holding the end rather than as a
         * fraction, because those are the same pose and only one of them survives frame blending.
         */
        private static float HoldOf(bool holdsEnd, float frozenPhase)
        {
            if (frozenPhase < 0f) return holdsEnd ? HOLD_END : HOLD_NONE;

            return frozenPhase >= 1f ? HOLD_END : Mathf.Max(frozenPhase, FREEZE_MIN);
        }

        // How long one cycle of a clip takes at the speed the shader is running it, which is what
        // turns a position in the clip back into a moment in time and the other way round.
        private float CycleSecondsOf(int clip)
        {
            float length = clipSet ? clipSet.LengthAt(clip) : 0f;
            return length > 0f ? length / EffectiveSpeed : 0f;
        }

        // Hand the current state to the outgoing slot before overwriting it,
        // so the fade starts from exactly the pose that was on screen.
        private void HandOverToPrevious()
        {
            _previousClip = clipIndex;
            _previousStart = _clipStart;
            _previousHoldsEnd = _holdsEnd;
            _previousFrozenPhase = _frozenPhase;
            _blendStart = VATTime.Now;
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
            return IsFrozen ? _frozenPhase : NormalizedOf(clipIndex, _clipStart, _holdsEnd);
        }

        /*
         * Reads whichever of the two clips the shader is mixing, so a freeze can stop the outgoing
         * one on the pose it had reached rather than on a guess.
         *
         * Holding the end is what decides between clamping and wrapping here, not whether a one-shot
         * is still in flight: a one-shot that has already finished is no longer one, and asking where
         * it is has to keep answering 1 rather than reporting a cycle it never started.
         */
        private float NormalizedOf(int clip, float start, bool holdsEnd)
        {
            if (!clipSet || clip < 0 || clip >= clipSet.Count) return 0f;

            float cycle = CycleSecondsOf(clip);
            if (cycle <= 0f) return 0f;

            float raw = (VATTime.Now - start) / cycle;
            return holdsEnd ? Mathf.Clamp01(raw) : raw - Mathf.Floor(raw);
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
            // Freeze unregisters, but a tick queued earlier in the same frame can still arrive,
            // and nothing has moved for it to report.
            if (IsFrozen) return false;

            if (!clipSet || clipIndex < 0 || clipIndex >= clipSet.Count) return false;

            // Events and the end of a one-shot are read off the same cycle length the shader is,
            // so a clip slowed down or sped up fires them where it looks like it should.
            float cycle = CycleSecondsOf(clipIndex);
            if (cycle <= 0f) return false;

            bool fromStart = _fireFromStart;
            _fireFromStart = false;

            float raw = (VATTime.Now - _clipStart) / cycle;

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

    }
}
