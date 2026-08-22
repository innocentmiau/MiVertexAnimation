using UnityEditor;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Kept in EditorPrefs rather than anywhere in the project: this is how one person likes their editor,
     * so it should follow them between projects instead of being saved with any one of them
     * and turning up in somebody else's checkout.
     *
     * Read from EditorPrefs once and held in memory after that. EditorPrefs is a native call out to a
     * store on disk, and these are read while laying out every header and every button on every repaint.
     */
    /// <summary>
    /// How the baker window is drawn: whether it shows icons and button colours, and how tall its preview is.
    /// </summary>
    public static class VATUiSettings
    {

        private const string ICONS_KEY = "MiVertexAnimation.Icons";
        private const string COLOURS_KEY = "MiVertexAnimation.Colours";
        private const string PREVIEW_HEIGHT_KEY = "MiVertexAnimation.PreviewHeight";
        private const string SIDE_BY_SIDE_KEY = "MiVertexAnimation.SideBySide";
        private const string SPLIT_FRACTION_KEY = "MiVertexAnimation.SplitFraction";
        private const string WEIGHTLESS_BONES_KEY = "MiVertexAnimation.ShowWeightlessBones";

        public const float ICON_SIZE = 16f;

        public const float DEFAULT_PREVIEW_HEIGHT = 260f;
        public const float SMALLEST_PREVIEW_HEIGHT = 120f;
        public const float LARGEST_PREVIEW_HEIGHT = 900f;

        public const float DEFAULT_SPLIT_FRACTION = .5f;
        public const float SMALLEST_SPLIT_FRACTION = .2f;
        public const float LARGEST_SPLIT_FRACTION = .8f;

        private static bool _read;
        private static bool _icons = true;
        private static bool _colours = true;
        private static float _previewHeight = DEFAULT_PREVIEW_HEIGHT;
        private static bool _sideBySide = true;
        private static float _splitFraction = DEFAULT_SPLIT_FRACTION;
        private static bool _showWeightlessBones;

        /// <summary>On by default, so someone who has never thought about it gets the readable version.</summary>
        public static bool Icons
        {
            get
            {
                Read();

                return _icons;
            }
            set
            {
                Read();
                if (_icons == value) return;

                _icons = value;
                EditorPrefs.SetBool(ICONS_KEY, value);
            }
        }

        public static bool Colours
        {
            get
            {
                Read();

                return _colours;
            }
            set
            {
                Read();
                if (_colours == value) return;

                _colours = value;
                EditorPrefs.SetBool(COLOURS_KEY, value);
            }
        }

        /// <summary>Height of the baker's preview, dragged by the grip underneath it.</summary>
        public static float PreviewHeight
        {
            get
            {
                Read();

                return _previewHeight;
            }
            set
            {
                Read();

                float wanted = Mathf.Clamp(value, SMALLEST_PREVIEW_HEIGHT, LARGEST_PREVIEW_HEIGHT);
                if (Mathf.Approximately(_previewHeight, wanted)) return;

                _previewHeight = wanted;
                EditorPrefs.SetFloat(PREVIEW_HEIGHT_KEY, wanted);
            }
        }

        /// <summary>
        /// Whether the preview sits beside the settings rather than under them, when the window is wide
        /// enough for it. The window decides whether there is room, this only says whether it is wanted.
        /// </summary>
        public static bool SideBySide
        {
            get
            {
                Read();

                return _sideBySide;
            }
            set
            {
                Read();
                if (_sideBySide == value) return;

                _sideBySide = value;
                EditorPrefs.SetBool(SIDE_BY_SIDE_KEY, value);
            }
        }

        /// <summary>How much of the window's width the settings pane takes, as a fraction.</summary>
        public static float SplitFraction
        {
            get
            {
                Read();

                return _splitFraction;
            }
            set
            {
                Read();

                float wanted = Mathf.Clamp(value, SMALLEST_SPLIT_FRACTION, LARGEST_SPLIT_FRACTION);
                if (Mathf.Approximately(_splitFraction, wanted)) return;

                _splitFraction = wanted;
                EditorPrefs.SetFloat(SPLIT_FRACTION_KEY, wanted);
            }
        }

        /*
         * Rigs are full of bones that exist only as parents or as helpers and move no vertex at all, and
         * a section built on one bakes an empty mask. Hidden by default for that reason, and a setting
         * rather than a constant because a rig that names its deform bones oddly is somebody's problem
         * eventually.
         */
        /// <summary>Whether the section bone picker lists bones that carry no skin weight.</summary>
        public static bool ShowWeightlessBones
        {
            get
            {
                Read();

                return _showWeightlessBones;
            }
            set
            {
                Read();
                if (_showWeightlessBones == value) return;

                _showWeightlessBones = value;
                EditorPrefs.SetBool(WEIGHTLESS_BONES_KEY, value);
            }
        }

        /// <summary>Puts the window's layout back where it started.</summary>
        public static void ResetLayout()
        {
            PreviewHeight = DEFAULT_PREVIEW_HEIGHT;
            SplitFraction = DEFAULT_SPLIT_FRACTION;
            SideBySide = true;
        }

        private static void Read()
        {
            if (_read) return;

            _read = true;
            _icons = EditorPrefs.GetBool(ICONS_KEY, true);
            _colours = EditorPrefs.GetBool(COLOURS_KEY, true);
            _previewHeight = Mathf.Clamp(EditorPrefs.GetFloat(PREVIEW_HEIGHT_KEY, DEFAULT_PREVIEW_HEIGHT),
                SMALLEST_PREVIEW_HEIGHT, LARGEST_PREVIEW_HEIGHT);
            _sideBySide = EditorPrefs.GetBool(SIDE_BY_SIDE_KEY, true);
            _splitFraction = Mathf.Clamp(EditorPrefs.GetFloat(SPLIT_FRACTION_KEY, DEFAULT_SPLIT_FRACTION),
                SMALLEST_SPLIT_FRACTION, LARGEST_SPLIT_FRACTION);
            _showWeightlessBones = EditorPrefs.GetBool(WEIGHTLESS_BONES_KEY, false);
        }

    }
}
