namespace MiVertexAnimation
{

    /*
     * Only ever an argument to Auto Frame Step, never a thing the bake reads. Not pressing the button is
     * what "lossless" means here, so there is no option for it: an entry that greys out the control next
     * to it says less than an empty list does.
     */
    /// <summary>
    /// How much error Auto Frame Step may introduce when it decides how many frames a clip keeps.
    /// </summary>
    public enum VATFrameQuality
    {
        PRECISE, // barely a twentieth of a percent of the model, for anything inspected up close
        BALANCED, // a fifth of a percent, which does not read as wrong at normal viewing distance
        AGGRESSIVE, // a full percent, invisible on a crowd at distance and the cheapest of the three
        CUSTOM // the tolerance is whatever you set it to
    }
}
