using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * The pictures and the colours in one place, so every control in the baker answers to the same two
     * settings and nothing has to remember to ask.
     *
     * Colour goes on through GUI.backgroundColor, which multiplies the button's own background rather
     * than replacing it. Against the dark skin's grey that comes out as a wash rather than a block,
     * which is what is wanted: enough to say "this one throws work away" while it still looks like
     * every other button in the editor.
     */
    /// <summary>
    /// Shared look for the baker window: section boxes, headers, tinted buttons.
    /// </summary>
    public static class VATUi
    {

        /// <summary>Something that throws work or stored data away.</summary>
        public static readonly Color DESTRUCTIVE = new Color(1f, .5f, .45f);

        /// <summary>The button the window exists for.</summary>
        public static readonly Color PRIMARY = new Color(.55f, .78f, 1f);

        /// <summary>Something that only looks, opens or reveals.</summary>
        public static readonly Color GENTLE = new Color(.7f, .95f, .8f);

        /// <summary>Changes something outside this window, so worth telling apart from the ones that only look.</summary>
        public static readonly Color CAUTION = new Color(1f, .86f, .4f);

        // GUIContent sets an icon flush against its text, which at heading weight reads as one smudge
        // rather than a picture and a title. Two spaces is the whole fix.
        private const string HEADING_GAP = "  ";

        private static readonly Dictionary<string, GUIStyle> PADDED = new Dictionary<string, GUIStyle>();

        private static GUIStyle _section;
        private static bool _sectionProSkin;

        /*
         * Built lazily rather than in a field initializer: a GUIStyle copied from EditorStyles at static
         * construction runs before Unity's skin exists and comes out blank.
         * Rebuilt on a theme change for the same reason the icons are, since the built-in styles are
         * replaced wholesale when the skin does.
         */
        /// <summary>The box one section of the window is drawn inside.</summary>
        public static GUIStyle Section
        {
            get
            {
                if (_section != null && _sectionProSkin == EditorGUIUtility.isProSkin) return _section;

                _sectionProSkin = EditorGUIUtility.isProSkin;
                _section = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(8, 8, 6, 8),
                    margin = new RectOffset(2, 2, 3, 3)
                };

                return _section;
            }
        }

        /// <summary>
        /// A label with an icon on it, or without one when icons are turned off.
        /// </summary>
        /// <param name="text">The label text.</param>
        /// <param name="tooltip">Hover text, or null for none.</param>
        /// <param name="icon">The icon, which may be null.</param>
        /// <returns>Content ready to hand to any IMGUI control.</returns>
        public static GUIContent Content(string text, string tooltip, Texture icon)
        {
            return new GUIContent(text, VATUiSettings.Icons ? icon : null, tooltip);
        }

        /// <summary>
        /// A label with an icon on it, or without one when icons are turned off.
        /// </summary>
        /// <param name="text">The label text.</param>
        /// <param name="icon">The icon, which may be null.</param>
        /// <returns>Content ready to hand to any IMGUI control.</returns>
        public static GUIContent Content(string text, Texture icon)
        {
            return new GUIContent(text, VATUiSettings.Icons ? icon : null);
        }

        /// <summary>
        /// Opens a section box and writes its heading.
        /// </summary>
        /// <param name="title">Heading text.</param>
        /// <param name="icon">Heading icon, which may be null.</param>
        public static void BeginSection(string title, Texture icon)
        {
            EditorGUILayout.BeginVertical(Section);
            EditorGUILayout.LabelField(Heading(title, icon), EditorStyles.boldLabel);
        }

        /// <summary>A section heading, with the icon held off the title rather than against it.</summary>
        private static GUIContent Heading(string title, Texture icon)
        {
            return VATUiSettings.Icons && icon
                ? new GUIContent($"{HEADING_GAP}{title}", icon)
                : new GUIContent(title);
        }

        /// <summary>
        /// Opens a section box whose heading carries the switch that turns the whole thing on, so a
        /// feature nothing is using costs one line instead of a panel.
        /// </summary>
        /// <param name="title">Heading text.</param>
        /// <param name="icon">Heading icon, which may be null.</param>
        /// <param name="enabled">Current state of the switch.</param>
        /// <param name="tooltip">What the switch turns on.</param>
        /// <returns>The state of the switch after this pass.</returns>
        public static bool BeginSection(string title, Texture icon, bool enabled, string tooltip)
        {
            EditorGUILayout.BeginVertical(Section);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(Heading(title, icon), EditorStyles.boldLabel);
                enabled = EditorGUILayout.Toggle(new GUIContent(string.Empty, tooltip), enabled,
                    GUILayout.Width(18f));

                GUILayout.FlexibleSpace();
            }

            return enabled;
        }

        /// <summary>Closes the box BeginSection opened.</summary>
        public static void EndSection()
        {
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// A button that says what kind of thing it does through its colour.
        /// </summary>
        /// <param name="content">Label, icon and tooltip.</param>
        /// <param name="tint">One of the colours on this class.</param>
        /// <param name="options">Layout options, as for GUILayout.Button.</param>
        /// <returns>True on the pass where it was clicked.</returns>
        public static bool Button(GUIContent content, Color tint, params GUILayoutOption[] options)
        {
            using (new Tinted(tint))
            {
                return GUILayout.Button(content, Padded(GUI.skin.button), options);
            }
        }

        /// <summary>
        /// A button in a given style that says what kind of thing it does through its colour.
        /// </summary>
        /// <param name="content">Label, icon and tooltip.</param>
        /// <param name="tint">One of the colours on this class.</param>
        /// <param name="style">Style to draw with, usually one of EditorStyles.</param>
        /// <param name="options">Layout options, as for GUILayout.Button.</param>
        /// <returns>True on the pass where it was clicked.</returns>
        public static bool Button(GUIContent content, Color tint, GUIStyle style, params GUILayoutOption[] options)
        {
            using (new Tinted(tint))
            {
                return GUILayout.Button(content, style, options);
            }
        }

        /*
         * A style belongs to the skin it came from, and the built-in ones are replaced when the theme
         * changes, so this is keyed by skin as well as by style.
         */
        /// <summary>
        /// A copy of a style with breathing room inside it, so a label is not touching its own edge.
        /// </summary>
        /// <param name="basis">The style to pad.</param>
        /// <returns>A cached padded copy.</returns>
        public static GUIStyle Padded(GUIStyle basis)
        {
            string key = $"{basis.name}:{(EditorGUIUtility.isProSkin ? 'd' : 'l')}";
            if (PADDED.TryGetValue(key, out GUIStyle cached) && cached != null) return cached;

            GUIStyle padded = new GUIStyle(basis)
            {
                padding = new RectOffset(Mathf.Max(basis.padding.left, 8), Mathf.Max(basis.padding.right, 8),
                    basis.padding.top, basis.padding.bottom)
            };

            PADDED[key] = padded;
            return padded;
        }

        /*
         * Caps how large an icon inside a GUIContent is allowed to draw. Without this a control takes the
         * icon's native size, and Unity's editor icons are commonly 64 pixels, which is what turns an
         * ordinary header into a huge one. The whole window is wrapped in one of these rather than trying
         * to pick only the small icon names, since which names have a small variant is not worth depending on.
         */
        /// <summary>
        /// Holds the editor's icon size for as long as the scope is open.
        /// </summary>
        public readonly struct IconScope : IDisposable
        {

            private readonly Vector2 _was;

            public IconScope(float size)
            {
                _was = EditorGUIUtility.GetIconSize();
                EditorGUIUtility.SetIconSize(new Vector2(size, size));
            }

            public void Dispose() => EditorGUIUtility.SetIconSize(_was);

        }

        /*
         * Puts the colour back whatever happens in between, since GUI.backgroundColor is global
         * and one left set would tint the rest of the window.
         */
        /// <summary>
        /// Tints button backgrounds for as long as the scope is open.
        /// </summary>
        public readonly struct Tinted : IDisposable
        {

            private readonly Color _was;

            public Tinted(Color tint)
            {
                _was = GUI.backgroundColor;

                if (VATUiSettings.Colours) GUI.backgroundColor = tint;
            }

            public void Dispose() => GUI.backgroundColor = _was;

        }

    }
}
