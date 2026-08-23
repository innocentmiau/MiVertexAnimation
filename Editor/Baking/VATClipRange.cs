using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Slices of a texture array must all be the same size, so every clip pads up to the longest one.
     * A forty frame idle baked beside an eleven frame run therefore costs four slices of forty frames
     * for eighty-six frames of animation, and most of that texture holds nothing.
     *
     * Stepping the idle by three and leaving the attack at one both shrinks it and shortens the clip
     * that everything else is padding up to, which is why this is worth having per clip rather than once
     * for the whole bake. The shader already supports it: _VATClipData stores frames and rate per clip,
     * so every slice can run at its own length and its own speed with no change to the sampling at all.
     *
     * Keyed by clip reference, not by name. Two FBX files can each export an "Idle", and a name key made
     * them share one of these: editing either edited both, and a rename orphaned the lot.
     */
    /// <summary>
    /// How much of one clip gets baked, and at what resolution.
    /// </summary>
    [Serializable]
    public class VATClipRange
    {

        [Tooltip("The source AnimationClip these numbers belong to.")]
        public AnimationClip clip;

        [Tooltip("Display label only. Two clips can share a name, so the reference above is the key.")]
        public string clipName;

        [Tooltip("First source frame to bake.")]
        public int startFrame;

        [Tooltip("Last source frame to bake.")]
        public int endFrame = 1;

        [Tooltip("Bake every Nth frame of this clip.")]
        public int frameStep = 1;

        [Tooltip("Drop this clip's last frame when it repeats the first.")]
        public bool trimLoopFrame = true;

        /// <summary>Frames this range actually writes into its slice, before loop trimming.</summary>
        public int Frames => ((Mathf.Max(endFrame - startFrame, 0)) / Mathf.Max(frameStep, 1)) + 1;

    }
}
