using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Two full vertex slots rather than one, because the preview has to show frame blending honestly.
     * Sampling the clip at an in-between time would interpolate bone rotations along arcs and look
     * smoother than the bake ever will, hiding exactly the artefacts a high Frame Step introduces.
     * So both neighbouring frames are captured and the vertices are lerped, which is what the shader does.
     */
    /// <summary>
    /// A plain mesh standing in for one SkinnedMeshRenderer in the baker's preview, drawn instead of
    /// the rig so the preview can show interpolated vertices rather than only whole frames.
    /// </summary>
    internal class VATPreviewPart
    {

        public SkinnedMeshRenderer Source;
        public Mesh Display;

        public Vector3[] VerticesA;
        public Vector3[] VerticesB;
        public Vector3[] NormalsA;
        public Vector3[] NormalsB;

        public Vector3[] Blended;
        public Vector3[] BlendedNormals;

        // The source mesh's own normals, used when Bake Normals is off. Taken unrebased, because that
        // is exactly what the vertex shader gets: the mesh's normal in the mesh's own space.
        public Vector3[] BindNormals;

        // Triangles, UVs and submeshes never change, so they are written once and left alone.
        public bool TopologyReady;

        // Section weights, cached against a fingerprint of the section list rather than rebuilt every
        // frame. Both the highlight colours and the preview's test drive read them.
        public Vector4[] SectionWeights;
        public Color[] HighlightColors;
        public string SectionKey;
        public bool Highlighted;

    }
}
