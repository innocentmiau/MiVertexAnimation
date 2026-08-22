using UnityEngine;

namespace MiVertexAnimation
{

    /// <summary>
    /// One clip's slice in the texture array, as the bake actually wrote it rather than as the
    /// source clip describes itself.
    /// </summary>
    internal class VATClipBake
    {

        public AnimationClip Clip;
        public int StartFrame;
        public int Frames;
        public float Rate;

        // Kept per slice rather than read off the window, because clips can be stepped differently
        // and the frame loop has to sample each one at its own stride.
        public int Step = 1;

        // Reported in the bake log, not acted on. Trimmed means the duplicate loop frame was
        // dropped, Duplicates counts interior frames that repeat the one before them.
        public bool Trimmed;
        public int Duplicates;

    }
}
