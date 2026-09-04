using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The default inspector draws this asset as a raw array, which gets one thing badly wrong: frames,
     * frame rate and length describe the texture that was baked, and editing any of them here changes
     * nothing about the texture and quietly desynchronises playback from it. They are shown but locked.
     *
     * The name is the opposite case. It is the only field in the asset that means nothing to the shader
     * and everything to gameplay code, so it is the one thing worth being able to fix after a bake,
     * without re-baking a texture to change a string.
     */
    /// <summary>
    /// The baked clip set inspector: renameable slice names, the numbers they were baked with, and their events.
    /// </summary>
    [CustomEditor(typeof(VATClipSet))]
    public class VATClipSetEditor : UnityEditor.Editor
    {

        private const string RENAME_NOTE = "These names are what Play() matches. Renaming one here changes " +
                                           "this asset only: the next bake writes whatever the baker's clip " +
                                           "list says, so make the same change there to keep it. Any script " +
                                           "or Clip Finished binding still using the old name stops matching.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty clips = serializedObject.FindProperty("clips");

            if (clips.arraySize == 0)
            {
                EditorGUILayout.HelpBox(
                    "No clips in this set. It is written by the VAT Baker, one slice per baked clip.",
                    MessageType.Info);
            }
            else
                EditorGUILayout.HelpBox(RENAME_NOTE, MessageType.Info);

            for (int i = 0; i < clips.arraySize; i++)
                DrawSlice(clips.GetArrayElementAtIndex(i), i);

            DrawDuplicateWarning(clips);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sections"), true);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// One slice: its editable name, the numbers it was baked with, and its markers.
        /// </summary>
        private static void DrawSlice(SerializedProperty slice, int index)
        {
            SerializedProperty nameProperty = slice.FindPropertyRelative("name");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                string edited = EditorGUILayout.DelayedTextField(
                    new GUIContent($"Slice {index}", "What gameplay code plays this slice by."),
                    nameProperty.stringValue);

                if (EditorGUI.EndChangeCheck()) nameProperty.stringValue = edited.Trim();

                float frameRate = slice.FindPropertyRelative("frameRate").floatValue;
                EditorGUILayout.LabelField(" ",
                    $"{slice.FindPropertyRelative("frames").intValue} frames @ {frameRate:0.##} fps, " +
                    $"{slice.FindPropertyRelative("length").floatValue:0.###}s",
                    EditorStyles.miniLabel);

                EditorGUILayout.PropertyField(slice.FindPropertyRelative("events"), true);
            }
        }

        /*
         * The same warning the baker gives before baking, repeated here because a name can be edited into
         * a collision after the fact, and nothing else would ever say so.
         */
        private static void DrawDuplicateWarning(SerializedProperty clips)
        {
            for (int i = 0; i < clips.arraySize; i++)
            {
                string mine = clips.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;

                for (int j = i + 1; j < clips.arraySize; j++)
                {
                    string theirs = clips.GetArrayElementAtIndex(j).FindPropertyRelative("name").stringValue;
                    if (!string.Equals(mine, theirs, System.StringComparison.OrdinalIgnoreCase)) continue;

                    EditorGUILayout.HelpBox(
                        $"Slices {i} and {j} are both called '{mine}'. Play() matches the first one, so " +
                        $"slice {j} can only be reached by its index.",
                        MessageType.Warning);

                    return;
                }
            }
        }

    }
}
