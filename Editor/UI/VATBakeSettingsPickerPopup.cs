using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Replaces EditorGUIUtility.ShowObjectPicker, which was reporting its result through the command
     * event stream and losing it: the control id it is matched against is cleared as the picker closes,
     * so the ObjectSelectorClosed branch never ran and picking a settings asset appeared to do nothing.
     * The id was also being requested inside a button's if block, which breaks IMGUI's rule that control
     * ids are asked for in the same order on every pass.
     *
     * Doing it here needs neither, and can show what each asset actually is.
     * Unity's picker lists bare asset names, which say nothing about which object was baked or how many
     * clips went in, and those are the only things worth knowing when choosing between saved bakes.
     */
    /// <summary>
    /// The searchable list of saved bake settings behind the baker's "Load" button.
    /// </summary>
    internal class VATBakeSettingsPickerPopup : PopupWindowContent
    {

        private readonly Action<VATBakeSettings> _onPick;
        private readonly List<VATBakeSettings> _all = new List<VATBakeSettings>();

        private string _search = string.Empty;
        private Vector2 _scroll;
        private bool _focused;

        /// <summary>
        /// Collects every saved bake setting in the project.
        /// </summary>
        /// <param name="onPick">Raised with the asset that was clicked, just before the popup closes.</param>
        public VATBakeSettingsPickerPopup(Action<VATBakeSettings> onPick)
        {
            _onPick = onPick;

            foreach (string guid in AssetDatabase.FindAssets("t:VATBakeSettings"))
            {
                VATBakeSettings found = AssetDatabase.LoadAssetAtPath<VATBakeSettings>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (found) _all.Add(found);
            }

            _all.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        public override Vector2 GetWindowSize() => new Vector2(340f, 340f);

        public override void OnGUI(Rect rect)
        {
            EditorGUILayout.Space(4f);

            GUI.SetNextControlName("VATSettingsSearch");
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (!_focused)
            {
                EditorGUI.FocusTextInControl("VATSettingsSearch");
                _focused = true;
            }

            VATBakeSettings[] matches = _all
                .Where(s => string.IsNullOrEmpty(_search) ||
                            s.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            if (matches.Length == 0)
            {
                EditorGUILayout.LabelField(_all.Count == 0
                    ? "No saved bake settings in this project yet."
                    : "No settings asset matches.", EditorStyles.miniLabel);
                return;
            }

            using (EditorGUILayout.ScrollViewScope scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                foreach (VATBakeSettings settings in matches)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        if (GUILayout.Button(settings.name, EditorStyles.miniButton))
                        {
                            _onPick(settings);
                            editorWindow.Close();
                        }

                        string target = settings.target ? settings.target.name : "target missing";
                        int clipCount = settings.clips != null ? settings.clips.Count : 0;
                        EditorGUILayout.LabelField($"{target}  |  {clipCount} clip(s)  |  {settings.outputPath}",
                            EditorStyles.miniLabel);
                    }
                }
            }
        }

    }
}
