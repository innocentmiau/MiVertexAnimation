using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * One level of an LOD Group bake.
     *
     * Unity 6 Mesh LOD stores its levels as extra index buffers over ONE vertex buffer, so a decimated
     * level reuses the very same vertices. That is what makes this cheap: every level keeps the full
     * vertex buffer, SV_VertexID still addresses the same texel, and one texture set serves the lot.
     * Only the triangles differ.
     */
    /// <summary>A source Mesh LOD level, and the screen size it takes over at.</summary>
    [Serializable]
    public class VATLodLevel
    {

        [Tooltip("Which of the source mesh's Mesh LOD levels this uses. 0 is full detail.")]
        public int level;

        [Tooltip("Fraction of screen height below which the next level takes over. On the last level " +
                 "this is where the object stops drawing, and 0 means it never does.")]
        [Range(0f, 1f)]
        public float screenPercentage = .5f;

    }
}
