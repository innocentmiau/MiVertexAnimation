using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * A snapshot of everything in the baker window that a person can change, and nothing else.
     * Scroll positions, foldouts, the preview's orbit and which event row is expanded are all left out:
     * undoing a frame step should not also throw the camera back where it was ten seconds ago.
     *
     * The baker's state lives in plain window fields rather than on an asset, which is why this exists
     * at all. Unity's Undo records UnityEngine.Objects, so recording the window itself would mean
     * serializing every one of those fields and would put the baker's edits on the editor's one global
     * stack, mixed in with scene and asset changes. Keeping the stack here is what makes Ctrl+Z inside
     * the baker mean "undo what I just did in the baker".
     */
    /// <summary>
    /// One entry on the baker window's undo stack: every setting that decides what gets baked.
    /// </summary>
    [Serializable]
    internal class VATBakerState
    {

        public GameObject target;
        public int rendererMode;
        public int rendererIndex;

        public List<AnimationClip> bakeClips = new List<AnimationClip>();
        public AnimationClip explicitClip;
        public AnimationClip frameRangeClip;
        public int clipIndex;

        public int startFrame;
        public int endFrame;
        public int frameStep;
        public bool trimLoopFrame;
        public float blendDuration;

        public bool sectionsEnabled;
        public List<VATSectionSetup> sections = new List<VATSectionSetup>();

        public bool perClipRanges;
        public List<VATClipRange> clipRanges = new List<VATClipRange>();

        public bool removeRootMotion;
        public int rootIndex;
        public bool lockRootX;
        public bool lockRootY;
        public bool lockRootZ;

        public int textureWidth;
        public bool bakeNormals;
        public int positionPrecision;
        public int normalPrecision;
        public int frameQuality;
        public float stepTolerance;

        public string outputPath;
        public string fileName;
        public bool createMaterial;
        public Shader materialShader;
        public bool lodGroup;
        public List<VATLodLevel> lodLevels = new List<VATLodLevel>();
        public bool restPoseMesh;
        public bool createPrefab;
        public bool frameBlend;
        public bool updateExisting;
        public bool saveSettings;

        public List<VATAuthoredClipEvents> authoredEvents = new List<VATAuthoredClipEvents>();

        /// <summary>
        /// Whether two snapshots describe the same bake, which is how the window decides there is
        /// anything worth putting on the stack.
        /// </summary>
        /// <param name="other">The snapshot to compare against, which may be null.</param>
        /// <returns>True when every setting matches, events included.</returns>
        public bool Matches(VATBakerState other)
        {
            if (other == null) return false;

            bool sameBake = target == other.target
                            && rendererMode == other.rendererMode
                            && rendererIndex == other.rendererIndex
                            && explicitClip == other.explicitClip
                            && frameRangeClip == other.frameRangeClip
                            && clipIndex == other.clipIndex
                            && startFrame == other.startFrame
                            && endFrame == other.endFrame
                            && frameStep == other.frameStep
                            && trimLoopFrame == other.trimLoopFrame
                            && perClipRanges == other.perClipRanges

                            && Mathf.Approximately(blendDuration, other.blendDuration);

            if (!sameBake) return false;
            if (!RangesMatch(other)) return false;
            if (sectionsEnabled != other.sectionsEnabled) return false;
            if (!SectionsMatch(other)) return false;
            if (!LodLevelsMatch(other)) return false;

            bool sameRoot = removeRootMotion == other.removeRootMotion
                            && rootIndex == other.rootIndex
                            && lockRootX == other.lockRootX
                            && lockRootY == other.lockRootY
                            && lockRootZ == other.lockRootZ
                            && textureWidth == other.textureWidth
                            && bakeNormals == other.bakeNormals
                            && positionPrecision == other.positionPrecision
                            && normalPrecision == other.normalPrecision
                            && frameQuality == other.frameQuality
                            && Mathf.Approximately(stepTolerance, other.stepTolerance);

            if (!sameRoot) return false;

            bool sameOutput = outputPath == other.outputPath
                              && fileName == other.fileName
                              && createMaterial == other.createMaterial
                              && materialShader == other.materialShader
                              && restPoseMesh == other.restPoseMesh
                              && createPrefab == other.createPrefab
                              && frameBlend == other.frameBlend
                              && updateExisting == other.updateExisting
                              && saveSettings == other.saveSettings;

            if (!sameOutput) return false;

            if (bakeClips.Count != other.bakeClips.Count) return false;

            for (int i = 0; i < bakeClips.Count; i++)
                if (bakeClips[i] != other.bakeClips[i]) return false;

            return EventsMatch(other);
        }

        private bool LodLevelsMatch(VATBakerState other)
        {
            if (lodGroup != other.lodGroup) return false;
            if (lodLevels.Count != other.lodLevels.Count) return false;

            for (int i = 0; i < lodLevels.Count; i++)
            {
                if (lodLevels[i].level != other.lodLevels[i].level) return false;
                if (!Mathf.Approximately(lodLevels[i].screenPercentage,
                        other.lodLevels[i].screenPercentage)) return false;
            }

            return true;
        }

        private bool SectionsMatch(VATBakerState other)
        {
            if (sections.Count != other.sections.Count) return false;

            for (int i = 0; i < sections.Count; i++)
            {
                VATSectionSetup mine = sections[i];
                VATSectionSetup theirs = other.sections[i];

                if (mine.name != theirs.name || mine.boneName != theirs.boneName) return false;
                if (mine.priority != theirs.priority) return false;
                if (!Mathf.Approximately(mine.falloff, theirs.falloff)) return false;
                if (mine.pivotOffset != theirs.pivotOffset) return false;
                if (!Mathf.Approximately(mine.maxAngle, theirs.maxAngle)) return false;
            }

            return true;
        }

        private bool RangesMatch(VATBakerState other)
        {
            if (clipRanges.Count != other.clipRanges.Count) return false;

            for (int i = 0; i < clipRanges.Count; i++)
            {
                VATClipRange mine = clipRanges[i];
                VATClipRange theirs = other.clipRanges[i];

                if (mine.clip != theirs.clip || mine.clipName != theirs.clipName) return false;
                if (mine.startFrame != theirs.startFrame || mine.endFrame != theirs.endFrame) return false;
                if (mine.frameStep != theirs.frameStep || mine.trimLoopFrame != theirs.trimLoopFrame) return false;
            }

            return true;
        }

        private bool EventsMatch(VATBakerState other)
        {
            if (authoredEvents.Count != other.authoredEvents.Count) return false;

            for (int i = 0; i < authoredEvents.Count; i++)
            {
                VATAuthoredClipEvents mine = authoredEvents[i];
                VATAuthoredClipEvents theirs = other.authoredEvents[i];

                if (mine.clip != theirs.clip || mine.clipName != theirs.clipName) return false;
                if (mine.authored != theirs.authored) return false;
                if (mine.authoredStartFrame != theirs.authoredStartFrame) return false;
                if (mine.events.Count != theirs.events.Count) return false;

                for (int e = 0; e < mine.events.Count; e++)
                    if (!EventMatches(mine.events[e], theirs.events[e])) return false;
            }

            return true;
        }

        private static bool EventMatches(VATClipEvent a, VATClipEvent b)
        {
            return a.name == b.name
                   && Mathf.Approximately(a.normalizedTime, b.normalizedTime)
                   && a.stringParameter == b.stringParameter
                   && Mathf.Approximately(a.floatParameter, b.floatParameter)
                   && a.intParameter == b.intParameter;
        }

    }
}
