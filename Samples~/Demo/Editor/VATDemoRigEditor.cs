using System.IO;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The scene opens empty until something is baked, and an empty scene with no explanation reads as
     * a broken sample rather than an unfinished one. This is the whole difference, so it says what to
     * do in the place you would already be looking.
     *
     * The button hands the baker this sample's own settings asset rather than opening it on whatever
     * was baked last, because a window pointed at somebody else's model and output folder is not a
     * starting point. Everything it presets lives in Source/DemoBakeSettings.asset, so changing what
     * the button sets up is editing an asset rather than editing this file.
     */
    /// <summary>Inspector for VATDemoRig, which explains and performs the one setup step it needs.</summary>
    [CustomEditor(typeof(VATDemoRig))]
    public class VATDemoRigEditor : Editor
    {

        private const string SETTINGS = "Source/DemoBakeSettings.asset";
        private const string OUTPUT = "Baked";

        public override void OnInspectorGUI()
        {
            VATDemoRig rig = (VATDemoRig)target;

            if (!rig.Ready)
            {
                EditorGUILayout.HelpBox(
                    "No prefab yet, so this scene has nothing in it.\n\n" +
                    "1. Press the button below. The baker opens set up for this sample.\n" +
                    "2. Press Bake.\n" +
                    "3. Drag the prefab it wrote into VAT Prefab, below.",
                    MessageType.Info);

                if (GUILayout.Button("Open the Baker, set up for this sample")) OpenBaker();

                EditorGUILayout.Space();
            }
            else
            {
                EditorGUILayout.LabelField($"Play spawns {rig.Count} copies.", EditorStyles.miniLabel);
            }

            DrawDefaultInspector();
        }

        /*
         * Paths are worked out from this script's own asset path, so the sample keeps working wherever
         * it was imported to - the version number in the import path changes with every release, and
         * anything written down here would go stale on the next one.
         */
        private void OpenBaker()
        {
            string root = SampleRoot();

            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("Could not locate this sample's folder, so the baker is opening " +
                                 "empty. Load Source/DemoBakeSettings.asset by hand.");
                VATBakerWindow.ShowWindow();
                return;
            }

            string path = $"{root}/{SETTINGS}";
            VATBakeSettings settings = AssetDatabase.LoadAssetAtPath<VATBakeSettings>(path);

            if (!settings)
            {
                Debug.LogWarning($"No bake settings at {path}, so the baker is opening empty.");
                VATBakerWindow.ShowWindow();
                return;
            }

            // Made now rather than at bake time so the folder field points somewhere that exists,
            // and so the Project window has something to show when the bake lands.
            string output = $"{root}/{OUTPUT}";
            if (!AssetDatabase.IsValidFolder(output)) AssetDatabase.CreateFolder(root, OUTPUT);

            VATBakerWindow.ShowWith(settings, output);
        }

        /// <summary>
        /// This sample's folder, found from where this script itself was imported to.
        /// </summary>
        /// <returns>A project relative path, or null when the script is not an asset.</returns>
        private string SampleRoot()
        {
            MonoScript script = MonoScript.FromScriptableObject(this);
            string path = script ? AssetDatabase.GetAssetPath(script) : null;
            if (string.IsNullOrEmpty(path)) return null;

            // .../Demo/Editor/VATDemoRigEditor.cs -> .../Demo
            string editor = Path.GetDirectoryName(path);
            string root = string.IsNullOrEmpty(editor) ? null : Path.GetDirectoryName(editor);

            return string.IsNullOrEmpty(root) ? null : root.Replace('\\', '/');
        }

    }
}
