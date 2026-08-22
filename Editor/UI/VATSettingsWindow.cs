using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * One page, reachable from two places, because people look in two places. Preferences rather than
     * Project Settings: these describe how one person likes their editor, so they follow whoever set
     * them between projects instead of arriving in somebody else's checkout.
     *
     * Anything that decides what a bake CONTAINS stays in the baker window and in the bake settings
     * asset beside the output. A preference that silently changed what got written would be a setting
     * nobody could see when looking at the result.
     */
    /// <summary>The package's preferences page, and the maintenance actions that belong beside it.</summary>
    public static class VATSettingsWindow
    {

        private const string PATH = "Preferences/Mi Vertex Animation";

        [MenuItem("Tools/MiVertexAnimation/Settings")]
        private static void Open()
        {
            SettingsService.OpenUserPreferences(PATH);
        }

        [SettingsProvider]
        private static SettingsProvider Create()
        {
            return new SettingsProvider(PATH, SettingsScope.User)
            {
                label = "Mi Vertex Animation",
                guiHandler = Draw,
                keywords = new HashSet<string>(new[]
                {
                    "vat", "vertex", "animation", "baker", "icons", "colours", "colors",
                    "preview", "bones", "readable", "memory", "texture"
                })
            };
        }

        private static void Draw(string search)
        {
            EditorGUIUtility.labelWidth = 220f;

            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 8, 8) }))
            {
                DrawWindowSettings();
                EditorGUILayout.Space(10f);

                DrawBakerSettings();
                EditorGUILayout.Space(10f);

                DrawMaintenance();
            }
        }

        private static void DrawWindowSettings()
        {
            EditorGUILayout.LabelField("Baker Window", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            VATUiSettings.Icons = EditorGUILayout.Toggle(
                new GUIContent("Icons", "Unity's own icons on section headings and buttons."),
                VATUiSettings.Icons);

            VATUiSettings.Colours = EditorGUILayout.Toggle(
                new GUIContent("Button Colours",
                    "Tints buttons by what they do: blue for the one the window exists for, red for " +
                    "anything that throws work away."),
                VATUiSettings.Colours);

            VATUiSettings.SideBySide = EditorGUILayout.Toggle(
                new GUIContent("Preview Beside Settings",
                    "Put the preview and the event track in a second column when the window is wide " +
                    "enough for one. Off keeps everything in a single column."),
                VATUiSettings.SideBySide);

            VATUiSettings.PreviewHeight = EditorGUILayout.Slider(
                new GUIContent("Preview Height", "Also draggable by the grip under the preview."),
                VATUiSettings.PreviewHeight,
                VATUiSettings.SMALLEST_PREVIEW_HEIGHT, VATUiSettings.LARGEST_PREVIEW_HEIGHT);

            VATUiSettings.SplitFraction = EditorGUILayout.Slider(
                new GUIContent("Settings Column Width",
                    "How much of the window the settings take when the preview sits beside them."),
                VATUiSettings.SplitFraction,
                VATUiSettings.SMALLEST_SPLIT_FRACTION, VATUiSettings.LARGEST_SPLIT_FRACTION);

            if (EditorGUI.EndChangeCheck()) RepaintBakers();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Reset Layout", GUILayout.Width(140f)))
                {
                    VATUiSettings.ResetLayout();
                    RepaintBakers();
                }
            }
        }

        private static void DrawBakerSettings()
        {
            EditorGUILayout.LabelField("Sections", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            VATUiSettings.ShowWeightlessBones = EditorGUILayout.Toggle(
                new GUIContent("List Bones With No Weight",
                    "Rigs carry bones that exist only as parents or as helpers and move no vertex at " +
                    "all, and a section built on one bakes an empty mask. Off hides them from the " +
                    "section bone picker."),
                VATUiSettings.ShowWeightlessBones);

            if (EditorGUI.EndChangeCheck()) RepaintBakers();
        }

        /*
         * A texture built from script keeps a copy of its pixels in system memory as well as on the GPU,
         * and nothing in this package ever reads that copy. Bakes written before that was fixed still
         * carry it, so the fix has to be reachable rather than only applied to new ones.
         */
        private static void DrawMaintenance()
        {
            EditorGUILayout.LabelField("Maintenance", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Baked textures written before version 1.0 keep a copy of their pixels in system memory " +
                "that nothing reads. Freeing it changes nothing about how they render.",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Free CPU Copies of Baked Textures", GUILayout.Width(280f)))
                    VATTextureMaintenance.FreeExisting();
            }
        }

        private static void RepaintBakers()
        {
            foreach (VATBakerWindow window in Resources.FindObjectsOfTypeAll<VATBakerWindow>())
                window.Repaint();
        }

    }
}
