using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The clip field is an index into a texture array, which tells you nothing on its own.
     * Drawing it as a name dropdown is the whole reason VATClipSet exists,
     * and the play buttons exist because a cross-fade cannot be judged from a number field.
     */
    /// <summary>
    /// The VATAnimator inspector: a clip name dropdown, and one button per clip in play mode.
    /// </summary>
    [CustomEditor(typeof(VATAnimator))]
    [CanEditMultipleObjects]
    public class VATAnimatorEditor : UnityEditor.Editor
    {

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty clipSetProperty = serializedObject.FindProperty("clipSet");
            SerializedProperty clipProperty = serializedObject.FindProperty("clipIndex");
            SerializedProperty randomizeProperty = serializedObject.FindProperty("randomizeStartPhase");

            EditorGUILayout.PropertyField(clipSetProperty);

            VATClipSet clipSet = clipSetProperty.objectReferenceValue as VATClipSet;
            if (clipSet && clipSet.Count > 0)
            {
                string[] names = clipSet.Names();
                int index = Mathf.Clamp(clipProperty.intValue, 0, names.Length - 1);

                EditorGUI.BeginChangeCheck();
                index = EditorGUILayout.Popup(new GUIContent("Clip", "Which baked animation to play."), index, names);
                if (EditorGUI.EndChangeCheck()) clipProperty.intValue = index;

                VATClipEntry entry = clipSet.clips[index];
                EditorGUILayout.LabelField(" ",
                    $"slice {index}  |  {entry.frames} frames @ {entry.frameRate:0.##} fps  |  {entry.length:0.00}s",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.PropertyField(clipProperty);
                EditorGUILayout.HelpBox(
                    "Assign the Clip Set the baker generated (named <output>_Clips) to choose clips " +
                    "by name instead of by index.",
                    MessageType.Info);
            }

            EditorGUILayout.PropertyField(randomizeProperty);
            serializedObject.ApplyModifiedProperties();

            if (!clipSet || clipSet.Count == 0) return;

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                EditorGUILayout.LabelField(
                    Application.isPlaying ? "Play a clip" : "Play a clip (enter play mode)",
                    EditorStyles.boldLabel);

                // Buttons act on every selected animator.
                for (int i = 0; i < clipSet.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(clipSet.clips[i].name))
                        {
                            foreach (UnityEngine.Object selected in targets)
                                ((VATAnimator)selected).Play(i);
                        }

                        if (GUILayout.Button("snap", EditorStyles.miniButton, GUILayout.Width(48f)))
                        {
                            foreach (UnityEngine.Object selected in targets)
                                ((VATAnimator)selected).Snap(i);
                        }
                    }
                }
            }

            if (Application.isPlaying) Repaint();
        }

    }
}
