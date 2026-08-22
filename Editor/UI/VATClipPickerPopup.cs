using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Chosen clips are filtered out rather than shown ticked, so the list shrinks as the bake set is
     * built and a rig with eighty clips stops being a wall of text after the first few picks.
     * Closing on the first pick is deliberate too: the alternative leaves a popup covering the list
     * that is being added to, so you cannot see what you already have.
     *
     * Arrow keys are read before the search field is drawn, and used, so the text editor never sees them.
     * Left to reach the field, up and down move the caret to the ends of the line instead of moving
     * through the results, which is not what anyone typing into a search box expects.
     */
    /// <summary>
    /// The searchable clip list behind the baker's "+ Add" button.
    /// </summary>
    internal class VATClipPickerPopup : PopupWindowContent
    {

        private readonly AnimationClip[] _all;
        private readonly List<AnimationClip> _chosen;
        private readonly Action<AnimationClip> _onPick;

        private string _search = string.Empty;
        private Vector2 _scroll;
        private bool _focused;

        // Last pass's results. Arrow keys and Enter cannot change what matches, so acting on these
        // before this pass has filtered again is safe, and it is the only way to read a key press
        // before the search field has had a chance to swallow it.
        private AnimationClip[] _matches = new AnimationClip[0];
        private int _highlighted = -1;
        private bool _scrollToHighlight;

        /// <summary>
        /// Builds the picker.
        /// </summary>
        /// <param name="all">Every clip on the source object's controller.</param>
        /// <param name="chosen">Clips already in the bake set, which are hidden from the list.</param>
        /// <param name="onPick">Raised with the clip that was chosen, just before the popup closes.</param>
        public VATClipPickerPopup(AnimationClip[] all, List<AnimationClip> chosen, Action<AnimationClip> onPick)
        {
            _all = all;
            _chosen = chosen;
            _onPick = onPick;
        }

        public override Vector2 GetWindowSize() => new Vector2(280f, 320f);

        public override void OnGUI(Rect rect)
        {
            if (HandleKeys()) return;

            EditorGUILayout.Space(4f);

            GUI.SetNextControlName("VATClipSearch");
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);

            // A new search means new results, so nothing is chosen until an arrow key says so.
            if (EditorGUI.EndChangeCheck()) _highlighted = -1;

            if (!_focused)
            {
                EditorGUI.FocusTextInControl("VATClipSearch");
                _focused = true;
            }

            _matches = _all
                .Where(c => c && !_chosen.Contains(c))
                .Where(c => string.IsNullOrEmpty(_search) ||
                            c.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            _highlighted = Mathf.Min(_highlighted, _matches.Length - 1);

            if (_matches.Length == 0)
            {
                EditorGUILayout.LabelField(_chosen.Count >= _all.Length
                    ? "Every clip is already selected."
                    : "No clip matches.", EditorStyles.miniLabel);
                return;
            }

            KeepHighlightInView();

            using (EditorGUILayout.ScrollViewScope scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                for (int i = 0; i < _matches.Length; i++)
                {
                    // Set straight rather than through VATUi.Tinted, because this says which row Enter
                    // will take and has to show even for someone who turned button colours off.
                    Color was = GUI.backgroundColor;
                    if (i == _highlighted) GUI.backgroundColor = VATUi.PRIMARY;

                    bool clicked = GUILayout.Button(_matches[i].name, EditorStyles.miniButton);
                    GUI.backgroundColor = was;

                    if (clicked) Pick(_matches[i]);
                }
            }
        }

        /*
         * Deliberately does not wrap at either end. A list that jumps from the last result back to the
         * first loses you your place, and holding down an arrow to reach the end is how people find out
         * they are at the end.
         */
        /// <summary>
        /// Reads the keys that move through the results, before the search field can take them.
        /// </summary>
        /// <returns>True when the popup is closing and nothing more should be drawn this pass.</returns>
        private bool HandleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return false;

            switch (e.keyCode)
            {
                case KeyCode.DownArrow:
                    _highlighted = Mathf.Min(_highlighted + 1, _matches.Length - 1);
                    _scrollToHighlight = true;
                    e.Use();
                    return false;

                case KeyCode.UpArrow:
                    _highlighted = Mathf.Max(_highlighted - 1, 0);
                    _scrollToHighlight = true;
                    e.Use();
                    return false;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_matches.Length == 0) return false;

                    // Nothing highlighted yet means the search itself was the choice, so Enter takes the
                    // first result rather than doing nothing.
                    Pick(_matches[Mathf.Clamp(_highlighted, 0, _matches.Length - 1)]);
                    e.Use();
                    return true;

                case KeyCode.Escape:
                    editorWindow.Close();
                    e.Use();
                    return true;

                default:
                    return false;
            }
        }

        /*
         * Worked out from the row height rather than from the highlighted button's rect, because that
         * rect only exists after the row has been drawn, and by then the scroll view it lives in has
         * already been positioned for this pass.
         */
        /// <summary>
        /// Scrolls just far enough to bring the highlighted row into view, and only after an arrow key,
        /// so it never fights the scroll wheel.
        /// </summary>
        private void KeepHighlightInView()
        {
            if (!_scrollToHighlight || _highlighted < 0) return;

            _scrollToHighlight = false;

            float row = EditorGUIUtility.singleLineHeight + 2f;
            float view = Mathf.Max(row, GetWindowSize().y - 44f);
            _scroll.y = Mathf.Max(0f, Mathf.Clamp(_scroll.y, ((_highlighted + 1) * row) - view, _highlighted * row));
        }

        private void Pick(AnimationClip clip)
        {
            _onPick(clip);
            editorWindow.Close();
        }

    }
}
