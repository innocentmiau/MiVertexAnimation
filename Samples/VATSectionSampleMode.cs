namespace MiVertexAnimation
{

    /// <summary>Which of the sample behaviours VATSectionSample runs.</summary>
    public enum VATSectionSampleMode
    {
        LOOK_AT, // follows a transform every frame, the case that has to be driven from the CPU
        GLANCE, // looks somewhere at random every few seconds, then back
        RECOIL, // a sharp kick and a slow settle, fired on demand
        SWAY // a continuous drift, written every frame with no duration
    }
}
