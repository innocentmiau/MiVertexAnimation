using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Every timestamp this package writes into a material is compared against _Time.y inside the shader,
     * so both have to be read off the same clock or the comparison means nothing.
     * URP fills _Time from Application.isPlaying ? Time.time : Time.realtimeSinceStartup, in ScriptableRenderer,
     * and this mirrors that exactly.
     *
     * Time.timeSinceLevelLoad is the one that looks right and is not.
     * It agrees with Time.time only in the first scene of a run, which is the scene anyone tests in the editor,
     * and then resets to zero on every scene load after that while _Time.y carries on counting from application start.
     * A build that opens on a menu and loads the game from there hands the shader clip start times
     * a whole menu's worth of seconds in the past, so one-shots arrive already holding their last frame,
     * cross-fades arrive already finished, section turns snap, and every clip event fires against a pose
     * that is not the one on screen. Loops survive it, because frac() hides a wrong phase,
     * which is what kept this out of sight until a build with more than one scene in it.
     */
    /// <summary>
    /// The clock the VAT shaders run on, which is whatever the render pipeline last wrote into _Time.y.
    /// Anything compared against _Time.y in a shader has to be timestamped with this.
    /// </summary>
    public static class VATTime
    {

        /// <summary>Seconds on the shader's clock, counting from application start rather than from the current scene.</summary>
#if UNITY_EDITOR
        public static float Now => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#else
        public static float Now => Time.time;
#endif

    }
}
