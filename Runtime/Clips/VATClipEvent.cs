using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Copied out of the source AnimationClip at bake time rather than read from it at runtime.
     * A baked VAT has no Animator and no AnimationClip behind it, only a texture and a clip index,
     * so a marker left on the clip asset would never fire on anything the baker produced.
     *
     * The time is normalized rather than in seconds because the baker retimes clips.
     * Frame stepping and loop trimming both change how long a baked clip runs for,
     * and a marker stored in seconds would drift off its pose every time one of those settings moved.
     */
    /// <summary>
    /// A marker at a point inside a baked clip, raised through VATAnimator.ClipEventFired.
    /// </summary>
    [Serializable]
    public struct VATClipEvent
    {

        [Tooltip("Identifier passed to listeners. Comes from the source event's function name.")]
        public string name;

        [Range(0f, 1f)]
        [Tooltip("Where in the clip it fires. 0 is the first frame, 1 the last.")]
        public float normalizedTime;

        public string stringParameter;
        public float floatParameter;
        public int intParameter;

    }
}
