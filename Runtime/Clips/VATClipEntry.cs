using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /// <summary>
    /// One baked clip's slice in a VAT texture array: what it is called, how many frames it holds,
    /// how fast it plays, and the markers that fire while it runs.
    /// </summary>
    [Serializable]
    public struct VATClipEntry
    {

        [Tooltip("The source AnimationClip's name. This is what Play(string) matches against.")]
        public string name;

        [Tooltip("Frames actually written into this slice, after frame stepping and loop trimming.")]
        public int frames;

        [Tooltip("Frames per second the shader plays this slice at.")]
        public float frameRate;

        [Tooltip("Seconds one cycle takes, which is frames divided by frameRate.")]
        public float length;

        [Tooltip("Markers that fire while this clip plays.")]
        public VATClipEvent[] events;

    }
}
