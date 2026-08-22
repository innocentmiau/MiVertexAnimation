using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The driver's API is meant to be called from gameplay code, which leaves nothing to grab hold of
     * when all you want is to see whether a head turns the way you hoped. This is that: live controls
     * per baked section, working in edit mode as well as play mode.
     *
     * In edit mode there is no timeline for a transition to run along, so the fields simply pose the
     * section as they are dragged and everything that describes a transition is disabled. Without that
     * split, Live and Turn To are two controls doing exactly the same thing.
     */
    /// <summary>Inspector for VATSectionDriver, with live controls for every baked section.</summary>
    [CustomEditor(typeof(VATSectionDriver))]
    public class VATSectionDriverEditor : Editor
    {

        private Vector3[] _rotations;
        private Vector3[] _offsets;
        private float[] _durations;
        private bool[] _live;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            VATSectionDriver driver = (VATSectionDriver)target;
            VATClipSet clips = driver.ClipSet;

            EditorGUILayout.Space(6f);

            if (!clips)
            {
                EditorGUILayout.HelpBox(
                    "No clip set assigned, so section names cannot be resolved. The baker assigns this " +
                    "automatically on prefabs it writes.", MessageType.Info);
                return;
            }

            if (clips.SectionCount == 0)
            {
                EditorGUILayout.HelpBox(
                    $"'{clips.name}' was baked without any sections, so there is nothing to drive. Add " +
                    "one in the baker's Sections panel and bake again.", MessageType.Info);
                return;
            }

            EnsureBuffers(clips.SectionCount);

            EditorGUILayout.LabelField("Sections", EditorStyles.boldLabel);

            for (int i = 0; i < clips.SectionCount; i++) DrawSection(driver, clips.sections[i], i);

            if (!Application.isPlaying)
                EditorGUILayout.HelpBox(
                    "Not in play mode. Rotation and Offset pose the section directly as you drag them. " +
                    "Duration, Live and Turn To all describe a transition over time and need frames to " +
                    "run, so they wait for play mode.",
                    MessageType.None);
        }

        private void EnsureBuffers(int count)
        {
            if (_rotations != null && _rotations.Length == count) return;

            _rotations = new Vector3[count];
            _offsets = new Vector3[count];
            _durations = new float[count];
            _live = new bool[count];

            for (int i = 0; i < count; i++) _durations[i] = .35f;
        }

        private void DrawSection(VATSectionDriver driver, VATSection section, int index)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    $"{section.name}",
                    $"channel {section.channel}   pivot {section.pivotBone}" +
                    (section.maxAngle > 0f ? $"   max {section.maxAngle:0.#} deg" : "   no limit"));

                bool playing = Application.isPlaying;

                EditorGUI.BeginChangeCheck();

                _rotations[index] = EditorGUILayout.Vector3Field("Rotation", _rotations[index]);
                _offsets[index] = EditorGUILayout.Vector3Field("Offset", _offsets[index]);

                bool dragged = EditorGUI.EndChangeCheck();

                // Both of these describe a transition running over time, and edit mode has no frames to
                // run one along. Left visible but disabled, because hiding them would move everything
                // below whenever play mode is entered.
                using (new EditorGUI.DisabledScope(!playing))
                {
                    _durations[index] = EditorGUILayout.Slider("Duration", _durations[index], 0f, 2f);
                    _live[index] = EditorGUILayout.Toggle(
                        new GUIContent("Live", "Apply the values above as they are dragged, with no " +
                                               "transition. Outside play mode this is the only thing " +
                                               "that can happen, so it is always on."),
                        playing ? _live[index] : true);
                }

                // Outside play mode the fields always apply, or there would be no way to pose anything.
                if (dragged && (!playing || _live[index])) Apply(driver, section.name, index, 0f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!playing))
                    {
                        if (GUILayout.Button("Turn To"))
                            Apply(driver, section.name, index, _durations[index]);
                    }

                    if (GUILayout.Button("Release"))
                    {
                        driver.StopTracking(section.name);
                        driver.Release(section.name, playing ? _durations[index] : 0f);

                        _rotations[index] = Vector3.zero;
                        _offsets[index] = Vector3.zero;
                    }
                }
            }
        }

        private void Apply(VATSectionDriver driver, string sectionName, int index, float duration)
        {
            driver.StopTracking(sectionName);
            driver.Set(sectionName, Quaternion.Euler(_rotations[index]), _offsets[index], duration);
        }

    }
}
