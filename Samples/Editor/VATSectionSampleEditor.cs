using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Sections are addressed by name, and a name typed into a text field goes stale the moment someone
     * renames that section in the baker - silently, because a name that does not resolve is simply a
     * call that does nothing. A dropdown of what was actually baked removes the whole class of mistake
     * for anything stored in a scene or a prefab.
     *
     * A value that no longer matches is kept rather than quietly replaced, and called out, because
     * overwriting it would destroy the only evidence of what it used to point at.
     */
    /// <summary>Inspector for VATSectionSample, picking sections from what the bake actually wrote.</summary>
    [CustomEditor(typeof(VATSectionSample))]
    public class VATSectionSampleEditor : Editor
    {

        private const string ANY_SECTION = "(first baked section)";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VATSectionSample sample = (VATSectionSample)target;
            VATSectionDriver driver = sample.GetComponent<VATSectionDriver>();
            VATClipSet clips = driver ? driver.ClipSet : null;

            DrawSectionPicker(serializedObject.FindProperty("sectionName"), clips);
            DrawPropertiesExcluding(serializedObject, "m_Script", "sectionName");

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSectionPicker(SerializedProperty property, VATClipSet clips)
        {
            if (!clips || clips.SectionCount == 0)
            {
                EditorGUILayout.PropertyField(property, new GUIContent("Section"));
                EditorGUILayout.HelpBox(
                    "No baked sections to choose from. Bake with a section in the baker's Sections panel.",
                    MessageType.Info);
                return;
            }

            string[] baked = clips.SectionNames();
            string current = property.stringValue;

            int index = string.IsNullOrEmpty(current) ? 0 : System.Array.IndexOf(baked, current) + 1;
            bool stale = !string.IsNullOrEmpty(current) && index == 0;

            string[] options = new string[baked.Length + (stale ? 2 : 1)];
            options[0] = ANY_SECTION;
            for (int i = 0; i < baked.Length; i++) options[i + 1] = baked[i];

            if (stale)
            {
                options[options.Length - 1] = $"{current}  (not baked)";
                index = options.Length - 1;
            }

            int picked = EditorGUILayout.Popup(
                new GUIContent("Section", "Which baked section to drive."), index, options);

            if (picked != index)
                property.stringValue = picked == 0 ? string.Empty : baked[picked - 1];

            if (stale)
                EditorGUILayout.HelpBox(
                    $"'{current}' is not in this bake. It was probably renamed or removed in the baker. " +
                    "Pick one from the list, or the component will do nothing.",
                    MessageType.Warning);
        }

    }
}
