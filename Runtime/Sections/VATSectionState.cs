using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * What one section is doing, mirrored on the CPU so a transition can be redirected halfway through.
     *
     * The GPU is told where a turn starts, where it ends, when it began and how long it takes, and then
     * walks the curve on its own. Retargeting cannot simply restart that timer - the section would snap
     * back to where it began - so the driver evaluates the same curve here, uses the result as the new
     * starting pose, and the redirect comes out continuous.
     */
    /// <summary>One section's transition, as both ends of it plus when it runs.</summary>
    internal class VATSectionState
    {

        public Quaternion FromRotation = Quaternion.identity;
        public Quaternion ToRotation = Quaternion.identity;

        public Vector3 FromOffset;
        public Vector3 ToOffset;

        public float StartTime;
        public float Duration;

        // Set by Track. The GPU is handed a finished transition every frame instead of a running one,
        // because following something that moves is not a curve anyone can describe in advance.
        public bool Tracking;
        public Quaternion TrackTarget = Quaternion.identity;
        public float TrackSharpness = 8f;

        /// <summary>How far through the transition it is, matching VAT_ApplyTimedSection exactly.</summary>
        public float Progress(float now)
        {
            if (Duration <= 0f) return 1f;

            float t = Mathf.Clamp01((now - StartTime) / Duration);
            return t * t * (3f - (2f * t));
        }

        /// <summary>The pose right now, which is what a redirect has to start from.</summary>
        public Quaternion RotationAt(float now)
        {
            return Quaternion.Slerp(FromRotation, ToRotation, Progress(now));
        }

        /// <summary>The offset right now, for the same reason.</summary>
        public Vector3 OffsetAt(float now)
        {
            return Vector3.Lerp(FromOffset, ToOffset, Progress(now));
        }

    }
}
