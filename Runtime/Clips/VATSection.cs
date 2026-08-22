using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * A section is one region of the baked mesh that can still be moved after the bake: a head that
     * turns, a torso that leans, a weapon arm that recoils. It exists because a VAT is otherwise
     * frozen - every vertex is exactly where the texture says and nothing can react to anything.
     *
     * The region itself is not stored here. It lives as a per-vertex weight in one channel of the
     * mesh's UV3, written by the baker from the rig's own skin weights, so the falloff down a neck or
     * a waist is the one the rigger painted rather than anything this package invented.
     *
     * What is stored here is everything a script needs to address that region by name instead of by
     * channel number, which is the same reason VATClipSet stores clip names.
     */
    /// <summary>
    /// One baked mesh section: which UV1 channel holds its weights, and what it turns on.
    /// </summary>
    [Serializable]
    public class VATSection
    {

        [Tooltip("What gameplay code calls this section. Matched case-insensitively.")]
        public string name;

        [Tooltip("Which component of UV3 holds this section's per-vertex weight, 0 to 3.")]
        public int channel;

        [Tooltip("Bone the section turns on. Recorded so a re-bake can rebuild the same mask.")]
        public string pivotBone;

        [Tooltip("Pivot at the rest pose, in object space. The animated one lives in the pivot texture.")]
        public Vector3 restPivot;

        [Tooltip("Higher priority wins the vertices two sections both claim.")]
        public int priority;

        [Tooltip("Largest rotation to apply, in degrees. 0 means no limit.")]
        public float maxAngle;

    }
}
