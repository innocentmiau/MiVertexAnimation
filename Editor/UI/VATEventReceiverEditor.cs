using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The default inspector would draw the binding name as an editable text field, which is the one thing
     * that must not be edited: it is what matches the binding to a marker, and a typo silently stops the
     * response ever firing. Drawn as a label here, with the clips it was found on beside it.
     *
     * Names that are no longer in the clip set are called out rather than hidden, because a wired response
     * that can never fire looks exactly like one that works until the moment it is needed.
     */
    /// <summary>
    /// The VAT Event Receiver inspector: one row per marker, with what to do about it.
    /// </summary>
    [CustomEditor(typeof(VATEventReceiver))]
    public class VATEventReceiverEditor : UnityEditor.Editor
    {

        private const string MISSING_NAME = "Nothing in the clip set is called this any more, so this can " +
                                            "never fire. It is kept in case the name comes back on the next bake.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty animatorProperty = serializedObject.FindProperty("animator");
            EditorGUILayout.PropertyField(animatorProperty);

            VATAnimator animator = animatorProperty.objectReferenceValue as VATAnimator;
            VATClipSet set = animator ? animator.ClipSet : null;

            if (!animator)
            {
                EditorGUILayout.HelpBox(
                    "Assign the VATAnimator this should listen to. It is usually on the same object.",
                    MessageType.Info);

                serializedObject.ApplyModifiedProperties();
                return;
            }

            if (!set)
            {
                EditorGUILayout.HelpBox(
                    "That animator has no Clip Set, so there are no marker names to offer. Assign the " +
                    "asset the baker generated, named <output>_Clips.",
                    MessageType.Warning);

                serializedObject.ApplyModifiedProperties();
                return;
            }

            Dictionary<string, string> markerSources = new Dictionary<string, string>();
            List<string> clipNames = new List<string>();
            CollectNames(set, markerSources, clipNames);

            EditorGUILayout.Space();
            if (VATUi.Button(VATUi.Content("Refresh from Clip Set",
                    "Add a row for anything baked since this was last looked at. Nothing already wired is touched.",
                    VATIcons.First("Refresh", "RotateTool")), VATUi.GENTLE))
            {
                ((VATEventReceiver)target).SyncWithClipSet();
                EditorUtility.SetDirty(target);
            }

            DrawMarkers(markerSources);
            DrawClipFinished(clipNames);

            serializedObject.ApplyModifiedProperties();
        }

        private static void CollectNames(VATClipSet set, Dictionary<string, string> markerSources,
                                         List<string> clipNames)
        {
            for (int i = 0; i < set.Count; i++)
            {
                string clipName = set.NameAt(i);
                if (!clipNames.Contains(clipName)) clipNames.Add(clipName);

                VATClipEvent[] events = set.EventsAt(i);
                if (events == null) continue;

                for (int e = 0; e < events.Length; e++)
                {
                    string marker = events[e].name;
                    if (string.IsNullOrEmpty(marker)) continue;

                    // Several clips can carry the same marker, and one binding answers for all of them,
                    // so the row says which ones rather than pretending there is only one.
                    markerSources[marker] = markerSources.TryGetValue(marker, out string already)
                        ? $"{already}, {clipName}"
                        : clipName;
                }
            }
        }

        /*
         * Filled in from the clip set rather than added by hand, because there are only ever as many
         * markers as somebody deliberately placed, and having them all listed is the point.
         */
        private void DrawMarkers(Dictionary<string, string> markerSources)
        {
            SerializedProperty list = serializedObject.FindProperty("markers");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                VATUi.Content("Markers", VATIcons.First("Animation.EventMarker", "AnimationClip Icon")),
                EditorStyles.boldLabel);

            if (list.arraySize == 0)
            {
                EditorGUILayout.HelpBox(
                    "No markers on any baked clip yet. Add them in the baker's Events section, save them " +
                    "to the clip set or re-bake, then press Refresh.",
                    MessageType.Info);

                return;
            }

            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty binding = list.GetArrayElementAtIndex(i);
                string bindingName = binding.FindPropertyRelative("eventName").stringValue;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(bindingName, EditorStyles.boldLabel);

                    if (markerSources.TryGetValue(bindingName, out string clips))
                        EditorGUILayout.LabelField($"on {clips}", EditorStyles.miniLabel);
                    else
                        EditorGUILayout.HelpBox(MISSING_NAME, MessageType.Warning);

                    EditorGUILayout.PropertyField(binding.FindPropertyRelative("response"), GUIContent.none);
                }
            }
        }

        /*
         * Added one at a time rather than filled in from the clip set. Every clip has an end, so a rig
         * with ten of them would open with ten UnityEvents nobody asked for and a screen of empty rows
         * to scroll past. Almost nothing wants to hear about every clip ending.
         */
        private void DrawClipFinished(List<string> clipNames)
        {
            SerializedProperty list = serializedObject.FindProperty("clipFinished");

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    VATUi.Content("Clip Finished", VATIcons.First("PlayButton", "Animation Icon")),
                    EditorStyles.boldLabel);

                Rect addRect = GUILayoutUtility.GetRect(new GUIContent("+ Add"), EditorStyles.miniButton,
                    GUILayout.Width(60f));

                if (GUI.Button(addRect, "+ Add", EditorStyles.miniButton)) ShowClipMenu(addRect, list, clipNames);
            }

            if (list.arraySize == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing here. Add a clip to be told when a PlayOnce of it reaches its end, which is " +
                    "how an attack hands control back to whatever was driving it.",
                    MessageType.None);

                return;
            }

            int remove = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty binding = list.GetArrayElementAtIndex(i);
                string bindingName = binding.FindPropertyRelative("eventName").stringValue;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(bindingName, EditorStyles.boldLabel);

                        if (VATUi.Button(new GUIContent("-", "Stop listening for this clip's end."),
                                VATUi.DESTRUCTIVE, EditorStyles.miniButton, GUILayout.Width(22f)))
                        {
                            remove = i;
                        }
                    }

                    if (!clipNames.Contains(bindingName))
                        EditorGUILayout.HelpBox(MISSING_NAME, MessageType.Warning);

                    EditorGUILayout.PropertyField(binding.FindPropertyRelative("response"), GUIContent.none);
                }
            }

            if (remove >= 0) list.DeleteArrayElementAtIndex(remove);
        }

        /*
         * The menu fires after this pass has finished, so it acts on the component rather than on a
         * SerializedProperty, which would be stale by then. Clips that already have a row are listed
         * greyed out rather than left out, so the menu always says what the clip set holds.
         */
        private void ShowClipMenu(Rect rect, SerializedProperty list, List<string> clipNames)
        {
            HashSet<string> taken = new HashSet<string>();
            for (int i = 0; i < list.arraySize; i++)
                taken.Add(list.GetArrayElementAtIndex(i).FindPropertyRelative("eventName").stringValue);

            VATEventReceiver receiver = (VATEventReceiver)target;
            GenericMenu menu = new GenericMenu();

            foreach (string clipName in clipNames)
            {
                if (taken.Contains(clipName))
                {
                    menu.AddDisabledItem(new GUIContent(clipName));
                    continue;
                }

                string chosen = clipName;
                menu.AddItem(new GUIContent(clipName), false, () =>
                {
                    Undo.RecordObject(receiver, "Add Clip Finished Event");
                    receiver.AddClipFinished(chosen);
                    EditorUtility.SetDirty(receiver);
                });
            }

            menu.DropDown(rect);
        }

    }
}
