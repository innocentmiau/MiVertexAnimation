using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Drives the mesh sections a bake left drivable: a head that turns to look at something, a torso
     * that leans, an arm that recoils. Sections are addressed by the name given in the baker, never by
     * index, for the same reason clips and events are - reordering them in the baker would otherwise
     * silently repoint every call in the project.
     *
     * Two ways to drive one, and they are different jobs rather than two flavours of the same one:
     *
     *   TurnTo  describes a transition once and lets the GPU walk it. Nothing runs per frame, so two
     *           hundred characters glancing at the player cost two hundred writes, not two hundred a
     *           frame. This is what almost everything wants.
     *
     *   Track   follows a target that keeps moving, which no curve can be described in advance for.
     *           Smoothed here and pushed every frame.
     *
     * Everything goes through a MaterialPropertyBlock, which is read before it is written so this and
     * VATAnimator can both live on the same renderer without either wiping the other's state.
     */
    /// <summary>Turns and moves the baked sections on one VAT renderer.</summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class VATSectionDriver : MonoBehaviour
    {

        private const int MAX_SECTIONS = 4;

        private static readonly int[] FROM_ROTATION_IDS = BuildIds("_VATSectionFromRot");
        private static readonly int[] TO_ROTATION_IDS = BuildIds("_VATSectionToRot");
        private static readonly int[] FROM_OFFSET_IDS = BuildIds("_VATSectionFromOff");
        private static readonly int[] TO_OFFSET_IDS = BuildIds("_VATSectionToOff");

        [SerializeField]
        [Tooltip("The clip set the baker generated. Supplies section names and their Max Angle limits.")]
        private VATClipSet clipSet;

        // Every renderer underneath, for the same reason VATAnimator does it: an LOD Group bake has
        // one per level and the group enables one at a time.
        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private VATSectionState[] _states;
        private bool _tracking;
        private HashSet<string> _warned;

        /// <summary>The clip set section names are resolved against.</summary>
        public VATClipSet ClipSet => clipSet;

        /// <summary>Sections this bake left drivable, in channel order.</summary>
        public int SectionCount => clipSet ? clipSet.SectionCount : 0;

        /// <summary>Whether anything is currently following a moving target.</summary>
        public bool IsTracking => _tracking;

        /// <summary>
        /// Stops a section following a target, leaving it wherever it had got to.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        public void StopTracking(string sectionName)
        {
            int index = IndexOf(sectionName);
            if (index < 0) return;

            _states[index].Tracking = false;
        }

        private static int[] BuildIds(string prefix)
        {
            int[] ids = new int[MAX_SECTIONS];
            for (int i = 0; i < MAX_SECTIONS; i++)
                ids[i] = Shader.PropertyToID($"{prefix}{i}");

            return ids;
        }

        private void OnEnable()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            if (!clipSet) clipSet = FindClipSet();

            _states = new VATSectionState[MAX_SECTIONS];
            for (int i = 0; i < MAX_SECTIONS; i++) _states[i] = new VATSectionState();

            // Cleared here so fixing a name and pressing play reports the next problem rather than
            // staying quiet because it warned about this one before the domain reloaded.
            _warned = null;

            ApplyAll();
        }

        // Only while something is actually following a moving target. A finished TurnTo needs no
        // per frame work at all, which is the whole point of describing it to the GPU.
        private void LateUpdate()
        {
            // Tracking needs a frame to happen every frame, which edit mode does not have. The rest of
            // the component still runs there so the inspector can pose a section without entering play.
            if (!_tracking || !Application.isPlaying) return;

            float now = VATTime.Now;
            bool stillTracking = false;

            for (int i = 0; i < MAX_SECTIONS; i++)
            {
                VATSectionState state = _states[i];
                if (!state.Tracking) continue;

                stillTracking = true;

                // Exponential rather than a fixed step, so the follow behaves the same at 30 and 144
                // frames a second instead of snapping harder the faster the game runs.
                float step = 1f - Mathf.Exp(-state.TrackSharpness * Time.deltaTime);
                Quaternion current = Quaternion.Slerp(state.ToRotation, state.TrackTarget, step);

                state.FromRotation = current;
                state.ToRotation = current;
                state.StartTime = now;
                state.Duration = 0f;

            }

            _tracking = stillTracking;
            Push();
        }

        /// <summary>
        /// Turns a section to a rotation over a period, then leaves it there. Nothing runs per frame.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="rotation">Local rotation about the section's baked pivot.</param>
        /// <param name="duration">Seconds to get there. 0 snaps.</param>
        public void TurnTo(string sectionName, Quaternion rotation, float duration)
        {
            Set(sectionName, rotation, Vector3.zero, duration);
        }

        /// <summary>
        /// Turns a section to a rotation given in degrees, over a period.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="euler">Local rotation in degrees about the section's baked pivot.</param>
        /// <param name="duration">Seconds to get there. 0 snaps.</param>
        public void TurnTo(string sectionName, Vector3 euler, float duration)
        {
            Set(sectionName, Quaternion.Euler(euler), Vector3.zero, duration);
        }

        /// <summary>
        /// Sets a section's full local transform over a period.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="rotation">Local rotation about the section's baked pivot.</param>
        /// <param name="offset">Local offset in object space.</param>
        /// <param name="duration">Seconds to get there. 0 snaps.</param>
        public void Set(string sectionName, Quaternion rotation, Vector3 offset, float duration)
        {
            int index = IndexOf(sectionName);
            if (index < 0) return;

            float now = VATTime.Now;
            VATSectionState state = _states[index];

            // Where it is NOW becomes where the new transition starts, so redirecting a turn that is
            // still running bends it toward the new target instead of snapping back to the beginning.
            state.FromRotation = state.RotationAt(now);
            state.FromOffset = state.OffsetAt(now);

            state.ToRotation = Limit(rotation, index);
            state.ToOffset = offset;
            state.StartTime = now;
            state.Duration = Mathf.Max(0f, duration);
            state.Tracking = false;

            Push();
        }

        /// <summary>
        /// Follows a rotation that keeps changing, smoothed. Call it every frame with a fresh target.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="rotation">Where the section should be aiming right now.</param>
        /// <param name="sharpness">How hard it chases. Around 8 reads as a natural head turn.</param>
        public void Track(string sectionName, Quaternion rotation, float sharpness = 8f)
        {
            int index = IndexOf(sectionName);
            if (index < 0) return;

            VATSectionState state = _states[index];

            if (!state.Tracking)
            {
                // Picks up from wherever a TurnTo had got to, rather than jumping to the target.
                Quaternion current = state.RotationAt(VATTime.Now);
                state.FromRotation = current;
                state.ToRotation = current;
            }

            state.Tracking = true;
            state.TrackTarget = Limit(rotation, index);
            state.TrackSharpness = Mathf.Max(.01f, sharpness);
            state.Duration = 0f;
            _tracking = true;
        }

        /// <summary>
        /// Eases a section back to its baked pose.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="duration">Seconds to get back. 0 snaps.</param>
        public void Release(string sectionName, float duration)
        {
            Set(sectionName, Quaternion.identity, Vector3.zero, duration);
        }

        /// <summary>
        /// Aims a section at a point in the world, which is the head-look case.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="worldPoint">Where to look.</param>
        /// <param name="duration">Seconds to get there, or 0 with Track for continuous following.</param>
        public void LookAt(string sectionName, Vector3 worldPoint, float duration)
        {
            TurnTo(sectionName, LookRotation(sectionName, worldPoint), duration);
        }

        /// <summary>
        /// The local rotation that would aim a section at a world point, for feeding to Track.
        /// </summary>
        /// <param name="sectionName">Name given to the section in the baker, matched ignoring case.</param>
        /// <param name="worldPoint">Where to look.</param>
        /// <returns>A local rotation about the section's rest pivot, or identity when there is no such section.</returns>
        public Quaternion LookRotation(string sectionName, Vector3 worldPoint)
        {
            VATSection section = clipSet ? clipSet.Section(sectionName) : null;
            if (section == null) return Quaternion.identity;

            /*
             * Measured from the REST pivot, not the animated one. The animated pivot lives in a texture
             * the CPU never reads, and over the distance a look-at is aimed across the difference is far
             * smaller than the error in guessing where someone is looking anyway.
             */
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint) - section.restPivot;
            if (localPoint.sqrMagnitude < .0001f) return Quaternion.identity;

            return Quaternion.FromToRotation(Vector3.forward, localPoint.normalized);
        }

        /// <summary>
        /// Whether this bake actually has a section of that name.
        /// </summary>
        /// <param name="sectionName">Name to look for, matched ignoring case.</param>
        /// <returns>True when the name resolves to a baked section.</returns>
        public bool Has(string sectionName) => Resolve(sectionName, false) >= 0;

        private int IndexOf(string sectionName) => Resolve(sectionName, true);

        /*
         * A name that does not resolve used to return -1 and the call simply did nothing - no error, no
         * clue. Renaming a section in the baker cannot follow the name into code, so that silence is
         * exactly the case this has to be loud about.
         *
         * Once per name rather than once per call, because Track runs every frame and would otherwise
         * fill the console faster than it could be read.
         */
        private int Resolve(string sectionName, bool warn)
        {
            if (_states == null) return -1;

            if (!clipSet)
            {
                if (warn) Warn(sectionName, "no clip set is assigned to this driver");
                return -1;
            }

            VATSection section = clipSet.Section(sectionName);

            if (section == null)
            {
                if (warn)
                    Warn(sectionName, clipSet.SectionCount > 0
                        ? $"'{clipSet.name}' has: {string.Join(", ", clipSet.SectionNames())}"
                        : $"'{clipSet.name}' was baked without any sections");

                return -1;
            }

            return section.channel >= 0 && section.channel < MAX_SECTIONS ? section.channel : -1;
        }

        private void Warn(string sectionName, string detail)
        {
            if (_warned == null) _warned = new HashSet<string>();
            if (!_warned.Add(sectionName ?? string.Empty)) return;

            Debug.LogWarning(
                $"[VAT] No section called '{sectionName}' on {name}, so the call did nothing. {detail}.",
                this);
        }

        /*
         * Clamped on the total angle from identity rather than per axis, so a limit means the same thing
         * however the rotation was expressed. Slerping back keeps the axis the caller asked for and only
         * shortens the turn.
         */
        private Quaternion Limit(Quaternion rotation, int index)
        {
            VATSection section = SectionAt(index);
            if (section == null || section.maxAngle <= 0f) return rotation;

            float angle = Quaternion.Angle(Quaternion.identity, rotation);
            if (angle <= section.maxAngle) return rotation;

            return Quaternion.Slerp(Quaternion.identity, rotation, section.maxAngle / angle);
        }

        private VATSection SectionAt(int index)
        {
            if (!clipSet) return null;

            for (int i = 0; i < clipSet.SectionCount; i++)
                if (clipSet.sections[i].channel == index) return clipSet.sections[i];

            return null;
        }

        private VATClipSet FindClipSet()
        {
            VATAnimator animator = GetComponent<VATAnimator>();
            return animator ? animator.ClipSet : null;
        }

        private void ApplyAll() => Push();

        /*
         * Read before write. Setting a property block replaces it outright rather than merging, so
         * building a fresh one here would wipe whatever VATAnimator has written and freeze the whole
         * character on clip 0. Reading into the same block every time also keeps this allocation free,
         * which matters because Track pushes every frame.
         */
        private void Push()
        {
            if (_renderers == null || _renderers.Length == 0)
                _renderers = GetComponentsInChildren<Renderer>(true);

            if (_renderers.Length == 0 || _states == null) return;
            if (_block == null) _block = new MaterialPropertyBlock();

            foreach (Renderer renderer in _renderers)
            {
                if (!renderer) continue;

                renderer.GetPropertyBlock(_block);

                for (int i = 0; i < MAX_SECTIONS; i++)
                {
                    VATSectionState state = _states[i];

                    _block.SetVector(FROM_ROTATION_IDS[i], AsVector(state.FromRotation));
                    _block.SetVector(TO_ROTATION_IDS[i], AsVector(state.ToRotation));

                    // The timing rides in the w of the offsets, which the shader unpacks the same way.
                    _block.SetVector(FROM_OFFSET_IDS[i], new Vector4(
                        state.FromOffset.x, state.FromOffset.y, state.FromOffset.z, state.StartTime));
                    _block.SetVector(TO_OFFSET_IDS[i], new Vector4(
                        state.ToOffset.x, state.ToOffset.y, state.ToOffset.z, state.Duration));
                }

                renderer.SetPropertyBlock(_block);
            }
        }

        private static Vector4 AsVector(Quaternion q) => new Vector4(q.x, q.y, q.z, q.w);

    }
}
