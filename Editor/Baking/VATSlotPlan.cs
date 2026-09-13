using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Worked out once, before anything is written, and then read by everything that writes: the mesh
     * builder needs to know which slot a submesh's triangles go into, the material writer needs one
     * name per slot, and the window needs both before a bake so that a merge which flattens two
     * different looks can be seen rather than discovered in the output.
     */
    /// <summary>
    /// The material slots one part of a bake will write, and which of its submeshes land in each.
    /// </summary>
    internal class VATSlotPlan
    {

        public readonly List<string> Names = new List<string>();

        // The material each slot was opened for, which is what the preview draws that slot with.
        public readonly List<Material> Representatives = new List<Material>();

        // Every source material that landed in each slot, so a merge across materials that do not
        // match can name the ones it flattened.
        public readonly List<List<Material>> Members = new List<List<Material>>();

        // One entry per source submesh, in the order the mesh builder walks them, indexing Names.
        public readonly List<int> SlotOf = new List<int>();

        /// <summary>How many source submeshes were planned, which is what the slot count would be unmerged.</summary>
        public int SubMeshCount => SlotOf.Count;

        /// <summary>How many material slots the bake will actually write.</summary>
        public int Count => Names.Count;

        /// <summary>How many submeshes landed in one slot.</summary>
        /// <param name="slot">Slot index.</param>
        /// <returns>The number of source submeshes drawn by that slot.</returns>
        public int SubMeshesIn(int slot)
        {
            int count = 0;
            foreach (int of in SlotOf)
                if (of == slot) count++;

            return count;
        }

    }
}
