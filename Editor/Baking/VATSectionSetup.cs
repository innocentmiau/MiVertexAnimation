using System;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The bake-time half of a section. The bone is stored by NAME rather than by index into
     * SkinnedMeshRenderer.bones, so a settings asset still resolves after the rig is reimported or
     * pointed at a different character with the same skeleton.
     */
    /// <summary>
    /// One section as configured in the baker, before it becomes a mask and a VATSection.
    /// </summary>
    [Serializable]
    public class VATSectionSetup
    {

        [Tooltip("What gameplay code will call this section.")]
        public string name = "Section";

        [Tooltip("The section is this bone and every bone parented under it.")]
        public string boneName;

        [Tooltip("Higher wins the vertices two sections both claim. Ties fall back to list order.")]
        public int priority;

        [Tooltip("Shapes the rig's falloff without changing what the section covers. Above 1 pulls the " +
                 "blend toward the section's core, below 1 spreads it further out.")]
        public float falloff = 1f;

        [Tooltip("Nudges the pivot in object space, for when the joint is not quite where the hinge " +
                 "should sit.")]
        public Vector3 pivotOffset;

        [Tooltip("Largest rotation the runtime will apply, in degrees. 0 means no limit.")]
        public float maxAngle;

    }
}
