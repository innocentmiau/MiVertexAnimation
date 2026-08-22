namespace MiVertexAnimation
{

    /// <summary>
    /// How a source object's SkinnedMeshRenderers are turned into baked output.
    /// </summary>
    public enum VATRendererMode
    {
        SELECTED, // bake one renderer and ignore the rest, which is also how each LODGroup level is baked
        SEPARATE_PARTS, // one texture pair and material per renderer, assembled as children of a single prefab
        COMBINED_MESH // every renderer merged into one mesh and one texture pair, cheapest in memory but loses Mesh LOD
    }
}
