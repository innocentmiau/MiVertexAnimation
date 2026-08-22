using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * None of this is recoverable from the generated assets. Clip order could be read back off a
     * VATClipSet, but frame step, root axes, renderer mode and texture width could not,
     * so it has to be written at bake time or it is gone for good.
     *
     * Editor-only on purpose: it lives in the editor assembly, so it can never be pulled into a build
     * the way a runtime ScriptableObject referenced by a prefab would be.
     */
    /// <summary>
    /// Everything the VAT Baker window needs to reproduce a bake exactly: the same clips in the same
    /// order, the same frame range, the same root handling and the same output paths.
    /// </summary>
    public class VATBakeSettings : ScriptableObject
    {

        public const int CURRENT_VERSION = 3;

        [HideInInspector] public int version = CURRENT_VERSION;

        [Header("Source")]
        public GameObject target;
        public int rendererMode;
        public int rendererIndex;
        public List<AnimationClip> clips = new List<AnimationClip>();
        public AnimationClip explicitClip;

        [Header("Animation")]
        public int startFrame;
        public int endFrame = 1;
        public int frameStep = 1;
        public bool trimLoopFrame = true;
        public float blendDuration = .15f;

        [Header("Root Motion")]
        public bool removeRootMotion = true;
        public int rootIndex;
        public bool lockRootX = true;
        public bool lockRootY;
        public bool lockRootZ = true;

        [Header("Per-Clip Ranges")]
        public bool sectionsEnabled;
        public List<VATSectionSetup> sections = new List<VATSectionSetup>();

        public bool perClipRanges;
        public List<VATClipRange> clipRanges = new List<VATClipRange>();

        [Header("Events")]
        public List<VATAuthoredClipEvents> authoredEvents = new List<VATAuthoredClipEvents>();

        [Header("Texture")]
        public int textureWidth = 1024;
        public bool bakeNormals = true;
        // Version 1 only. Read when an old settings asset is loaded, to work out what its Compact
        // Normals checkbox meant, and never written again.
        [HideInInspector] public bool compactNormals = true;
        public int positionPrecision = (int)VATPositionPrecision.NORMALIZED;
        public int normalPrecision = (int)VATNormalPrecision.OCTAHEDRAL;
        public int frameQuality = (int)VATFrameQuality.BALANCED;
        public float stepTolerance = .002f;

        [Header("Output")]
        public string outputPath = "Assets/VAT";
        public string fileName = string.Empty;
        public bool createMaterial = true;
        public Shader materialShader;
        public bool createPrefab = true;
        public bool frameBlend = true;
        public bool updateExisting = true;

    }
}
