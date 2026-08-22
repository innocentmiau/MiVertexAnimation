namespace MiVertexAnimation
{

    /*
     * How a baked normal is stored.
     *
     * A normal is a direction, not a point: it has only two degrees of freedom, and storing it as three
     * numbers spends a third of every texel restating what the other two already said. Octahedral
     * encoding folds the sphere onto a square and keeps two channels, which is what lets sixteen bits
     * each fit in the same four bytes that three eight-bit channels took - and land about a hundred
     * times closer.
     */
    /// <summary>Storage precision for baked vertex normals.</summary>
    public enum VATNormalPrecision
    {
        OCTAHEDRAL, // two 16-bit channels, 4 bytes. Around 0.001 degrees
        BYTE, // three 8-bit channels, 4 bytes. Around 0.17 degrees, and what Compact Normals used to mean
        HALF // three 16-bit floats, 8 bytes. Around 0.02 degrees, for reading bakes made before the rest existed
    }
}
