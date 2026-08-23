using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * A bake produces one of these per output set rather than per renderer, because the three renderer
     * modes group renderers differently: one part holding one renderer, one part each, or a single part
     * holding all of them. Everything downstream of the frame loop only has to understand parts.
     */
    /// <summary>
    /// One output set from a bake: its own texture pair, materials, mesh and prefab entry.
    /// </summary>
    internal class VATPartBake
    {

        public string Name;
        public readonly List<SkinnedMeshRenderer> Targets = new List<SkinnedMeshRenderer>();
        public Mesh SourceMesh;

        // One per LOD Group level, each the full vertex buffer with a single level's triangles.
        // Null when this bake is not writing a group.
        public Mesh[] LodMeshes;

        // One name per submesh, in the order BuildCombinedMesh emits them, so the generated
        // materials can be told apart at a glance.
        public readonly List<string> SlotNames = new List<string>();
        public Material[] Materials;

        public Bounds Bounds;
        public int VertexCount;
        public int RowsPerFrame;
        public int TextureHeight;

        public Color[] Positions;
        public Color[] Normals;

        // Kept from the mesh write so the frame loop can measure how far each section reaches, which
        // is what the culling bounds have to be padded by.
        public Vector4[] SectionMasks;
        public Vector3 Min;
        public Vector3 Max;

    }
}
