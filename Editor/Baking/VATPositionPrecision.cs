namespace MiVertexAnimation
{

    /*
     * How a baked vertex position is stored, which decides how finely it can be placed.
     *
     * A half float spends its bits on an exponent a character never uses: on a two metre rig the step
     * between representable positions is around half a millimetre, and every vertex snaps to its own
     * grid independently of its neighbours. Close up that reads as the surface swimming.
     */
    /// <summary>Storage precision for baked vertex positions.</summary>
    public enum VATPositionPrecision
    {
        HALF, // 16-bit float, positions stored raw. What every bake made before this used
        NORMALIZED, // 16-bit fixed point across the bake's own bounds. Same size, far finer
        FLOAT // 32-bit float. Exact, and twice the texture
    }
}
