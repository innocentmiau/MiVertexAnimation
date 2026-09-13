namespace MiVertexAnimation
{

    /*
     * NONE is first so that it is the zero value. A bake settings asset written before this existed
     * has no field to read, deserializes to zero, and so reproduces exactly the bake it recorded.
     */
    /// <summary>
    /// How many material slots a baked mesh keeps, when several of its submeshes could share one.
    /// </summary>
    public enum VATSlotMerge
    {
        NONE, // one slot per submesh, exactly as the source model is built
        SAME_MATERIAL, // submeshes pointing at the same material asset share a slot
        IDENTICAL, // also merges separate materials that draw the same, every property included
        ALL // one slot for the whole mesh, whatever the source materials were
    }
}
