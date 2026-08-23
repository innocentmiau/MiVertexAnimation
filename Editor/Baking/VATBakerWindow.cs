using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace MiVertexAnimation
{

    /*
     * Layout, per frame F and vertex N:
     *     x = N % textureWidth
     *     y = floor(N / textureWidth) + rowsPerFrame * F
     * so a frame occupies a contiguous block of rowsPerFrame rows and there is no limit on vertex count.
     * VAT_Core.hlsl reads it back with the same arithmetic, which is why the two must never drift apart.
     *
     * Every clip becomes one slice of a Texture2DArray. Slices must all be the same size,
     * so the shortest clips pad up to the longest rather than each getting a texture of its own.
     */
    /// <summary>
    /// The Vertex Animation Baker window. Turns a SkinnedMeshRenderer and its AnimationClips into
    /// position and normal textures, a material, a prefab and a clip set.
    /// </summary>
    public class VATBakerWindow : EditorWindow, IHasCustomMenu
    {

        private const string SHADER_NAME = "Mi/Vertex Animation/Lit";
        private const int MAX_TEXTURE_DIMENSION = 16384;
        private const int MAX_CLIPS = 16; // must match VAT_MAX_CLIPS in VAT_Core.hlsl
        private const int MAX_SECTIONS = 4; // one per UV3 component, and VAT_MAX_SECTIONS in VAT_Core.hlsl

        // Narrower than this and a pane cannot hold a label and its field side by side, which is what
        // makes two columns worth having in the first place.
        private const float MIN_PANE_WIDTH = 320f;
        private const float SPLITTER_WIDTH = 7f;
        private const float SMALLEST_SPLIT_WIDTH = (MIN_PANE_WIDTH * 2f) + SPLITTER_WIDTH;

        // The least a shader needs before the baker has anywhere to put the animation. Everything else
        // it writes is optional, because setting a property a shader does not have is simply ignored.
        private static readonly string[] REQUIRED_SHADER_PROPERTIES =
        {
            "_VATPositionTex", "_VATTextureWidth", "_VATTextureHeight", "_VATRowsPerFrame", "_VATClipData0"
        };

        private const int MAX_UNDO_STEPS = 64;

        // How long a value has to stop moving before it becomes an undo step. Typing a name is a run of
        // single-character changes, and without this each keystroke would be its own step to walk back.
        private const double UNDO_SETTLE_SECONDS = .35d;

        // Error a dropped frame is allowed to introduce, as a fraction of the model's own size, so the
        // same number behaves the same whether the rig is authored in metres or centimetres.
        private const float PRECISE_TOLERANCE = .0005f;
        private const float BALANCED_TOLERANCE = .002f;
        private const float AGGRESSIVE_TOLERANCE = .01f;
        private const int MAX_AUTO_STEP = 10;

        private GameObject _target;

        private SkinnedMeshRenderer[] _renderers = new SkinnedMeshRenderer[0];
        private int _rendererIndex;
        private VATRendererMode _rendererMode = VATRendererMode.SELECTED;

        private AnimationClip[] _clips = new AnimationClip[0];
        private int _clipIndex;
        private AnimationClip _explicitClip;
        private AnimationClip _frameRangeClip;
        private readonly List<AnimationClip> _bakeClips = new List<AnimationClip>();
        private float _blendDuration = .15f;

        private List<VATAuthoredClipEvents> _authoredEvents = new List<VATAuthoredClipEvents>();
        private bool _showEvents = true;
        private int _selectedEvent = -1;
        private int _draggingEvent = -1;

        private int _startFrame;
        private int _endFrame = 1;
        private int _frameStep = 1;
        private bool _trimLoopFrame = true;

        private bool _sectionsEnabled;
        private List<VATSectionSetup> _sections = new List<VATSectionSetup>();

        // Filled during the bake: one pivot per section per frame per clip, and the rest pose pivots
        // that go into the clip set for scripts and gizmos to read.
        [System.NonSerialized] private Color[] _sectionPivotPixels;
        [System.NonSerialized] private Vector3[] _sectionRestPivots = new Vector3[MAX_SECTIONS];
        [System.NonSerialized] private int _sectionPivotHeight;
        [System.NonSerialized] private float _sectionMargin;

        private bool _perClipRanges;

        private List<VATClipRange> _clipRanges = new List<VATClipRange>();

        private int _textureWidth = 1024;
        private bool _bakeNormals = true;
        private VATPositionPrecision _positionPrecision = VATPositionPrecision.NORMALIZED;
        private VATNormalPrecision _normalPrecision = VATNormalPrecision.OCTAHEDRAL;
        private VATFrameQuality _frameQuality = VATFrameQuality.BALANCED;
        private float _stepTolerance = .002f;

        private bool _removeRootMotion = true;
        private int _rootIndex;
        private bool _lockRootX = true;
        private bool _lockRootY;
        private bool _lockRootZ = true;

        private string _outputPath = "Assets/VAT";
        private string _fileName = "";

        private bool _createMaterial = true;
        private Shader _materialShader;
        private bool _lodGroup;
        private List<VATLodLevel> _lodLevels = new List<VATLodLevel>();
        private bool _restPoseMesh = true;
        private bool _createPrefab = true;
        private bool _frameBlend = true;
        private bool _updateExisting = true;

        private Vector2 _scroll;
        private Vector2 _paneScroll;

        // What the settings pane worked out this pass, for the preview pane to draw from. Both run
        // inside the same OnGUI, so this never carries a stale value into a later frame.
        /*
         * A script recompile reruns the window and keeps only what Unity serialized, and every setting
         * here lives in a plain field. Marking them one by one is what caused the bug this replaces:
         * the per-clip ranges and the authored events were marked, the clip selection was not, so a
         * reload brought back four ranges attached to a selection that had collapsed to one clip and
         * the next bake quietly produced a single-clip texture. CaptureState already describes exactly
         * the state a person can change, so one snapshot of it goes through the serializer instead.
         */
        [SerializeField] private VATBakerState reloadState;

        // Not serialized on purpose. Losing the undo history to a script recompile is a small thing,
        // and far better than pushing dozens of snapshots of asset references through Unity's serializer
        // on every reload.
        [System.NonSerialized] private readonly List<VATBakerState> _undoSteps = new List<VATBakerState>();
        [System.NonSerialized] private readonly List<VATBakerState> _redoSteps = new List<VATBakerState>();
        [System.NonSerialized] private VATBakerState _undoBaseline;
        [System.NonSerialized] private VATBakerState _undoPending;
        [System.NonSerialized] private double _undoSettleAt;
        [System.NonSerialized] private bool _undoDirty;

        [System.NonSerialized] private AnimationClip _paneClip;
        [System.NonSerialized] private int _paneFrameCount;
        [System.NonSerialized] private SkinnedMeshRenderer _paneRenderer;

        private VATBakeSettings _settings;
        private VATBakeSettings _detectedSettings;
        private bool _saveSettings = true;

        [System.NonSerialized] private PreviewRenderUtility _preview;
        [System.NonSerialized] private GameObject _previewInstance;
        [System.NonSerialized] private SkinnedMeshRenderer _previewRenderer;
        [System.NonSerialized] private Transform _previewRoot;
        [System.NonSerialized] private Vector3 _previewRootReference;
        [System.NonSerialized] private Object _previewKeyTarget;
        [System.NonSerialized] private AnimationClip _previewKeyClip;
        [System.NonSerialized] private int _previewKeyRenderer = -1;
        [System.NonSerialized] private int _previewKeyRootIndex = -1;
        [System.NonSerialized] private VATRendererMode _previewKeyMode = (VATRendererMode)(-1);
        [System.NonSerialized] private Bounds _previewBounds;
        [System.NonSerialized] private bool _previewBoundsValid;
        [System.NonSerialized] private double _previewStart;
        [System.NonSerialized] private GameObject _previewDisplay;
        [System.NonSerialized] private Mesh _previewScratch;
        [System.NonSerialized] private readonly List<VATPreviewPart> _previewParts = new List<VATPreviewPart>();

        private bool _showPreview = true;
        private Vector2 _previewOrbit = new Vector2(120f, -15f);
        private float _previewZoom = 4f;
        private bool _previewPlaying = true;
        private int _previewFrame;
        [System.NonSerialized] private int _previewCurrentFrame;

        // Index into _sections, or -1 for none. View state, so it is not worth an undo step.
        [System.NonSerialized] private bool _showBakeSettings;
        [System.NonSerialized] private int _previewLod = -1;
        [System.NonSerialized] private int _highlightSection = -1;
        [System.NonSerialized] private readonly HashSet<int> _expandedSections = new HashSet<int>();
        [System.NonSerialized] private Material _maskMaterial;

        // Test drive. Applied to the preview mesh with the same arithmetic the shader uses, so what the
        // baker shows is what the bake will do rather than an approximation of it.
        [System.NonSerialized] private Vector3 _testRotation;
        [System.NonSerialized] private float _testWeight = 1f;
        [System.NonSerialized] private Vector3 _previewPivot;
        [System.NonSerialized] private bool _previewPivotValid;

        // Both are answers to "how much of this mesh does that bone actually move", which costs a pass
        // over every vertex and must not run once per repaint.
        // Per mesh rather than one slot: the section bone filter asks about every renderer being
        // baked, and a single slot swapped its key on every call, re-reading mesh.boneWeights - which
        // allocates a fresh array per access - once per bone per renderer per repaint.
        [System.NonSerialized] private readonly Dictionary<int, HashSet<int>> _weightedBones =
            new Dictionary<int, HashSet<int>>();

        [System.NonSerialized] private readonly Dictionary<string, HashSet<int>> _boneSubtrees =
            new Dictionary<string, HashSet<int>>();

        [System.NonSerialized] private readonly Dictionary<string, Vector2Int[]> _sectionCoverage =
            new Dictionary<string, Vector2Int[]>();

        // Measured while the preview quantizes, because the whole point of the option is that the
        // result does not look different and a picture cannot show that it did anything at all.
        [System.NonSerialized] private float _normalErrorAverage;
        [System.NonSerialized] private float _normalErrorMax;
        [System.NonSerialized] private int _normalErrorSamples;

        /// <summary>Opens the baker, or focuses it when it is already open.</summary>
        [MenuItem("Tools/MiVertexAnimation/Baker")]
        public static void ShowWindow()
        {
            GetWindow<VATBakerWindow>("Vertex Animation Baker").minSize = new Vector2(380, 480);
        }

        /*
         * Opening the baker on whatever the last bake happened to be is fine for someone who set that
         * bake up, and useless to someone who has just imported a sample: they get a window pointed at
         * a model they have never seen, writing to a folder that means nothing to them.
         *
         * The output path is passed in rather than read from the asset because a sample does not know
         * where it will be imported to, and a path written at authoring time would be wrong everywhere.
         */
        /// <summary>
        /// Opens the baker already loaded with a settings asset, ready to bake.
        /// </summary>
        /// <param name="settings">Settings to load, exactly as the Bake Settings field would.</param>
        /// <param name="outputPath">Where this bake should write, or null to use the asset's own path.</param>
        /// <returns>The baker window, focused.</returns>
        public static VATBakerWindow ShowWith(VATBakeSettings settings, string outputPath = null)
        {
            VATBakerWindow window = GetWindow<VATBakerWindow>("Vertex Animation Baker");
            window.minSize = new Vector2(380, 480);

            if (settings)
            {
                window.ApplySettings(settings);
                if (!string.IsNullOrEmpty(outputPath)) window._outputPath = outputPath;
                window.Repaint();
            }

            return window;
        }

        /*
         * Unity's editor icons are commonly 64 pixels, and a control takes the size of the icon inside it,
         * so the whole window is drawn inside one IconScope rather than sizing each header by hand.
         * A using block rather than a pair of calls because the body returns early in seven places,
         * and any of them would otherwise leave the editor's icon size changed for every other window.
         */
        private void OnGUI()
        {
            using (new VATUi.IconScope(VATUiSettings.ICON_SIZE))
                DrawWindow();

            // GUI.changed is true by the end of the pass if any control took a new value, whatever it was,
            // so one test here covers every field in the window without instrumenting a single control.
            if (GUI.changed) _undoDirty = true;
        }

        /*
         * Ctrl+Z is registered against this window as its shortcut context, so it only fires while the
         * baker has focus and takes priority over the editor's global undo there.
         * That is the whole point: pressing it in the baker should walk back what was done in the baker,
         * not reach past it into a scene edit from a minute ago.
         *
         * Bound through the shortcut system rather than read out of OnGUI, so it turns up in
         * Edit > Shortcuts like everything else and can be rebound if it clashes with something.
         */
        [Shortcut("MiVertexAnimation/Baker Undo", typeof(VATBakerWindow), KeyCode.Z, ShortcutModifiers.Action)]
        private static void UndoShortcut(ShortcutArguments args)
        {
            if (args.context is VATBakerWindow window) window.PerformUndo();
        }

        [Shortcut("MiVertexAnimation/Baker Redo", typeof(VATBakerWindow), KeyCode.Z,
            ShortcutModifiers.Action | ShortcutModifiers.Shift)]
        private static void RedoShortcut(ShortcutArguments args)
        {
            if (args.context is VATBakerWindow window) window.PerformRedo();
        }

        // What Windows hands are used to, alongside the Ctrl+Shift+Z above.
        [Shortcut("MiVertexAnimation/Baker Redo (Y)", typeof(VATBakerWindow), KeyCode.Y, ShortcutModifiers.Action)]
        private static void RedoShortcutAlternate(ShortcutArguments args)
        {
            if (args.context is VATBakerWindow window) window.PerformRedo();
        }

        /// <summary>
        /// Appearance lives in the window's own menu, so it is out of the way of the work.
        /// </summary>
        /// <param name="menu">The window's context menu, from IHasCustomMenu.</param>
        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Icons"), VATUiSettings.Icons, () =>
            {
                VATUiSettings.Icons = !VATUiSettings.Icons;
                Repaint();
            });

            menu.AddItem(new GUIContent("Colours"), VATUiSettings.Colours, () =>
            {
                VATUiSettings.Colours = !VATUiSettings.Colours;
                Repaint();
            });

            menu.AddSeparator(string.Empty);

            if (_undoSteps.Count > 0) menu.AddItem(new GUIContent("Undo"), false, PerformUndo);
            else menu.AddDisabledItem(new GUIContent("Undo"));

            if (_redoSteps.Count > 0) menu.AddItem(new GUIContent("Redo"), false, PerformRedo);
            else menu.AddDisabledItem(new GUIContent("Redo"));

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Preview Beside Settings"), VATUiSettings.SideBySide, () =>
            {
                VATUiSettings.SideBySide = !VATUiSettings.SideBySide;
                Repaint();
            });

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Reset Preview Height"), false, () =>
            {
                VATUiSettings.PreviewHeight = VATUiSettings.DEFAULT_PREVIEW_HEIGHT;
                Repaint();
            });

            menu.AddItem(new GUIContent("Reset Split"), false, () =>
            {
                VATUiSettings.SplitFraction = VATUiSettings.DEFAULT_SPLIT_FRACTION;
                Repaint();
            });
        }

        /*
         * Two panes side by side once there is room for them, because the settings and the thing they
         * change should be on screen together. Stacked, the preview sits below a screenful of options,
         * so changing a value and seeing what it did are never the same glance.
         *
         * Each pane scrolls on its own, which is the point: reading down the output settings on the left
         * leaves the preview where it was on the right instead of dragging it off the top.
         *
         * Below the threshold there is not enough width for two columns of controls to be usable at all,
         * so it falls back to one. Nothing is hidden either way, only stacked.
         */
        private void DrawWindow()
        {
            if (!SideBySide)
            {
                using (EditorGUILayout.ScrollViewScope scroll = new EditorGUILayout.ScrollViewScope(_scroll))
                {
                    _scroll = scroll.scrollPosition;

                    if (DrawSettingsPane()) DrawPreviewPane();
                }

                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                bool ready;
                float leftWidth = Mathf.Clamp(position.width * VATUiSettings.SplitFraction,
                    MIN_PANE_WIDTH, position.width - MIN_PANE_WIDTH - SPLITTER_WIDTH);

                using (EditorGUILayout.ScrollViewScope left =
                       new EditorGUILayout.ScrollViewScope(_scroll, GUILayout.Width(leftWidth)))
                {
                    _scroll = left.scrollPosition;
                    ready = DrawSettingsPane();
                }

                DrawSplitter();

                using (EditorGUILayout.ScrollViewScope right = new EditorGUILayout.ScrollViewScope(_paneScroll))
                {
                    _paneScroll = right.scrollPosition;

                    if (ready) DrawPreviewPane();
                    else
                    {
                        EditorGUILayout.Space(20f);
                        EditorGUILayout.LabelField("The preview appears once the settings on the left are valid.",
                            EditorStyles.centeredGreyMiniLabel);
                    }
                }
            }
        }

        /// <summary>Whether there is room to put the preview beside the settings rather than under them.</summary>
        private bool SideBySide => VATUiSettings.SideBySide && position.width >= SMALLEST_SPLIT_WIDTH;

        /*
         * Returns false instead of tearing down the layout itself, so every guard is one line and the
         * scroll view it sits in is opened and closed in exactly one place.
         */
        /// <summary>
        /// Everything that decides what gets baked, ending with the Bake button.
        /// </summary>
        /// <returns>False when the settings are not complete enough to preview or bake.</returns>
        private bool DrawSettingsPane()
        {

            VATUi.BeginSection("Source", VATIcons.First("Prefab Icon", "GameObject Icon"));

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _target = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Prefab / Object",
                        "A prefab or scene object with a SkinnedMeshRenderer and an Animator."),
                    _target, typeof(GameObject), true);

                if (EditorGUI.EndChangeCheck())
                {
                    // A different object means a different set of clips, so markers and ranges keyed by clip name would be meaningless,
                    // and a name that happens to match would be worse than meaningless.
                    // A reload restores both from the snapshot instead of coming through here.
                    _authoredEvents.Clear();
                    _clipRanges.Clear();
                    _selectedEvent = -1;
                    Refresh();
                }

                /*
                 * Folded away, because a bake settings asset is something you reach for at the start of a session and then never again.
                 * As its own section at the top of the window it was costing a heading and a row on every repaint to say nothing.
                 */
                bool loaded = _settings;
                if (VATUi.Button(VATUi.Content(loaded && !_showBakeSettings
                        ? "Bake Settings *" : "Bake Settings",
                        loaded
                            ? $"Loaded from '{_settings.name}'. Load another, or start this object over."
                            : "Load a saved bake, or start this object over.",
                        VATIcons.First("Settings", "_Popup", "EditorSettings Icon")),
                        _showBakeSettings ? VATUi.PRIMARY : (loaded ? VATUi.CAUTION : Color.white),
                        GUILayout.Width(140f)))
                    _showBakeSettings = !_showBakeSettings;
            }

            if (_showBakeSettings) DrawBakeSettingsPanel();

            if (!_target)
            {
                EditorGUILayout.HelpBox("Assign a prefab with a SkinnedMeshRenderer and an Animator.", MessageType.Info);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            if (_renderers.Length == 0)
                Refresh(); // recover after a domain reload

            if (_renderers.Length == 0)
            {
                EditorGUILayout.HelpBox("No SkinnedMeshRenderer found in this object or its children.", MessageType.Error);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            if (!_target.GetComponentInChildren<Animator>())
            {
                EditorGUILayout.HelpBox("No Animator found. SampleAnimation needs one to pose the rig.", MessageType.Error);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            if (_renderers.Length <= 1)
                _rendererMode = VATRendererMode.SELECTED;
            else
            {
                _rendererMode = (VATRendererMode)EditorGUILayout.Popup(
                    new GUIContent("Renderers",
                        "This object has several SkinnedMeshRenderers. Choose how they are baked."),
                    (int)_rendererMode,
                    new[]
                    {
                        new GUIContent("Selected only"),
                        new GUIContent("Separate parts, one prefab"),
                        new GUIContent("Combined into one mesh")
                    });

                if (_rendererMode == VATRendererMode.SELECTED)
                {
                    EditorGUI.BeginChangeCheck();
                    _rendererIndex = EditorGUILayout.Popup(
                        new GUIContent("Renderer", "Bake this renderer only."),
                        _rendererIndex, _renderers.Select(r => r.name).ToArray());
                    if (EditorGUI.EndChangeCheck())
                        _rootIndex = DetectRootIndex(_renderers[Mathf.Clamp(_rendererIndex, 0, _renderers.Length - 1)]);

                    // Nothing here builds an LODGroup. This is how to hand-build one, which is the
                    // only sense in which this package has anything to do with LOD.
                    EditorGUILayout.HelpBox(
                        "To put a VAT character in an LODGroup, bake each level this way and assemble " +
                        "the prefabs yourself - with identical Start/End Frame, Frame Step and clip, " +
                        "or the levels pop as they swap.",
                        MessageType.None);
                }
                else if (_rendererMode == VATRendererMode.SEPARATE_PARTS)
                {
                    _rendererIndex = 0;
                    EditorGUILayout.HelpBox(
                        $"{_renderers.Length} renderers, each baked to its own texture pair and material, " +
                        "assembled into one prefab as children.\n" +
                        "Every part keeps its own base map, and all parts are sampled from the same " +
                        "frames, so they cannot drift apart." +
                        "\nEach part is one renderer, so its mesh is copied rather than rebuilt and " +
                        "keeps its Unity 6 Mesh LOD levels either way." ,
                        MessageType.Info);
                }
                else
                {
                    _rendererIndex = 0;
                    EditorGUILayout.HelpBox(
                        $"{_renderers.Length} renderers merged into a single mesh asset and one texture pair, " +
                        "with one material generated per submesh so each part keeps its own base map.\n" +
                        "Cheapest in memory. The merged mesh has no Mesh LOD levels of its own, but the " +
                        "LOD Group section still works: it merges each level as it merges the mesh.",
                        MessageType.Info);
                }
            }

            SkinnedMeshRenderer renderer = _renderers[Mathf.Clamp(_rendererIndex, 0, _renderers.Length - 1)];
            Mesh mesh = renderer.sharedMesh;
            if (!mesh)
            {
                EditorGUILayout.HelpBox("The selected SkinnedMeshRenderer has no mesh.", MessageType.Error);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            AnimationClip clip = ResolveClip();
            if (!clip)
            {
                EditorGUILayout.HelpBox("No AnimationClip available. Assign an Animator Controller with clips, or drop a clip below.", MessageType.Error);
                _explicitClip = (AnimationClip)EditorGUILayout.ObjectField("Clip", _explicitClip, typeof(AnimationClip), false);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            VATUi.EndSection();

            DrawLodGroupSettings(renderer);

            VATUi.BeginSection("Animation", VATIcons.ForType(typeof(AnimationClip)));

            if (_clips.Length > 0)
            {
                _bakeClips.RemoveAll(c => !c || !_clips.Contains(c));
                if (_bakeClips.Count == 0) _bakeClips.Add(_clips[0]);

                if (_clips.Length == 1)
                {
                    // Nothing to choose between.
                    EditorGUILayout.LabelField("Clip", _clips[0].name);
                }
                else
                    DrawClipSelection();
            }

            _explicitClip = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent("Override Clip", "Optional. Bakes this clip alone, ignoring the list."),
                _explicitClip, typeof(AnimationClip), false);

            List<AnimationClip> bakeClips = SelectedClips();
            if (bakeClips.Count == 0)
            {
                EditorGUILayout.HelpBox("Tick at least one clip to bake.", MessageType.Warning);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            if (bakeClips.Count > MAX_CLIPS)
            {
                EditorGUILayout.HelpBox(
                    $"{bakeClips.Count} clips selected but the shader holds {MAX_CLIPS}. " +
                    $"Raise VAT_MAX_CLIPS in VAT_Core.hlsl and MAX_CLIPS here, or untick some.",
                    MessageType.Error);
                DestroyPreview();
                VATUi.EndSection();
                return false;
            }

            DrawPerClipRangeBar(bakeClips);

            // Re-resolve after the pickers and the clip bar, so a clip change takes effect on this
            // repaint rather than the next one.
            clip = bakeClips.Contains(ResolveClip()) ? ResolveClip() : bakeClips[0];

            bool singleClip = bakeClips.Count == 1;
            bool perClip = UsePerClipRanges(bakeClips.Count);
            int sourceFrames = FrameCount(clip);

            if (perClip)
            {
                // Read out of the selected clip's own range every pass rather than kept in these fields
                // between passes, which is what makes picking a clip on the bar show that clip's numbers
                // instead of whichever one was selected last.
                VATClipRange range = RangeFor(clip);
                _startFrame = range.startFrame;
                _endFrame = range.endFrame;
                _frameStep = range.frameStep;
                _trimLoopFrame = range.trimLoopFrame;
                _frameRangeClip = clip;
            }
            else if (!ReferenceEquals(clip, _frameRangeClip))
            {
                // A new clip gets the full range. A stale range from a longer clip would otherwise
                // leave the sliders out of bounds and bake frames the clip does not have.
                _frameRangeClip = clip;
                _startFrame = 0;
                _endFrame = sourceFrames;
            }

            _startFrame = Mathf.Clamp(_startFrame, 0, sourceFrames - 1);
            _endFrame = Mathf.Clamp(_endFrame, _startFrame + 1, sourceFrames);

            EditorGUILayout.LabelField($"Source: {clip.length:0.###}s @ {clip.frameRate} fps ({sourceFrames} frames)");

            if (singleClip || perClip)
            {
                _startFrame = EditorGUILayout.IntSlider("Start Frame", _startFrame, 0, sourceFrames - 1);
                _endFrame = EditorGUILayout.IntSlider("End Frame", _endFrame, _startFrame + 1, sourceFrames);
            }
            else
            {
                _startFrame = 0;
                _endFrame = sourceFrames;
                EditorGUILayout.LabelField(" ", "Every selected clip bakes its full range.", EditorStyles.miniLabel);
            }

            _frameStep = EditorGUILayout.IntSlider(
                new GUIContent("Frame Step", "Bake every Nth frame. Halves texture size per step, and frame blending hides most of the loss."),
                _frameStep, 1, 10);

            _trimLoopFrame = EditorGUILayout.Toggle(
                new GUIContent("Trim Looping Duplicate",
                    "A seamlessly looping clip ends on the same pose it starts on, so baking the full " +
                    "range stores that pose twice - the animation plays slightly slow and hitches once " +
                    "per loop. This drops the last frame, but ONLY when it actually matches the first, " +
                    "so non-looping clips are left alone. Turn it off if a clip is trimmed wrongly."),
                _trimLoopFrame);

            if (perClip)
            {
                VATClipRange range = RangeFor(clip);
                range.startFrame = _startFrame;
                range.endFrame = _endFrame;
                range.frameStep = _frameStep;
                range.trimLoopFrame = _trimLoopFrame;
            }

            DrawAutoFrameStep(renderer, bakeClips);

            _blendDuration = EditorGUILayout.FloatField(
                new GUIContent("Clip Blend Duration",
                    "Seconds to cross-fade when VATAnimator switches clips. 0 snaps instantly. " +
                    "Stored on the material and editable there afterwards."),
                Mathf.Max(0f, _blendDuration));

            VATUi.EndSection();

            DrawRootMotionSection(renderer);

            VATUi.BeginSection("Texture", VATIcons.ForType(typeof(Texture2D)));

            _textureWidth = EditorGUILayout.IntPopup("Width", _textureWidth,
                new[] { "256", "512", "1024", "2048", "4096" },
                new[] { 256, 512, 1024, 2048, 4096 });
            _positionPrecision = (VATPositionPrecision)EditorGUILayout.EnumPopup(
                new GUIContent("Position Precision",
                    "How finely a vertex can be placed. Half floats spend their bits on a range a model " +
                    "never uses, so on a two metre rig they step by about half a millimetre and every " +
                    "vertex snaps to its own grid - which up close reads as the surface swimming."),
                _positionPrecision);

            DrawPrecisionNote();
            EditorGUILayout.Space(2f);

            // Kept directly above the precision it governs, so the indented rows below read as the
            // settings belonging to this toggle rather than as a second, unrelated group.
            _bakeNormals = EditorGUILayout.Toggle(
                new GUIContent("Bake Normals",
                    "Off halves the texture memory and drops two fetches per vertex in every pass. " +
                    "Lighting then uses the mesh's bind-pose normals, which are wrong wherever the " +
                    "animation bends the surface - the preview shows exactly what that looks like."),
                _bakeNormals);

            if (_bakeNormals)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();

                _normalPrecision = (VATNormalPrecision)EditorGUILayout.EnumPopup(
                    new GUIContent("Normal Precision",
                        "How a normal is stored. A normal has two degrees of freedom, so xyz spends a " +
                        "third of every texel restating what the other channels already said. " +
                        "Octahedral keeps two channels at 16 bits each, which fits the same four bytes " +
                        "as three 8-bit channels and lands about a hundred times closer."),
                    _normalPrecision);

                if (EditorGUI.EndChangeCheck()) DestroyPreview();

                EditorGUILayout.LabelField(
                    $"About {NormalErrorDegrees():0.####} degrees of error, " +
                    $"{NormalBytesPerPixel()} bytes per vertex per frame.",
                    EditorStyles.miniLabel);

                if (_normalPrecision != VATNormalPrecision.HALF && _normalErrorSamples > 0)
                    EditorGUILayout.LabelField(
                        $"This pose: {_normalErrorAverage:0.00} deg average error, "
                        + $"{_normalErrorMax:0.00} deg worst, over {_normalErrorSamples} vertices",
                        EditorStyles.miniLabel);

                EditorGUI.indentLevel--;
            }

            string unsupported = UnsupportedFormats();
            if (unsupported.Length > 0)
                EditorGUILayout.HelpBox(
                    $"This machine reports no support for the chosen storage:\n{unsupported}",
                    MessageType.Warning);

            int frameCount = ((_endFrame - _startFrame) / _frameStep) + 1;
            bool tooTall = DrawSizeEstimate(mesh, bakeClips);

            VATUi.EndSection();

            DrawSectionSettings(renderer);

            VATUi.BeginSection("Output", VATIcons.First("Folder Icon", "FolderOpened Icon"));

            using (new EditorGUILayout.HorizontalScope())
            {
                _outputPath = EditorGUILayout.TextField("Folder", _outputPath);

                if (VATUi.Button(VATUi.Content("Browse...", "Pick the output folder instead of typing its path.",
                        VATIcons.Named("FolderOpened Icon")), VATUi.GENTLE, GUILayout.Width(96f)))
                {
                    BrowseForOutputFolder();
                }
            }

            _fileName = EditorGUILayout.TextField(new GUIContent("Name", "Leave empty to use <object>_<clip>."), _fileName);
            _saveSettings = EditorGUILayout.Toggle(
                new GUIContent("Save Bake Settings",
                    "Write these settings next to the outputs so this bake can be reproduced later " +
                    "without setting everything up again. Editor-only, never enters a build."),
                _saveSettings);
            _createMaterial = EditorGUILayout.Toggle(new GUIContent("Create Material", "Fills in all five layout values for you."), _createMaterial);

            if (_createMaterial)
            {
                if (!_materialShader) _materialShader = Shader.Find(SHADER_NAME);

                EditorGUI.indentLevel++;
                _materialShader = (Shader)EditorGUILayout.ObjectField(
                    new GUIContent("Shader",
                        "Which shader the generated materials use. Any shader that reads a VAT works, " +
                        "including one of your own - see the README for what it has to declare."),
                    _materialShader, typeof(Shader), false);

                _frameBlend = EditorGUILayout.Toggle(new GUIContent("Frame Blend", "Smooth playback, at the cost of 4 texture fetches per vertex instead of 2."), _frameBlend);
                _restPoseMesh = EditorGUILayout.Toggle(
                    new GUIContent("Bake Rest Pose Mesh",
                        "Write a mesh holding the first baked frame, instead of pointing the prefab at " +
                        "the imported one. The shader replaces every vertex anyway, so this only shows " +
                        "when something else draws it - while a shader variant compiles, or if one " +
                        "fails - and then it is the character standing still rather than the bind pose " +
                        "at whatever scale the source file used. Turn it off only to keep Unity 6 Mesh " +
                        "LOD, which lives on the imported asset and cannot survive a copy."),
                    _restPoseMesh);

                _createPrefab = EditorGUILayout.Toggle(new GUIContent("Create Prefab", "MeshFilter + MeshRenderer + correct animated bounds."), _createPrefab);
                EditorGUI.indentLevel--;

                DrawShaderWarning();
            }

            DrawUpdateExistingSection(clip);

            VATUi.EndSection();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(tooTall))
            {
                if (VATUi.Button(VATUi.Content("Bake VAT", VATIcons.First("Lighting", "SceneViewLighting", "PlayButton")),
                        VATUi.PRIMARY, GUILayout.Height(30f)))
                {
                    Bake(renderer, bakeClips);
                }
            }

            _paneClip = clip;
            _paneFrameCount = frameCount;
            _paneRenderer = renderer;
            return true;
        }

        /// <summary>
        /// The preview and the event editor, drawn from what the settings pane worked out this pass.
        /// </summary>
        private void DrawPreviewPane()
        {
            AnimationClip clip = _paneClip;
            int frameCount = _paneFrameCount;

            _showPreview = EditorGUILayout.Foldout(_showPreview,
                VATUi.Content("Preview", VATIcons.First("ViewToolOrbit", "SceneViewCamera")), true);

            if (_showPreview)
            {
                EnsurePreview(clip);
                DrawPreview(GUILayoutUtility.GetRect(256f, VATUiSettings.PreviewHeight, GUILayout.ExpandWidth(true)),
                    clip, frameCount);
                DrawPreviewResizeGrip();
                DrawPreviewControls(clip, frameCount);

                string scope = _renderers.Length > 1 && _rendererMode == VATRendererMode.SELECTED
                    ? $"Showing '{_paneRenderer.name}' only - the renderer this bake writes."
                    : $"Showing all {_previewParts.Count} renderer(s) being baked.";

                EditorGUILayout.LabelField($"{scope} Drag to orbit, scroll to zoom.", EditorStyles.miniLabel);
            }
            else
                DestroyPreview();

            EditorGUILayout.Space();
            DrawEventsSection(clip, frameCount, SelectedClips());
        }

        /// <summary>
        /// The bake set: a count, an add button, and the chosen clips in the order they get baked,
        /// so the slice index each one lands on is visible without counting rows.
        /// </summary>
        private void DrawClipSelection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{_bakeClips.Count}/{_clips.Length} animations selected to bake");

                Rect addRect = GUILayoutUtility.GetRect(new GUIContent("+ Add"),
                    EditorStyles.miniButton, GUILayout.Width(60f));

                if (GUI.Button(addRect, "+ Add", EditorStyles.miniButton))
                {
                    PopupWindow.Show(addRect, new VATClipPickerPopup(_clips, _bakeClips, picked =>
                    {
                        _bakeClips.Add(picked);
                        MarkEdited();
                        Repaint();
                    }));
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                for (int i = 0; i < _bakeClips.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(_bakeClips.Count == 1))
                        {
                            if (VATUi.Button(new GUIContent("-", "Drop this clip from the bake."),
                                    VATUi.DESTRUCTIVE, EditorStyles.miniButton, GUILayout.Width(22f)))
                            {
                                _bakeClips.RemoveAt(i);
                                MarkEdited();
                                GUIUtility.ExitGUI();
                            }
                        }

                        EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(16f));
                        EditorGUILayout.LabelField(_bakeClips[i].name);

                        if (GUILayout.Button("preview", EditorStyles.miniButton, GUILayout.Width(58f)))
                            _clipIndex = System.Array.IndexOf(_clips, _bakeClips[i]);
                    }
                }
            }
        }

        /*
         * A bar rather than a row of numbers per clip, so the Start/End/Step controls stay exactly where
         * they were and only change which clip they point at. One set of sliders in one place, whichever
         * clip is being edited.
         *
         * Picking a clip here also moves the preview to it, because sliders that trim one clip while the
         * preview plays another are the same trap as an event marker that does not follow its own frame.
         */
        /// <summary>
        /// The per-clip range toggle, and the bar for choosing which clip the sliders below are editing.
        /// </summary>
        private void DrawPerClipRangeBar(List<AnimationClip> bakeClips)
        {
            if (bakeClips.Count <= 1) return;

            _perClipRanges = EditorGUILayout.Toggle(
                new GUIContent("Per-Clip Ranges",
                    "Give each clip its own Start Frame, End Frame and Frame Step, instead of baking every " +
                    "one in full at the same step. Slices pad up to the longest clip, so stepping a long " +
                    "idle harder than a short attack shrinks the texture twice over."),
                _perClipRanges);

            if (!_perClipRanges) return;

            int current = Mathf.Max(0, bakeClips.IndexOf(ResolveClip()));
            GUIContent[] tabs = new GUIContent[bakeClips.Count];

            for (int i = 0; i < bakeClips.Count; i++)
            {
                VATClipRange range = RangeFor(bakeClips[i]);
                tabs[i] = new GUIContent(bakeClips[i].name,
                    $"{bakeClips[i].name}\nframes {range.startFrame} to {range.endFrame}, " +
                    $"step {range.frameStep}, {range.Frames} baked");
            }

            // Wrapped into rows rather than one strip, so sixteen clips stay readable in a narrow pane.
            int columns = Mathf.Clamp(bakeClips.Count, 1, 3);

            EditorGUI.BeginChangeCheck();
            int picked = GUILayout.SelectionGrid(current, tabs, columns, EditorStyles.miniButton);

            if (EditorGUI.EndChangeCheck())
            {
                _clipIndex = System.Array.IndexOf(_clips, bakeClips[picked]);
                MarkEdited();
            }
        }

        /*
         * Sits under Frame Step because Frame Step is what it writes, and the quality dropdown sits on the
         * same row as the button because it is that button's argument and nothing else.
         *
         * It used to live in the Texture section as a "Memory Preset" that also set the normal format.
         * That was two unrelated levers under one name: one changed the bake as you touched it, the other
         * did nothing at all until a button was pressed, and the preset could be silently contradicted by
         * ticking the checkbox it had just set. Splitting them is what makes either of them readable.
         */
        /// <summary>
        /// The Auto Frame Step row: a quality to measure against, and the button that applies it.
        /// </summary>
        private void DrawAutoFrameStep(SkinnedMeshRenderer renderer, List<AnimationClip> bakeClips)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Auto Frame Step",
                    "Measures each clip and writes in the coarsest Frame Step it survives. " +
                    "Changes nothing until the button is pressed."));

                EditorGUI.BeginChangeCheck();
                _frameQuality = (VATFrameQuality)EditorGUILayout.EnumPopup(_frameQuality);
                if (EditorGUI.EndChangeCheck()) ApplyFrameQuality();

                if (VATUi.Button(VATUi.Content("Measure",
                        "Sample every frame of every selected clip, work out the coarsest step each one " +
                        "survives at this quality, and write it into their Frame Step. The size estimate " +
                        "and the preview both follow, so nothing is baked until you say so.",
                        VATIcons.First("Refresh", "RotateTool")), VATUi.CAUTION, GUILayout.Width(88f)))
                {
                    AnalyseFrameSteps(renderer, bakeClips);
                    GUIUtility.ExitGUI();
                }
            }

            if (_frameQuality != VATFrameQuality.CUSTOM) return;

            EditorGUI.indentLevel++;
            _stepTolerance = EditorGUILayout.Slider(
                new GUIContent("Tolerance",
                    "How far a frame rebuilt from its neighbours may sit from the real one, as a fraction " +
                    "of the model's own size. Measure raises each clip's step until it would cross this."),
                _stepTolerance, .0001f, .05f);
            EditorGUI.indentLevel--;
        }

        private void ApplyFrameQuality()
        {
            // Custom is the one that changes nothing, because then the tolerance below is yours.
            switch (_frameQuality)
            {
                case VATFrameQuality.PRECISE:
                    _stepTolerance = PRECISE_TOLERANCE;
                    break;

                case VATFrameQuality.BALANCED:
                    _stepTolerance = BALANCED_TOLERANCE;
                    break;

                case VATFrameQuality.AGGRESSIVE:
                    _stepTolerance = AGGRESSIVE_TOLERANCE;
                    break;
            }

            MarkEdited();
        }

        /*
         * One Frame Step for a whole bake is decided by its fastest clip: an attack that needs every frame
         * drags a forty frame idle along with it. Measuring each clip separately is what lets the idle be
         * stepped by three while the attack stays at one.
         *
         * The answer is written into the per-clip ranges rather than used directly at bake time, so the
         * size estimate and the preview both show what it did before anything is baked, and any clip whose
         * answer you disagree with can be changed by hand afterwards.
         */
        /// <summary>
        /// Works out the coarsest Frame Step each clip survives at the current tolerance, and writes it in.
        /// </summary>
        /// <param name="sourceRenderer">The renderer chosen in the window, used by Selected mode.</param>
        /// <param name="bakeClips">Clips to measure, which are the ones that would be baked.</param>
        private void AnalyseFrameSteps(SkinnedMeshRenderer sourceRenderer, List<AnimationClip> bakeClips)
        {
            if (_stepTolerance <= 0f || bakeClips.Count == 0) return;

            // Each clip gets its own answer, which is the point, so there has to be somewhere per clip
            // to put it.
            if (bakeClips.Count > 1) _perClipRanges = true;

            GameObject instance = Object.Instantiate(_target);
            instance.hideFlags = HideFlags.HideAndDontSave;

            System.Text.StringBuilder log = new System.Text.StringBuilder();
            log.AppendLine($"[VAT] Auto Frame Step, tolerance {_stepTolerance:P2} of the model");

            try
            {
                foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
                    animator.runtimeAnimatorController = null;

                SkinnedMeshRenderer[] instanceRenderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                List<VATPartBake> parts = new List<VATPartBake>();
                BuildParts(parts, instanceRenderers, sourceRenderer, "analysis");

                if (parts.Count == 0 || parts[0].Targets.Count == 0) return;

                Transform rootTransform = ResolveRoot(instance, parts[0].Targets[0], _rootIndex);
                Vector3 rootReference = rootTransform
                    ? ToBakeSpace(instance.transform, rootTransform.position)
                    : Vector3.zero;

                Mesh scratch = new Mesh();
                List<Vector3> buffer = new List<Vector3>();

                try
                {
                    for (int i = 0; i < bakeClips.Count; i++)
                    {
                        AnimationClip clip = bakeClips[i];

                        EditorUtility.DisplayProgressBar("Measuring frames",
                            $"{clip.name}  ({i + 1}/{bakeClips.Count})", (float)i / bakeClips.Count);

                        VATClipRange range = EffectiveRange(clip, bakeClips.Count);
                        int step = MeasureFrameStep(instance, parts, clip, range, rootTransform, rootReference,
                            scratch, buffer);

                        if (bakeClips.Count > 1) RangeFor(clip).frameStep = step;
                        else _frameStep = step;

                        int before = range.Frames;
                        log.AppendLine($"  '{clip.name}'  step {step}  ({before} frames -> " +
                                       $"{((range.endFrame - range.startFrame) / step) + 1})");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(scratch);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Object.DestroyImmediate(instance);
            }

            MarkEdited();
            Repaint();
            Debug.Log(log.ToString());
        }

        /// <summary>
        /// Measures one clip at every step until one of them costs too much.
        /// </summary>
        /// <returns>The coarsest step that stays inside the tolerance, never less than 1.</returns>
        private int MeasureFrameStep(GameObject instance, List<VATPartBake> parts, AnimationClip clip,
                                     VATClipRange range, Transform rootTransform, Vector3 rootReference,
                                     Mesh scratch, List<Vector3> buffer)
        {
            int sourceFrames = Mathf.Max(1, range.endFrame - range.startFrame + 1);
            List<Vector3[]> poses = new List<Vector3[]>(sourceFrames);

            for (int f = 0; f < sourceFrames; f++)
            {
                CapturePose(instance, parts, clip, (range.startFrame + f) / clip.frameRate,
                    rootTransform, rootReference, scratch, buffer);

                poses.Add(buffer.ToArray());
            }

            if (poses[0].Length == 0) return 1;

            // Scaled to the model, so the same tolerance behaves the same in metres or centimetres.
            float extent = 0f;
            for (int v = 0; v < poses[0].Length; v++)
                extent = Mathf.Max(extent, poses[0][v].magnitude);

            float tolerance = Mathf.Max(1e-6f, extent * _stepTolerance);

            // Stops at the first step that fails rather than trying all of them, so a coarser step that
            // happens to land on the motion cannot be chosen over a finer one that does not.
            int best = 1;
            for (int step = 2; step <= MAX_AUTO_STEP && step < sourceFrames; step++)
            {
                if (!StepWithinTolerance(poses, step, tolerance, _frameBlend)) break;

                best = step;
            }

            return best;
        }

        /*
         * Rebuilds every source frame the way the shader would and compares it against the real one.
         * With Frame Blend on that is a lerp between the two kept frames either side, and with it off the
         * kept frame simply holds, which is why the answer differs depending on that setting.
         */
        /// <summary>
        /// Whether a step reproduces every frame of a clip closely enough.
        /// </summary>
        private static bool StepWithinTolerance(List<Vector3[]> poses, int step, float tolerance, bool blend)
        {
            int count = poses.Count;
            int lastKept = ((count - 1) / step) * step;
            float squared = tolerance * tolerance;

            for (int f = 0; f < count; f++)
            {
                // Past the last kept frame the clip ends early and that frame is what stays on screen,
                // so comparing against it is what makes a step that truncates the clip fail.
                int lower = Mathf.Min((f / step) * step, lastKept);
                int upper = Mathf.Min(lower + step, lastKept);
                float weight = blend && upper > lower ? Mathf.Clamp01((float)(f - lower) / (upper - lower)) : 0f;

                Vector3[] a = poses[lower];
                Vector3[] b = poses[upper];
                Vector3[] truth = poses[f];

                for (int v = 0; v < truth.Length; v++)
                    if ((Vector3.Lerp(a[v], b[v], weight) - truth[v]).sqrMagnitude > squared) return false;
            }

            return true;
        }

        private void DrawRootMotionSection(SkinnedMeshRenderer renderer)
        {
            VATUi.BeginSection("Root Motion", VATIcons.First("Avatar Icon", "AvatarSelector", "Animator Icon"));

            _removeRootMotion = EditorGUILayout.Toggle(
                new GUIContent("Bake In Place",
                    "SampleAnimation poses the rig straight from the clip's curves, so it ignores the " +
                    "Animator's Apply Root Motion setting and any travel in the clip gets baked into the " +
                    "vertices. This subtracts that travel back out."),
                _removeRootMotion);

            // Hidden rather than greyed out. Nothing under here means anything with the travel left in,
            // and a disabled control still costs the same row of window as a live one.
            if (!_removeRootMotion)
            {
                VATUi.EndSection();
                return;
            }

            EditorGUI.indentLevel++;

            string[] rootOptions = BuildRootOptions(renderer);
            _rootIndex = EditorGUILayout.Popup(
                new GUIContent("Root Transform",
                    "The transform carrying the travel. A rig with a dedicated root bone gives the " +
                    "cleanest result - if you pick the hips instead, the locked axes also flatten hip sway."),
                Mathf.Clamp(_rootIndex, 0, rootOptions.Length - 1), rootOptions);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Lock Axes",
                "Axes are in the MESH's local space, not world space - on a Blender import that is " +
                "often rotated. If locking Z does not stop the travel, try the other axes. " +
                "Leave Y free so jumps and crouches survive."));
            _lockRootX = GUILayout.Toggle(_lockRootX, "X", EditorStyles.miniButtonLeft);
            _lockRootY = GUILayout.Toggle(_lockRootY, "Y", EditorStyles.miniButtonMid);
            _lockRootZ = GUILayout.Toggle(_lockRootZ, "Z", EditorStyles.miniButtonRight);
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;

            VATUi.EndSection();
        }

        /// <summary>
        /// Reports the texture set each part will need, and whether any of them busts the hardware limit.
        /// </summary>
        /// <param name="mesh">The selected renderer's mesh, used when only one renderer is baked.</param>
        /// <param name="bakeClips">Clips that become slices.</param>
        /// <returns>True when a part exceeds the maximum texture height, which blocks the bake.</returns>
        private bool DrawSizeEstimate(Mesh mesh, List<AnimationClip> bakeClips)
        {
            int sliceFrames = 1;
            int usedFrames = 0;

            foreach (AnimationClip bakeClip in bakeClips)
            {
                int frames = EffectiveRange(bakeClip, bakeClips.Count).Frames;
                sliceFrames = Mathf.Max(sliceFrames, frames);
                usedFrames += frames;
            }

            Mesh[] partMeshes = _rendererMode == VATRendererMode.SELECTED
                ? new[] { mesh }
                : _renderers.Where(r => r.sharedMesh).Select(r => r.sharedMesh).ToArray();

            int[] partVertexCounts = _rendererMode == VATRendererMode.SEPARATE_PARTS
                ? partMeshes.Select(m => m.vertexCount).ToArray()
                : new[] { partMeshes.Sum(m => m.vertexCount) };

            int tallest = 0;
            float megabytes = 0f;
            List<string> sizeLines = new List<string>();

            foreach (int partVerts in partVertexCounts)
            {
                int partRows = Mathf.CeilToInt((float)partVerts / _textureWidth);
                int partHeight = sliceFrames * partRows;
                tallest = Mathf.Max(tallest, partHeight);

                // Normals are four half floats unless they are compacted,
                // and nothing at all when they are not baked.
                long texels = _textureWidth * (long)partHeight * bakeClips.Count;
                long positionBytes = texels * PositionBytesPerPixel();
                long normalBytes = _bakeNormals ? texels * NormalBytesPerPixel() : 0L;
                megabytes += (positionBytes + normalBytes) / (1024f * 1024f);

                sizeLines.Add($"{partVerts} verts -> {_textureWidth} x {partHeight} x {bakeClips.Count} slice(s) " +
                              $"({partRows} rows/frame)");
            }

            // Slices are all the same size, so the shortest clips are paying for the longest one.
            // Worth saying out loud, because it is the number per-clip Frame Step exists to bring down.
            int allocatedFrames = sliceFrames * bakeClips.Count;
            if (bakeClips.Count > 1 && allocatedFrames > usedFrames)
            {
                int padding = Mathf.RoundToInt(100f * (allocatedFrames - usedFrames) / allocatedFrames);
                sizeLines.Add($"slices pad to {sliceFrames} frames for {usedFrames} frames of animation, " +
                              $"so {padding}% of the texture is padding");
            }

            EditorGUILayout.HelpBox(
                $"{bakeClips.Count} clip(s), up to {sliceFrames} frames each  |  " +
                $"{partVertexCounts.Length} texture set(s)  |  {megabytes:0.00} MB total\n" +
                string.Join("\n", sizeLines),
                MessageType.None);

            bool tooTall = tallest > MAX_TEXTURE_DIMENSION;
            if (tooTall)
            {
                EditorGUILayout.HelpBox(
                    $"Height {tallest} exceeds the {MAX_TEXTURE_DIMENSION} limit. Increase width or frame step.",
                    MessageType.Error);
            }

            return tooTall;
        }

        /*
         * Checked here rather than left to fail at bake time, because setting a property a shader does not
         * have is silently ignored: the bake would finish, write its textures, and produce a material that
         * simply never animates, with nothing anywhere saying why.
         */
        /// <summary>
        /// Says what the chosen shader is missing, if anything.
        /// </summary>
        private void DrawShaderWarning()
        {
            if (!_materialShader)
            {
                EditorGUILayout.HelpBox(
                    $"No shader assigned, and '{SHADER_NAME}' was not found either, so no material can be " +
                    "written. Textures will still be baked.",
                    MessageType.Error);

                return;
            }

            List<string> missing = new List<string>();
            foreach (string property in REQUIRED_SHADER_PROPERTIES)
                if (_materialShader.FindPropertyIndex(property) < 0) missing.Add(property);

            if (missing.Count == 0) return;

            EditorGUILayout.HelpBox(
                $"'{_materialShader.name}' does not declare {string.Join(", ", missing)}, so the baker has " +
                "nowhere to put the animation and the material will not move. See the README for the " +
                "smallest set a shader needs.",
                MessageType.Warning);
        }

        private void DrawUpdateExistingSection(AnimationClip clip)
        {
            string outputName = BaseName(clip);
            bool materialExists = _createMaterial &&
                AssetDatabase.LoadAssetAtPath<Material>($"{_outputPath}/{outputName}.mat");
            bool prefabExists = _createMaterial && _createPrefab &&
                AssetDatabase.LoadAssetAtPath<GameObject>($"{_outputPath}/{outputName}.prefab");

            if (!materialExists && !prefabExists) return;

            _updateExisting = EditorGUILayout.Toggle(
                new GUIContent("Update Existing",
                    "Rewrite the existing assets in place instead of making numbered copies. " +
                    "Their GUIDs are kept, so everything already referencing them keeps working."),
                _updateExisting);

            EditorGUILayout.HelpBox(_updateExisting
                ? $"'{outputName}' already exists and will be updated in place. Scene and prefab " +
                  "references survive, and hand-tuned surface settings (base map, colour, smoothness, " +
                  "metallic) are left alone - only the VAT properties are rewritten."
                : $"'{outputName}' already exists. A numbered copy will be created and nothing " +
                  "already in your scenes will pick up this bake.",
                _updateExisting ? MessageType.Info : MessageType.Warning);
        }

        private void Update()
        {
            // The preview animates on editor time, so it needs a steady repaint. Paused it needs
            // none, which is what makes scrubbing a still frame free.
            if (_preview != null && _showPreview && _previewPlaying) Repaint();

            // Driven from here rather than from OnGUI, because the settle delay has to expire on its own
            // even when nothing is repainting the window.
            if (_undoDirty) CommitUndoStep();
        }

        private void OnEnable()
        {
            // Restoring rather than rebuilding: the target comes back from the snapshot, which makes
            // RestoreState call Refresh for the renderer and clip lists and then write every setting
            // back over the top of it.
            if (reloadState != null) RestoreState(reloadState);

            // Taken after the restore and before anything can be changed, so the first edit already
            // has a state to return to.
            _undoBaseline = CaptureState();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            // Runs before an assembly reload as well as on close, which is what makes this the handoff.
            reloadState = CaptureState();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            DestroyPreview();
        }

        /*
         * Entering or leaving play mode tears down the instance and the meshes the preview is built from,
         * whether or not this window is the one on screen. Dropping it here means the next repaint builds
         * a fresh one, rather than the window coming back and drawing through references to objects that
         * no longer exist.
         */
        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            DestroyPreview();
            Repaint();
        }

        /// <summary>
        /// Play, pause and a frame slider. Placing an event needs the preview to hold still, and a
        /// paused preview is also what the event track's playhead follows.
        /// </summary>
        private void DrawPreviewControls(AnimationClip clip, int frameCount)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool playing = GUILayout.Toggle(_previewPlaying, _previewPlaying ? "Pause" : "Play",
                    EditorStyles.miniButton, GUILayout.Width(54f));

                if (playing != _previewPlaying)
                {
                    _previewPlaying = playing;

                    // Resuming rewinds the clock to where the scrubbed frame sits in the loop,
                    // so play carries on from the frame on screen instead of jumping.
                    if (playing)
                    {
                        float bakedFrameRate = Mathf.Max(clip.frameRate / _frameStep, .0001f);
                        _previewStart = EditorApplication.timeSinceStartup - _previewFrame / bakedFrameRate;
                    }
                    else
                        _previewFrame = _previewCurrentFrame;
                }

                EditorGUI.BeginChangeCheck();
                int scrubbed = EditorGUILayout.IntSlider(
                    _previewPlaying ? _previewCurrentFrame : _previewFrame, 0, Mathf.Max(0, frameCount - 1));

                if (EditorGUI.EndChangeCheck())
                {
                    _previewPlaying = false;
                    _previewFrame = scrubbed;
                    Repaint();
                }
            }
        }

        /*
         * Events are edited one clip at a time, the one the preview is showing, because placing a
         * marker means watching the pose it lands on. Only clips in the bake set can hold events,
         * since only those become slices of the texture array.
         */
        /// <summary>
        /// The event track and list for the previewed clip.
        /// </summary>
        private void DrawEventsSection(AnimationClip clip, int frameCount, List<AnimationClip> bakeClips)
        {
            _showEvents = EditorGUILayout.Foldout(_showEvents,
                VATUi.Content("Events", VATIcons.First("Animation.EventMarker", "AnimationClip Icon")), true);
            if (!_showEvents) return;

            int frames = Mathf.Max(1, frameCount);
            VATAuthoredClipEvents entry = EventsFor(clip, bakeClips, frames);
            int playhead = Mathf.Clamp(_previewPlaying ? _previewCurrentFrame : _previewFrame, 0, frames - 1);

            DrawEventTrack(GUILayoutUtility.GetRect(64f, 30f, GUILayout.ExpandWidth(true)), entry, frames, playhead);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"'{clip.name}'  |  {entry.events.Count} event(s)" +
                                           (entry.authored ? "  |  overrides the clip" : ""),
                    EditorStyles.miniLabel);

                if (GUILayout.Button($"+ Add at frame {playhead}", EditorStyles.miniButton, GUILayout.Width(130f)))
                {
                    entry.events.Add(new VATClipEvent
                    {
                        name = "Event",
                        normalizedTime = FrameTime(playhead, frames)
                    });

                    _selectedEvent = entry.events.Count - 1;
                    MarkAuthored(entry);
                }

                using (new EditorGUI.DisabledScope(!entry.authored))
                {
                    if (VATUi.Button(new GUIContent("Reset to source",
                            "Throw these edits away and re-import the source clip's own events."),
                            VATUi.DESTRUCTIVE, EditorStyles.miniButton, GUILayout.Width(112f)))
                    {
                        ResetToSourceEvents(entry, clip, frames);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (entry.authored && entry.authoredStartFrame != _startFrame)
            {
                EditorGUILayout.HelpBox(
                    $"These events were placed with Start Frame {entry.authoredStartFrame}, and it is now " +
                    $"{_startFrame}. Event times are stored as a fraction of the clip, so they held their " +
                    "position in time while the animation underneath them moved. Check where they land.",
                    MessageType.Warning);
            }

            DrawEventList(entry, frames);
            DrawEventSaveRow(clip, bakeClips, entry);
        }

        private void DrawEventList(VATAuthoredClipEvents entry, int frames)
        {
            if (entry.events.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No events on this clip. Scrub to a frame and press Add, or drag markers on the track.",
                    MessageType.None);
                return;
            }

            // Sorted for display only. Reordering the list itself would shuffle indices out from
            // under a drag in progress.
            List<int> order = new List<int>();
            for (int i = 0; i < entry.events.Count; i++)
                order.Add(i);

            order.Sort((a, b) => entry.events[a].normalizedTime.CompareTo(entry.events[b].normalizedTime));

            int remove = -1;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (int i in order)
                {
                    VATClipEvent e = entry.events[i];
                    bool selected = i == _selectedEvent;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (VATUi.Button(new GUIContent("-", "Delete this event."), VATUi.DESTRUCTIVE,
                                EditorStyles.miniButton, GUILayout.Width(22f)))
                        {
                            remove = i;
                        }

                        EditorGUI.BeginChangeCheck();
                        e.name = EditorGUILayout.TextField(e.name);
                        bool nameChanged = EditorGUI.EndChangeCheck();

                        // Checked on its own, so renaming an event imported from the source clip does
                        // not also snap its time onto a frame boundary it was never on.
                        EditorGUI.BeginChangeCheck();
                        int frame = Mathf.Clamp(
                            EditorGUILayout.IntField(EventFrame(e.normalizedTime, frames), GUILayout.Width(46f)),
                            0, frames - 1);

                        bool frameChanged = EditorGUI.EndChangeCheck();
                        if (frameChanged)
                        {
                            e.normalizedTime = FrameTime(frame, frames);
                            ScrubTo(frame);
                        }

                        if (nameChanged || frameChanged)
                        {
                            entry.events[i] = e;
                            MarkAuthored(entry);
                        }

                        EditorGUILayout.LabelField($"t {e.normalizedTime:0.000}", EditorStyles.miniLabel,
                            GUILayout.Width(52f));

                        if (GUILayout.Toggle(selected, "params", EditorStyles.miniButton, GUILayout.Width(52f)) != selected)
                            _selectedEvent = selected ? -1 : i;
                    }

                    if (!selected) continue;

                    EditorGUI.indentLevel++;
                    EditorGUI.BeginChangeCheck();
                    e.stringParameter = EditorGUILayout.TextField("String", e.stringParameter);
                    e.floatParameter = EditorGUILayout.FloatField("Float", e.floatParameter);
                    e.intParameter = EditorGUILayout.IntField("Int", e.intParameter);

                    if (EditorGUI.EndChangeCheck())
                    {
                        entry.events[i] = e;
                        MarkAuthored(entry);
                    }

                    EditorGUI.indentLevel--;
                }
            }

            if (remove < 0) return;

            entry.events.RemoveAt(remove);
            _selectedEvent = -1;
            MarkAuthored(entry);
            GUIUtility.ExitGUI();
        }

        /*
         * Drawn by hand rather than with layout controls because a marker has to sit at an arbitrary
         * fraction along the bar and be draggable, which no built-in control does.
         * One control id covers the whole track, so the marker count can change between events
         * without the id stream going out of step.
         */
        /// <summary>
        /// The scrub bar: frame ticks, a playhead, and one draggable marker per event.
        /// </summary>
        private void DrawEventTrack(Rect rect, VATAuthoredClipEvents entry, int frames, int playhead)
        {
            int trackId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(.14f, .14f, .14f));

                // Thinned out so the ticks stay readable on a long clip in a narrow window.
                int step = Mathf.Max(1, Mathf.CeilToInt(frames / Mathf.Max(1f, rect.width / 7f)));
                for (int f = 0; f < frames; f += step)
                {
                    float tick = rect.x + rect.width * ((float)f / frames);
                    EditorGUI.DrawRect(new Rect(tick, rect.yMax - 5f, 1f, 5f), new Color(1f, 1f, 1f, .2f));
                }

                float head = rect.x + rect.width * ((float)playhead / frames);
                EditorGUI.DrawRect(new Rect(head, rect.y, 1f, rect.height), new Color(1f, 1f, 1f, .75f));

                for (int i = 0; i < entry.events.Count; i++)
                {
                    EditorGUI.DrawRect(MarkerRect(rect, entry.events[i].normalizedTime),
                        i == _selectedEvent ? new Color(1f, .78f, .2f) : new Color(.35f, .7f, 1f));
                }
            }

            switch (e.GetTypeForControl(trackId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition)) break;

                    _selectedEvent = MarkerIndexAt(rect, entry, e.mousePosition);
                    if (_selectedEvent >= 0)
                    {
                        _draggingEvent = _selectedEvent;
                        GUIUtility.hotControl = trackId;

                        // Park on the marker's own frame the moment it is picked up, so the pose on
                        // screen is the one this event fires on before the drag even starts.
                        ScrubTo(EventFrame(entry.events[_selectedEvent].normalizedTime, frames));
                    }
                    else
                        ScrubTo(FrameAt(rect, e.mousePosition, frames));

                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != trackId || _draggingEvent < 0) break;

                    MoveEvent(entry, _draggingEvent, FrameAt(rect, e.mousePosition, frames), frames);
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != trackId) break;

                    GUIUtility.hotControl = 0;
                    _draggingEvent = -1;
                    e.Use();
                    break;
            }
        }

        private void DrawEventSaveRow(AnimationClip clip, List<AnimationClip> bakeClips, VATAuthoredClipEvents entry)
        {
            string path = ClipSetPath(bakeClips);
            VATClipSet set = AssetDatabase.LoadAssetAtPath<VATClipSet>(path);

            if (!set)
            {
                EditorGUILayout.HelpBox(
                    $"No clip set at {path} yet. Bake once and these events go in with it; after that they " +
                    "can be saved on their own without re-baking.",
                    MessageType.Info);
                return;
            }

            int slice = SliceFor(set, clip, bakeClips);
            if (slice < 0)
            {
                EditorGUILayout.HelpBox(
                    $"'{clip.name}' is not a slice of {set.name} yet, so there is nothing to write into. " +
                    "Bake with this clip in the set first.",
                    MessageType.Warning);
                return;
            }

            if (!VATUi.Button(VATUi.Content($"Save Events to {set.name}",
                    "Write these events straight into the clip set, without re-baking the textures.",
                    VATIcons.First("SaveAs", "Save Icon")), VATUi.CAUTION, GUILayout.Height(22f)))
            {
                return;
            }

            set.clips[slice].events = entry.events.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VAT] Saved {entry.events.Count} event(s) for '{clip.name}' into {path}");
        }

        /*
         * Slices are written in bake order, so when the set on disk still lines up with the bake list the
         * clip's position is the answer, and two clips sharing a name still land in their own slice.
         * Reordering the list without re-baking breaks that alignment, and then the name is all there is.
         */
        /// <summary>
        /// Which slice of a baked clip set a clip's events belong in.
        /// </summary>
        /// <param name="set">The clip set being written to.</param>
        /// <param name="clip">The clip whose events are being saved.</param>
        /// <param name="bakeClips">The current bake set, whose order the slices were written in.</param>
        /// <returns>The slice index, or -1 when that clip is not in the set.</returns>
        private static int SliceFor(VATClipSet set, AnimationClip clip, List<AnimationClip> bakeClips)
        {
            int position = bakeClips.IndexOf(clip);
            if (position >= 0 && position < set.Count && set.NameAt(position) == clip.name) return position;

            return set.IndexOf(clip.name);
        }

        /// <summary>
        /// The clip's working event list, seeded the first time it is looked at.
        /// </summary>
        /// <param name="clip">The clip being previewed.</param>
        /// <param name="bakeClips">The bake set, which decides where the clip set would live.</param>
        /// <param name="frames">Baked frames in this clip, used to normalize imported event times.</param>
        /// <returns>The entry for this clip, created and seeded if it did not exist.</returns>
        private VATAuthoredClipEvents EventsFor(AnimationClip clip, List<AnimationClip> bakeClips, int frames)
        {
            VATAuthoredClipEvents existing = FindAuthored(clip);

            if (existing != null)
            {
                existing.clipName = clip.name;
                return existing;
            }

            VATAuthoredClipEvents entry = new VATAuthoredClipEvents
            {
                clip = clip,
                clipName = clip.name,
                authoredStartFrame = _startFrame
            };

            // The baked clip set is the live runtime data, so it wins over the source clip when there
            // is one. Before any bake there is nothing but the source clip to go on.
            VATClipSet set = AssetDatabase.LoadAssetAtPath<VATClipSet>(ClipSetPath(bakeClips));
            VATClipEvent[] baked = set ? set.EventsAt(SliceFor(set, clip, bakeClips)) : null;

            if (baked?.Length > 0) entry.events.AddRange(baked);
            else entry.events.AddRange(ImportSourceEvents(clip, _startFrame, BakedLength(clip, frames)));

            _authoredEvents.Add(entry);
            return entry;
        }

        private void ResetToSourceEvents(VATAuthoredClipEvents entry, AnimationClip clip, int frames)
        {
            entry.events.Clear();
            entry.events.AddRange(ImportSourceEvents(clip, _startFrame, BakedLength(clip, frames)));
            entry.authored = false;
            entry.authoredStartFrame = _startFrame;
            _selectedEvent = -1;
            MarkEdited();
        }

        /*
         * Keyed by reference for the same reason ranges are: two clips called "Idle" used to share one
         * event list, and saving one of them wrote its markers over the other's slice. Legacy entries
         * hold only a name, so the first name match adopts the clip.
         */
        /// <summary>
        /// The stored event list for a clip, if there is one.
        /// </summary>
        /// <param name="clip">The clip to look up.</param>
        /// <returns>Its entry, or null when nothing has been authored or imported for it yet.</returns>
        private VATAuthoredClipEvents FindAuthored(AnimationClip clip)
        {
            VATAuthoredClipEvents legacy = null;

            foreach (VATAuthoredClipEvents entry in _authoredEvents)
            {
                if (entry.clip && entry.clip == clip) return entry;
                if (!entry.clip && legacy == null && entry.clipName == clip.name) legacy = entry;
            }

            if (legacy != null) legacy.clip = clip;
            return legacy;
        }

        private void MarkAuthored(VATAuthoredClipEvents entry)
        {
            if (!entry.authored)
            {
                entry.authored = true;
                entry.authoredStartFrame = _startFrame;
            }

            MarkEdited();
            Repaint();
        }

        /// <summary>
        /// Puts one event on a frame and brings the preview with it, so dragging a marker shows the
        /// pose it will fire on rather than leaving you to line the two up by eye afterwards.
        /// </summary>
        private void MoveEvent(VATAuthoredClipEvents entry, int index, int frame, int frames)
        {
            ScrubTo(frame);

            VATClipEvent moved = entry.events[index];
            float time = FrameTime(frame, frames);
            if (Mathf.Approximately(moved.normalizedTime, time)) return;

            moved.normalizedTime = time;
            entry.events[index] = moved;
            MarkAuthored(entry);
        }

        /// <summary>Pauses the preview on a frame.</summary>
        private void ScrubTo(int frame)
        {
            _previewPlaying = false;
            _previewFrame = frame;
            Repaint();
        }

        private string ClipSetPath(List<AnimationClip> bakeClips)
        {
            return $"{_outputPath}/{BaseName(bakeClips[0])}_Clips.asset";
        }

        /// <summary>Seconds one cycle of the baked slice runs for.</summary>
        private float BakedLength(AnimationClip clip, int frames)
        {
            return frames / Mathf.Max(clip.frameRate / _frameStep, .0001f);
        }

        /*
         * The shader spreads a clip's frames evenly over the cycle, so frame f owns the span from
         * f/frames to (f+1)/frames and the frame on screen at time t is floor(t * frames).
         * Storing the fraction rather than the frame is what lets Frame Step change later
         * without every marker sliding off the pose it was placed on.
         */
        /// <summary>Which baked frame an event time lands on.</summary>
        private static int EventFrame(float normalizedTime, int frames)
        {
            return Mathf.Clamp(Mathf.FloorToInt(normalizedTime * frames), 0, Mathf.Max(0, frames - 1));
        }

        /// <summary>The event time that puts a marker at the start of a baked frame.</summary>
        private static float FrameTime(int frame, int frames)
        {
            return frames > 0 ? Mathf.Clamp01((float)frame / frames) : 0f;
        }

        private static Rect MarkerRect(Rect track, float normalizedTime)
        {
            float x = track.x + track.width * Mathf.Clamp01(normalizedTime);
            return new Rect(x - 3f, track.y + 2f, 6f, track.height - 9f);
        }

        private static int MarkerIndexAt(Rect track, VATAuthoredClipEvents entry, Vector2 mouse)
        {
            // Backwards so the marker drawn last is the one picked up when two overlap.
            for (int i = entry.events.Count - 1; i >= 0; i--)
            {
                Rect marker = MarkerRect(track, entry.events[i].normalizedTime);
                marker.xMin -= 3f;
                marker.xMax += 3f;
                marker.yMax = track.yMax;
                if (marker.Contains(mouse)) return i;
            }

            return -1;
        }

        private static int FrameAt(Rect track, Vector2 mouse, int frames)
        {
            float t = track.width > 0f ? (mouse.x - track.x) / track.width : 0f;
            return Mathf.Clamp(Mathf.FloorToInt(t * frames), 0, frames - 1);
        }

        private static List<VATAuthoredClipEvents> CloneAuthored(List<VATAuthoredClipEvents> source)
        {
            List<VATAuthoredClipEvents> copy = new List<VATAuthoredClipEvents>();
            if (source == null) return copy;

            foreach (VATAuthoredClipEvents entry in source)
            {
                copy.Add(new VATAuthoredClipEvents
                {
                    clip = entry.clip,
                    clipName = entry.clipName,
                    authored = entry.authored,
                    authoredStartFrame = entry.authoredStartFrame,
                    events = new List<VATClipEvent>(entry.events)
                });
            }

            return copy;
        }

        private void DestroyPreview()
        {
            foreach (VATPreviewPart part in _previewParts)
                if (part.Display) Object.DestroyImmediate(part.Display);

            _previewParts.Clear();

            if (_maskMaterial)
            {
                Object.DestroyImmediate(_maskMaterial);
                _maskMaterial = null;
            }

            if (_previewDisplay)
            {
                Object.DestroyImmediate(_previewDisplay);
                _previewDisplay = null;
            }

            if (_previewScratch)
            {
                Object.DestroyImmediate(_previewScratch);
                _previewScratch = null;
            }

            _previewBoundsValid = false;

            if (_previewInstance)
            {
                Object.DestroyImmediate(_previewInstance);
                _previewInstance = null;
            }

            if (_preview != null)
            {
                _preview.Cleanup();
                _preview = null;
            }

            _previewRenderer = null;
            _previewRoot = null;
            _previewKeyTarget = null;
            _previewKeyClip = null;
            _previewKeyRenderer = -1;
            _previewKeyRootIndex = -1;
        }

        /// <summary>
        /// Rebuilds the preview scene when anything it depends on changed, and does nothing when
        /// nothing did. Called every repaint, so the cheap path has to stay cheap.
        /// </summary>
        private void EnsurePreview(AnimationClip clip)
        {
            bool valid = _preview != null
                         && _previewInstance
                         && _previewScratch
                         && _previewDisplay
                         && PreviewPartsAlive()
                         && ReferenceEquals(_previewKeyTarget, _target)
                         && ReferenceEquals(_previewKeyClip, clip)
                         && _previewKeyRenderer == _rendererIndex
                         && _previewKeyRootIndex == _rootIndex
                         && _previewKeyMode == _rendererMode;
            if (valid) return;

            DestroyPreview();

            _preview = new PreviewRenderUtility();
            _preview.cameraFieldOfView = 30f;
            _preview.camera.allowHDR = true;
            _preview.camera.nearClipPlane = .01f;
            _preview.camera.farClipPlane = 200f;
            AddUrpCameraData(_preview.camera.gameObject);

            _previewInstance = Object.Instantiate(_target);
            _previewInstance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (Animator animator in _previewInstance.GetComponentsInChildren<Animator>(true))
                animator.runtimeAnimatorController = null;

            _preview.AddSingleGO(_previewInstance);

            SkinnedMeshRenderer[] all = _previewInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _previewRenderer = all[Mathf.Clamp(_rendererIndex, 0, all.Length - 1)];

            // Same anchor as Bake(): the rest pose, read before a single frame is sampled.
            // The instance sits at the origin unrotated, so its space is world space here.
            _previewRoot = ResolveRoot(_previewInstance, _previewRenderer, _rootIndex);
            _previewRootReference = _previewRoot ? _previewRoot.position : Vector3.zero;

            // Stand-in meshes are drawn instead of the rig, so the preview can show blended vertices.
            _previewDisplay = new GameObject("VAT Preview Display");
            _previewDisplay.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // Every skinned renderer stops drawing - they are only here to be sampled - and a stand-in
            // is built for the ones this bake will actually write, so what you see in Selected mode is
            // the single renderer being baked rather than the whole character.
            int selected = Mathf.Clamp(_rendererIndex, 0, all.Length - 1);
            for (int i = 0; i < all.Length; i++)
            {
                SkinnedMeshRenderer source = all[i];
                source.enabled = false;

                if (!source.sharedMesh) continue;
                if (_rendererMode == VATRendererMode.SELECTED && i != selected) continue;

                GameObject child = new GameObject(source.name);
                child.transform.SetParent(_previewDisplay.transform, false);
                child.AddComponent<MeshFilter>();
                child.AddComponent<MeshRenderer>().sharedMaterials = source.sharedMaterials;

                _previewParts.Add(new VATPreviewPart { Source = source });
            }

            _preview.AddSingleGO(_previewDisplay);

            // HideAndDontSave or Unity destroys these on the next play mode transition, along with
            // everything else it treats as scene junk, and the preview is left holding dead references.
            _previewScratch = new Mesh { name = "VAT Preview Scratch", hideFlags = HideFlags.HideAndDontSave };

            _previewBounds = _previewRenderer.bounds;
            _previewBoundsValid = false;
            _previewZoom = Mathf.Max(_previewBounds.size.magnitude * 1.2f, .5f);
            _previewStart = EditorApplication.timeSinceStartup;

            _previewKeyTarget = _target;
            _previewKeyClip = clip;
            _previewKeyRenderer = _rendererIndex;
            _previewKeyRootIndex = _rootIndex;
            _previewKeyMode = _rendererMode;
        }

        /*
         * Orbiting is a proper hot control rather than "any drag over this rectangle", which is what it
         * used to be. That version had no idea another control might already be in the middle of something:
         * dragging the resize grip quickly enough to slide the cursor over the preview handed the drag to
         * the orbit instead, so the window stopped resizing and started spinning.
         *
         * Claiming hotControl on mouse down fixes the reverse case for free.
         * A drag that began inside the preview now keeps orbiting after the cursor leaves it,
         * instead of stopping dead at the edge.
         */
        /*
         * Checked rather than assumed, because these are destroyed from outside the window:
         * a play mode transition or a domain reload takes them and leaves the references behind,
         * reading as null only through Unity's own operator.
         *
         * A part whose topology has not been built yet legitimately has no display mesh, which is why
         * that case is read through TopologyReady rather than treated as a dead reference.
         */
        /// <summary>
        /// Whether every preview part still points at objects that exist.
        /// </summary>
        private bool PreviewPartsAlive()
        {
            for (int i = 0; i < _previewParts.Count; i++)
            {
                VATPreviewPart part = _previewParts[i];

                if (!part.Source) return false;
                if (part.TopologyReady && !part.Display) return false;
            }

            return true;
        }

        private void DrawPreview(Rect rect, AnimationClip clip, int frameCount)
        {
            // Asked for before anything can return, because the layout pass hands out a zero-width rect
            // and an id requested on only some passes would shift every id after it, the resize grip included.
            int orbitId = GUIUtility.GetControlID(FocusType.Passive);

            if (_preview == null || !_previewInstance || rect.width <= 1f) return;

            Event e = Event.current;
            switch (e.GetTypeForControl(orbitId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition)) break;

                    GUIUtility.hotControl = orbitId;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != orbitId) break;

                    _previewOrbit.x -= e.delta.x;
                    _previewOrbit.y = Mathf.Clamp(_previewOrbit.y - e.delta.y, -89f, 89f);
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != orbitId) break;

                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }

            // Zoom is not a drag, so it needs no ownership, only somewhere to point and nobody else busy.
            if (e.type == EventType.ScrollWheel && GUIUtility.hotControl == 0 && rect.Contains(e.mousePosition))
            {
                _previewZoom = Mathf.Clamp(_previewZoom * (1f + e.delta.y * .05f), .2f, 100f);
                e.Use();
                Repaint();
            }

            if (e.type != EventType.Repaint) return;

            // Same arithmetic as VAT_Core.hlsl, so the preview steps through exactly the frames the
            // texture will hold instead of playing the clip smoothly.
            float bakedFrameRate = clip.frameRate / _frameStep;
            float phase;

            if (_previewPlaying)
            {
                float loopsPerSecond = bakedFrameRate / Mathf.Max(frameCount, 1);
                float elapsed = (float)(EditorApplication.timeSinceStartup - _previewStart);
                phase = Mathf.Repeat(elapsed * loopsPerSecond, 1f) * frameCount;
            }
            else
            {
                // Parked on a whole frame, so there is nothing to blend towards and the pose shown
                // is exactly the one that frame's texels hold.
                phase = Mathf.Clamp(_previewFrame, 0, frameCount - 1);
            }

            int frameIndex = Mathf.Clamp(Mathf.FloorToInt(phase), 0, frameCount - 1);
            float blend = _frameBlend ? phase - Mathf.Floor(phase) : 0f;
            _previewCurrentFrame = frameIndex;

            ApplyPreviewPose(clip, frameIndex, frameCount, blend);

            _preview.BeginPreview(rect, GUIStyle.none);

            Quaternion orbit = Quaternion.Euler(-_previewOrbit.y, -_previewOrbit.x, 0f);
            _preview.camera.transform.position = _previewBounds.center + orbit * new Vector3(0f, 0f, -_previewZoom);
            _preview.camera.transform.LookAt(_previewBounds.center);

            _preview.lights[0].intensity = 1.2f;
            _preview.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            _preview.lights[1].intensity = .5f;

            _preview.Render(true);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
            DrawPivotMarker(rect);

            EditorGUI.DropShadowLabel(
                new Rect(rect.x, rect.yMax - 20f, rect.width, 16f),
                $"frame {frameIndex + 1}/{frameCount}   step {_frameStep}   {bakedFrameRate:0.##} fps" +
                (_frameBlend ? $"   blend {blend:0.00}" : "   no blend") +
                (_bakeNormals ? string.Empty : "   bind-pose normals") +
                (_previewLod >= 0 && LodGroupActive ? $"   LOD {_previewLod}" : string.Empty));
        }

        /*
         * Projected onto the rendered image rather than drawn as geometry, because a pivot is a point
         * and a point has no mesh. This is what turns Pivot Nudge from three numbers into something
         * with a visible answer.
         */
        /// <summary>Marks the highlighted section's pivot on top of the preview.</summary>
        private void DrawPivotMarker(Rect rect)
        {
            if (!_previewPivotValid || _highlightSection < 0 || _preview == null) return;

            Vector3 viewport = _preview.camera.WorldToViewportPoint(_previewPivot);
            if (viewport.z <= 0f) return;

            Vector2 point = new Vector2(
                rect.x + (viewport.x * rect.width),
                rect.y + ((1f - viewport.y) * rect.height));

            if (!rect.Contains(point)) return;

            Color marker = VATUiSettings.Colours ? VATUi.CAUTION : Color.white;
            EditorGUI.DrawRect(new Rect(point.x - 7f, point.y - 1f, 15f, 2f), marker);
            EditorGUI.DrawRect(new Rect(point.x - 1f, point.y - 7f, 2f, 15f), marker);

            EditorGUI.DropShadowLabel(
                new Rect(point.x + 8f, point.y - 8f, 160f, 16f),
                _sections[_highlightSection].boneName);
        }

        /*
         * With frame blending on this bakes BOTH neighbouring frames and lerps the vertices,
         * which is the same linear interpolation the shader does,
         * so a high Frame Step shows its real artefacts instead of looking free.
         * Sampling the clip at an in-between time would interpolate bone rotations along arcs
         * and look smoother than the bake ever will.
         */
        /// <summary>
        /// Builds the display meshes for one preview frame.
        /// </summary>
        private void ApplyPreviewPose(AnimationClip clip, int frameIndex, int frameCount, float blend)
        {
            bool blending = blend > .0001f;

            CapturePreviewFrame(clip, frameIndex, false);
            if (blending)
            {
                // Wraps to frame 0 exactly as VAT_Core.hlsl does.
                int next = (frameIndex + 1) % Mathf.Max(1, frameCount);
                CapturePreviewFrame(clip, next, true);
            }

            for (int i = 0; i < _previewParts.Count; i++)
            {
                VATPreviewPart part = _previewParts[i];
                if (!part.Display || part.VerticesA == null) continue;

                int count = part.VerticesA.Length;
                Vector3[] outVertices = blending ? part.Blended : part.VerticesA;
                Vector3[] outNormals = blending ? part.BlendedNormals : part.NormalsA;

                if (blending)
                {
                    for (int v = 0; v < count; v++)
                    {
                        outVertices[v] = Vector3.Lerp(part.VerticesA[v], part.VerticesB[v], blend);
                        outNormals[v] = Vector3.Lerp(part.NormalsA[v], part.NormalsB[v], blend).normalized;
                    }
                }

                part.Display.vertices = outVertices;
                part.Display.normals = outNormals;
                ApplyHighlight(part, i);
                part.Display.RecalculateBounds();
            }

            if (!_previewBoundsValid && _previewParts.Count > 0 && _previewParts[0].Display)
            {
                Bounds bounds = _previewParts[0].Display.bounds;
                for (int i = 1; i < _previewParts.Count; i++)
                    if (_previewParts[i].Display) bounds.Encapsulate(_previewParts[i].Display.bounds);

                _previewBounds = bounds;
                _previewZoom = Mathf.Max(bounds.size.magnitude * 1.2f, .5f);
                _previewBoundsValid = true;
            }
        }

        /// <summary>
        /// Poses the rig at one baked frame and stores every part's vertices, rebased into root space
        /// and with root motion removed, which is exactly the data the bake would write.
        /// </summary>
        private void CapturePreviewFrame(AnimationClip clip, int frameIndex, bool intoSecondSlot)
        {
            // Nothing should reach here with a dead preview, since EnsurePreview runs first and checks.
            // Kept anyway: the cost of being wrong is an exception on every repaint, and it costs a line.
            if (!_previewInstance || !_previewScratch) return;

            _previewInstance.transform.position = Vector3.zero;
            clip.SampleAnimation(_previewInstance, (_startFrame + frameIndex * _frameStep) / clip.frameRate);

            Vector3 rootOffset = Vector3.zero;
            if (_removeRootMotion && _previewRoot)
            {
                Vector3 delta = _previewRoot.position - _previewRootReference;
                rootOffset = new Vector3(
                    _lockRootX ? delta.x : 0f,
                    _lockRootY ? delta.y : 0f,
                    _lockRootZ ? delta.z : 0f);
            }

            double errorSum = 0d;
            float errorMax = 0f;
            int errorSamples = 0;

            /*
             * Read once for the whole pass rather than per part: the pivot is a bone on the shared
             * instance, and the instance has already been sampled to this frame, so this IS the
             * animated pivot the pivot texture would hold for it.
             */
            VATSectionSetup tested = TestSection();
            int testChannel = tested != null ? OrderedSections().IndexOf(tested) : -1;
            Quaternion testTurn = Quaternion.identity;
            Vector3 testPivot = Vector3.zero;
            bool testing = false;

            if (tested != null && testChannel >= 0 && _previewParts.Count > 0)
            {
                Transform bone = FindBone(_previewParts[0].Source.bones, tested.boneName);

                if (bone)
                {
                    testPivot = RigidMatrix(_previewInstance.transform).inverse
                        .MultiplyPoint3x4(bone.position) - rootOffset + tested.pivotOffset;

                    testTurn = LimitTurn(Quaternion.Euler(_testRotation), tested.maxAngle);
                    testTurn = Quaternion.Slerp(Quaternion.identity, testTurn, Mathf.Clamp01(_testWeight));
                    testing = Quaternion.Angle(Quaternion.identity, testTurn) > .01f;

                    _previewPivot = testPivot;
                    _previewPivotValid = true;
                }
            }

            for (int i = 0; i < _previewParts.Count; i++)
            {
                VATPreviewPart part = _previewParts[i];
                part.Source.BakeMesh(_previewScratch, false);

                Vector3[] verts = _previewScratch.vertices;
                Vector3[] norms = _previewScratch.normals;
                int count = verts.Length;

                if (part.VerticesA == null || part.VerticesA.Length != count)
                {
                    part.VerticesA = new Vector3[count];
                    part.VerticesB = new Vector3[count];
                    part.NormalsA = new Vector3[count];
                    part.NormalsB = new Vector3[count];
                    part.Blended = new Vector3[count];
                    part.BlendedNormals = new Vector3[count];
                    part.BindNormals = part.Source.sharedMesh ? part.Source.sharedMesh.normals : null;
                    part.TopologyReady = false;
                }

                Vector3[] targetVertices = intoSecondSlot ? part.VerticesB : part.VerticesA;
                Vector3[] targetNormals = intoSecondSlot ? part.NormalsB : part.NormalsA;

                Matrix4x4 toRoot = RigidMatrix(_previewInstance.transform).inverse
                                   * RigidMatrix(part.Source.transform);

                // Bake Normals off means no normal texture, and the shader falls back to the mesh's own
                // normals. Showing the animated ones anyway would make the preview look identical either
                // way, which is exactly the question the toggle is being asked.
                Vector3[] bind = part.BindNormals;
                bool useBaked = _bakeNormals && norms.Length == count;
                bool useBind = !_bakeNormals && bind != null && bind.Length == count;
                bool quantize = useBaked && _normalPrecision != VATNormalPrecision.HALF;

                for (int v = 0; v < count; v++)
                {
                    targetVertices[v] = toRoot.MultiplyPoint3x4(verts[v]) - rootOffset;

                    if (useBaked)
                    {
                        // Normalized after quantizing because VAT_Core.hlsl normalizes what it samples,
                        // and without it the non-blending path below hands the renderer normals whose
                        // length wanders by half a percent where the shader's never would.
                        Vector3 baked = toRoot.MultiplyVector(norms[v]).normalized;
                        targetNormals[v] = quantize ? QuantizeNormal(baked) : baked;

                        if (quantize)
                        {
                            float error = Vector3.Angle(baked, targetNormals[v]);
                            errorSum += error;
                            if (error > errorMax) errorMax = error;
                            errorSamples++;
                        }
                    }
                    else if (useBind) targetNormals[v] = bind[v];
                    else targetNormals[v] = Vector3.up;
                }

                if (testing)
                {
                    EnsureSectionWeights(part, count);

                    for (int v = 0; v < count; v++)
                    {
                        float mask = part.SectionWeights[v][testChannel];
                        if (mask <= .001f) continue;

                        Quaternion turn = SectionBlend(testTurn, mask);

                        targetVertices[v] = testPivot + (turn * (targetVertices[v] - testPivot));
                        targetNormals[v] = turn * targetNormals[v];
                    }
                }

                if (!part.TopologyReady) BuildPreviewTopology(part, i, targetVertices, count);
            }

            _normalErrorAverage = errorSamples > 0 ? (float)(errorSum / errorSamples) : 0f;
            _normalErrorMax = errorMax;
            _normalErrorSamples = errorSamples;
        }

        /*
         * The error this puts back is about a third of a degree and will usually not be visible at all.
         * Done anyway, because a toggle whose preview never changes is a toggle nobody can judge,
         * and the one case where it does show is exactly the one worth catching before baking.
         */
        /// <summary>
        /// Rounds a normal to what an 8-bit channel can hold, so the preview shows what the bake will write.
        /// </summary>
        private static Vector3 Quantize8(Vector3 normal)
        {
            return new Vector3(
                (Mathf.Round((normal.x * .5f + .5f) * 255f) / 255f * 2f) - 1f,
                (Mathf.Round((normal.y * .5f + .5f) * 255f) / 255f * 2f) - 1f,
                (Mathf.Round((normal.z * .5f + .5f) * 255f) / 255f * 2f) - 1f);
        }

        /// <summary>Round trips a normal through the chosen storage, exactly as the bake will.</summary>
        private Vector3 QuantizeNormal(Vector3 normal)
        {
            if (_normalPrecision == VATNormalPrecision.BYTE) return Quantize8(normal).normalized;

            Vector2 folded = OctEncode(normal);
            folded = new Vector2(
                (Mathf.Round(((folded.x * .5f) + .5f) * 65535f) / 65535f * 2f) - 1f,
                (Mathf.Round(((folded.y * .5f) + .5f) * 65535f) / 65535f * 2f) - 1f);

            Vector3 unfolded = new Vector3(folded.x, folded.y,
                1f - Mathf.Abs(folded.x) - Mathf.Abs(folded.y));

            if (unfolded.z < 0f)
            {
                float foldedX = (1f - Mathf.Abs(unfolded.y)) * (unfolded.x >= 0f ? 1f : -1f);
                unfolded.y = (1f - Mathf.Abs(unfolded.x)) * (unfolded.y >= 0f ? 1f : -1f);
                unfolded.x = foldedX;
            }

            return unfolded.normalized;
        }

        /// <summary>
        /// Writes the parts of the display mesh that never change, so the per-frame path only has to
        /// push vertices and normals.
        /// </summary>
        private void BuildPreviewTopology(VATPreviewPart part, int childIndex, Vector3[] vertices, int count)
        {
            if (!part.Display)
            {
                part.Display = new Mesh
                {
                    name = part.Source.name + " (preview)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                part.Display.MarkDynamic();
                _previewDisplay.transform.GetChild(childIndex).GetComponent<MeshFilter>().sharedMesh = part.Display;
            }

            part.Display.Clear();
            part.Display.indexFormat = count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            part.Display.vertices = vertices;
            part.Display.uv = _previewScratch.uv;

            /*
             * Triangles come from the SOURCE mesh when a level is being previewed, because that is where
             * the Mesh LOD levels live - BakeMesh returns a pose, not a level. Vertex order is the same
             * in both, so a level's indices address these vertices exactly as they address the source's,
             * which is the same reason the bake can cut a level out at all.
             */
            Mesh topology = _previewScratch;
            int level = PreviewedLevel(part.Source);

            if (level >= 0 && part.Source && part.Source.sharedMesh) topology = part.Source.sharedMesh;

            part.Display.subMeshCount = topology.subMeshCount;

            for (int sm = 0; sm < topology.subMeshCount; sm++)
            {
                part.Display.SetTriangles(level >= 0
                    ? topology.GetIndices(sm, level)
                    : topology.GetTriangles(sm), sm);
            }

            part.TopologyReady = true;
        }

        /// <summary>
        /// URP needs its own camera data component on preview cameras. Added by name so this editor
        /// script carries no compile-time dependency on the URP assembly.
        /// </summary>
        private static void AddUrpCameraData(GameObject cameraObject)
        {
            System.Type type = System.Type.GetType(
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");

            if (type != null && !cameraObject.GetComponent(type)) cameraObject.AddComponent(type);
        }

        private void Refresh()
        {
            _weightedBones.Clear();
            _boneSubtrees.Clear();

            _renderers = _target
                ? _target.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                : new SkinnedMeshRenderer[0];
            _rendererIndex = 0;
            _rootIndex = _renderers.Length > 0 ? DetectRootIndex(_renderers[0]) : 0;

            Animator animator = _target ? _target.GetComponentInChildren<Animator>() : null;
            RuntimeAnimatorController controller = animator ? animator.runtimeAnimatorController : null;
            _clips = controller ? controller.animationClips.Distinct().ToArray() : new AnimationClip[0];
            _clipIndex = 0;
            _bakeClips.Clear();
            if (_clips.Length > 0) _bakeClips.Add(_clips[0]);

            _frameRangeClip = null;

            DetectSettingsFor(_target);
        }

        /// <summary>
        /// Notes that something changed outside a normal IMGUI control, where GUI.changed says nothing.
        /// </summary>
        private void MarkEdited()
        {
            _undoDirty = true;
        }

        /*
         * Nothing is committed while a control is held, so dragging a slider is one step rather than one
         * per frame, and nothing is committed until the value has stopped moving, so typing a name is one
         * step rather than one per character.
         */
        /// <summary>
        /// Puts the previous state on the undo stack once the current one has settled.
        /// </summary>
        private void CommitUndoStep()
        {
            if (GUIUtility.hotControl != 0) return;

            VATBakerState now = CaptureState();

            if (_undoBaseline == null)
            {
                _undoBaseline = now;
                _undoDirty = false;
                return;
            }

            if (now.Matches(_undoBaseline))
            {
                _undoPending = null;
                _undoDirty = false;
                return;
            }

            if (_undoPending == null || !now.Matches(_undoPending))
            {
                _undoPending = now;
                _undoSettleAt = EditorApplication.timeSinceStartup + UNDO_SETTLE_SECONDS;
                return;
            }

            if (EditorApplication.timeSinceStartup < _undoSettleAt) return;

            PushUndoStep(now);
        }

        /// <summary>
        /// Commits whatever is still settling, so the first Ctrl+Z after an edit walks back that edit
        /// rather than appearing to do nothing.
        /// </summary>
        private void CommitPendingStep()
        {
            VATBakerState now = CaptureState();
            if (_undoBaseline == null || now.Matches(_undoBaseline))
            {
                _undoBaseline = now;
                _undoPending = null;
                _undoDirty = false;
                return;
            }

            PushUndoStep(now);
        }

        private void PushUndoStep(VATBakerState now)
        {
            _undoSteps.Add(_undoBaseline);
            if (_undoSteps.Count > MAX_UNDO_STEPS) _undoSteps.RemoveAt(0);

            _redoSteps.Clear();
            _undoBaseline = now;
            _undoPending = null;
            _undoDirty = false;
        }

        /// <summary>Steps back one edit made in this window.</summary>
        private void PerformUndo()
        {
            CommitPendingStep();
            if (_undoSteps.Count == 0) return;

            _redoSteps.Add(CaptureState());

            VATBakerState step = _undoSteps[_undoSteps.Count - 1];
            _undoSteps.RemoveAt(_undoSteps.Count - 1);
            RestoreState(step);
        }

        /// <summary>Steps forward again after an undo.</summary>
        private void PerformRedo()
        {
            if (_redoSteps.Count == 0) return;

            _undoSteps.Add(CaptureState());

            VATBakerState step = _redoSteps[_redoSteps.Count - 1];
            _redoSteps.RemoveAt(_redoSteps.Count - 1);
            RestoreState(step);
        }

        /// <summary>
        /// Copies every setting that decides what gets baked into a snapshot.
        /// </summary>
        /// <returns>A snapshot that shares nothing mutable with the window.</returns>
        private VATBakerState CaptureState()
        {
            return new VATBakerState
            {
                target = _target,
                rendererMode = (int)_rendererMode,
                rendererIndex = _rendererIndex,
                bakeClips = new List<AnimationClip>(_bakeClips),
                explicitClip = _explicitClip,
                frameRangeClip = _frameRangeClip,
                clipIndex = _clipIndex,
                startFrame = _startFrame,
                endFrame = _endFrame,
                frameStep = _frameStep,
                trimLoopFrame = _trimLoopFrame,
                sectionsEnabled = _sectionsEnabled,
                sections = CloneSections(_sections),
                perClipRanges = _perClipRanges,
                clipRanges = CloneRanges(_clipRanges),
                blendDuration = _blendDuration,
                removeRootMotion = _removeRootMotion,
                rootIndex = _rootIndex,
                lockRootX = _lockRootX,
                lockRootY = _lockRootY,
                lockRootZ = _lockRootZ,
                textureWidth = _textureWidth,
                bakeNormals = _bakeNormals,
                positionPrecision = (int)_positionPrecision,
                normalPrecision = (int)_normalPrecision,
                frameQuality = (int)_frameQuality,
                stepTolerance = _stepTolerance,
                outputPath = _outputPath,
                fileName = _fileName,
                createMaterial = _createMaterial,
                materialShader = _materialShader,
                lodGroup = _lodGroup,
                lodLevels = CloneLodLevels(_lodLevels),
                restPoseMesh = _restPoseMesh,
                createPrefab = _createPrefab,
                frameBlend = _frameBlend,
                updateExisting = _updateExisting,
                saveSettings = _saveSettings,
                authoredEvents = CloneAuthored(_authoredEvents)
            };
        }

        /// <summary>
        /// Writes a snapshot back over the window's settings.
        /// </summary>
        /// <param name="state">The snapshot to restore. Everything mutable in it is copied, not shared.</param>
        private void RestoreState(VATBakerState state)
        {
            // Only when the object itself changed, because Refresh rebuilds the renderer and clip lists
            // from scratch and there is nothing to rebuild while the target is the same one.
            if (_target != state.target)
            {
                _target = state.target;
                Refresh();
            }

            _rendererMode = (VATRendererMode)state.rendererMode;
            _rendererIndex = state.rendererIndex;

            _bakeClips.Clear();
            _bakeClips.AddRange(state.bakeClips);
            _explicitClip = state.explicitClip;
            _frameRangeClip = state.frameRangeClip;
            _clipIndex = state.clipIndex;

            _startFrame = state.startFrame;
            _endFrame = state.endFrame;
            _frameStep = state.frameStep;
            _trimLoopFrame = state.trimLoopFrame;
            _sectionsEnabled = state.sectionsEnabled;
            _sections = CloneSections(state.sections);
            _perClipRanges = state.perClipRanges;
            _clipRanges = CloneRanges(state.clipRanges);
            _blendDuration = state.blendDuration;

            _removeRootMotion = state.removeRootMotion;
            _rootIndex = state.rootIndex;
            _lockRootX = state.lockRootX;
            _lockRootY = state.lockRootY;
            _lockRootZ = state.lockRootZ;

            _textureWidth = state.textureWidth;
            _bakeNormals = state.bakeNormals;
            _positionPrecision = (VATPositionPrecision)state.positionPrecision;
            _normalPrecision = (VATNormalPrecision)state.normalPrecision;
            _frameQuality = (VATFrameQuality)state.frameQuality;
            _stepTolerance = state.stepTolerance;

            _outputPath = state.outputPath;
            _fileName = state.fileName;
            _createMaterial = state.createMaterial;
            _materialShader = state.materialShader;
            _lodGroup = state.lodGroup;
            _lodLevels = CloneLodLevels(state.lodLevels);
            _restPoseMesh = state.restPoseMesh;
            _createPrefab = state.createPrefab;
            _frameBlend = state.frameBlend;
            _updateExisting = state.updateExisting;
            _saveSettings = state.saveSettings;

            _authoredEvents = CloneAuthored(state.authoredEvents);
            _selectedEvent = -1;

            // Taken again rather than reusing the snapshot, so the baseline shares no list with the
            // stack entry it came from and a later edit cannot reach back and change history.
            _undoBaseline = CaptureState();
            _undoPending = null;
            _undoSettleAt = 0d;
            _undoDirty = false;

            GUI.FocusControl(null);
            Repaint();
        }

        /*
         * Everything to do with a saved bake in one place, under the object it belongs to:
         * what is loaded, how to load something else, and how to put this object back to a clean bake.
         */
        /// <summary>The folded panel under the Source row.</summary>
        private void DrawBakeSettingsPanel()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    VATBakeSettings dropped = (VATBakeSettings)EditorGUILayout.ObjectField(
                        new GUIContent("Loaded",
                            "Drop a saved settings asset here to restore that bake exactly - same clips, " +
                            "same order, same frame range and output. Clear it to leave the current " +
                            "values in place but stop them being written back to that asset."),
                        _settings, typeof(VATBakeSettings), false);

                    if (EditorGUI.EndChangeCheck())
                    {
                        if (!dropped) _settings = null;
                        else if (dropped != _settings)
                        {
                            ApplySettings(dropped);
                            GUIUtility.ExitGUI();
                        }
                    }

                    Rect loadRect = GUILayoutUtility.GetRect(new GUIContent("Load..."),
                        EditorStyles.miniButton, GUILayout.Width(70f));

                    using (new VATUi.Tinted(VATUi.GENTLE))
                    {
                        if (GUI.Button(loadRect, "Load...", EditorStyles.miniButton))
                        {
                            PopupWindow.Show(loadRect, new VATBakeSettingsPickerPopup(picked =>
                            {
                                ApplySettings(picked);
                                Repaint();
                            }));
                        }
                    }
                }

                if (_detectedSettings && _detectedSettings != _settings)
                {
                    EditorGUILayout.HelpBox($"'{_detectedSettings.name}' was baked from this object.",
                        MessageType.Info);

                    if (VATUi.Button(VATUi.Content($"Load {_detectedSettings.name}",
                            VATIcons.Named("Settings")), VATUi.GENTLE))
                    {
                        ApplySettings(_detectedSettings);
                        GUIUtility.ExitGUI();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(!_target))
                    {
                        if (VATUi.Button(VATUi.Content("Reset Bake",
                                "Keeps the object and puts everything else back to a fresh bake: clips, " +
                                "frame ranges, events, sections, texture and output.",
                                VATIcons.First("Refresh", "RotateTool")),
                                VATUi.DESTRUCTIVE, GUILayout.Width(130f)) && ConfirmReset())
                        {
                            ResetBake();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        private bool ConfirmReset()
        {
            return EditorUtility.DisplayDialog("Reset Bake",
                $"Put '{_target.name}' back to a fresh bake?\n\nThe object stays selected. Clip " +
                "selection, per-clip frame ranges, events, sections, texture and output settings all " +
                "go back to their defaults, and the loaded settings asset is unlinked.\n\nNothing " +
                "already written to disk is touched.",
                "Reset", "Cancel");
        }

        /*
         * Deliberately spelled out rather than restoring a default snapshot: the window has to end up
         * exactly where a newly opened one would, and a list of assignments is the only version of that
         * which can be read against the field declarations to check.
         */
        /// <summary>
        /// Puts every setting back to its default, keeping the object that is selected.
        /// </summary>
        private void ResetBake()
        {
            _settings = null;
            _explicitClip = null;
            _frameRangeClip = null;
            _selectedEvent = -1;

            _authoredEvents.Clear();
            _clipRanges.Clear();
            _sections.Clear();
            _sectionsEnabled = false;
            _highlightSection = -1;
            _testRotation = Vector3.zero;
            _testWeight = 1f;

            _rendererMode = VATRendererMode.SELECTED;
            _perClipRanges = false;
            _startFrame = 0;
            _endFrame = 1;
            _frameStep = 1;
            _trimLoopFrame = true;
            _blendDuration = .15f;
            _frameQuality = VATFrameQuality.BALANCED;
            _stepTolerance = BALANCED_TOLERANCE;

            _removeRootMotion = true;
            _lockRootX = true;
            _lockRootY = false;
            _lockRootZ = true;

            _textureWidth = 1024;
            _bakeNormals = true;
            _positionPrecision = VATPositionPrecision.NORMALIZED;
            _normalPrecision = VATNormalPrecision.OCTAHEDRAL;

            _outputPath = "Assets/VAT";
            _fileName = string.Empty;
            _createMaterial = true;
            _materialShader = null;
            _lodGroup = false;
            _lodLevels.Clear();
            _previewLod = -1;
            _restPoseMesh = true;
            _createPrefab = true;
            _frameBlend = true;
            _updateExisting = true;
            _saveSettings = true;

            InvalidateSectionCache();
            DestroyPreview();

            // Rebuilds the renderer and clip lists and picks the first clip, which is what a window
            // being handed this object for the first time would have.
            Refresh();
            MarkEdited();
        }

        /*
         * VAT output has to live inside Assets, because everything the bake writes goes through
         * AssetDatabase.CreateAsset and that cannot write anywhere else.
         * So a folder outside the project is refused rather than stored as an absolute path
         * that would fail much later, in the middle of a bake.
         */
        /// <summary>
        /// Picks the output folder with the system folder browser instead of typing the path.
        /// </summary>
        private void BrowseForOutputFolder()
        {
            string picked = EditorUtility.OpenFolderPanel("Choose VAT output folder", _outputPath, string.Empty);
            if (string.IsNullOrEmpty(picked)) return;

            picked = picked.Replace('\\', '/');
            string assets = Application.dataPath.Replace('\\', '/');

            // The panel and Application.dataPath do not always agree on casing on Windows, and an exact
            // comparison would then reject a folder that is plainly inside the project.
            if (string.Equals(picked, assets, System.StringComparison.OrdinalIgnoreCase))
            {
                _outputPath = "Assets";
                MarkEdited();
                GUI.FocusControl(null);
                return;
            }

            if (!picked.StartsWith(assets + "/", System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Outside the project",
                    "VAT output has to be inside this project's Assets folder, because the baker writes " +
                    $"Unity assets and AssetDatabase cannot create one anywhere else.\n\n{picked}",
                    "OK");
                return;
            }

            _outputPath = "Assets" + picked.Substring(assets.Length);
            MarkEdited();
            GUI.FocusControl(null);
        }

        /*
         * The same hot control pattern as the preview grip, turned on its side.
         * The fraction rather than a pixel width, so the panes keep their proportions when the window
         * itself is resized instead of the right one swallowing everything.
         */
        /// <summary>
        /// The drag bar between the settings pane and the preview pane.
        /// </summary>
        private void DrawSplitter()
        {
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Width(SPLITTER_WIDTH), GUILayout.ExpandHeight(true));

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            int splitId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            if (e.type == EventType.Repaint)
            {
                Rect bar = new Rect(rect.center.x - 1f, rect.y + 4f, 2f, Mathf.Max(0f, rect.height - 8f));
                EditorGUI.DrawRect(bar, new Color(1f, 1f, 1f, GUIUtility.hotControl == splitId ? .55f : .18f));
            }

            switch (e.GetTypeForControl(splitId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition)) break;

                    GUIUtility.hotControl = splitId;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != splitId) break;

                    VATUiSettings.SplitFraction += e.delta.x / Mathf.Max(position.width, 1f);
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != splitId) break;

                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }
        }

        /*
         * The window already grows the preview sideways on its own, but a taller one is what you want
         * for looking at detail, and nothing about the layout offers that.
         * Kept in EditorPrefs rather than on the window, so it survives closing the baker
         * and follows the person rather than the project, the same as the icon and colour settings.
         */
        /// <summary>
        /// The drag grip under the preview that sets its height.
        /// </summary>
        private void DrawPreviewResizeGrip()
        {
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(7f), GUILayout.ExpandWidth(true));

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            int gripId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            if (e.type == EventType.Repaint)
            {
                Rect bar = new Rect(rect.center.x - 14f, rect.center.y - 1f, 28f, 2f);
                EditorGUI.DrawRect(bar, new Color(1f, 1f, 1f, GUIUtility.hotControl == gripId ? .55f : .22f));
            }

            switch (e.GetTypeForControl(gripId))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition)) break;

                    GUIUtility.hotControl = gripId;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != gripId) break;

                    VATUiSettings.PreviewHeight += e.delta.y;
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != gripId) break;

                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }
        }

        /// <summary>
        /// Looks for a saved bake that already points at this object, so reopening the baker on a
        /// prefab offers its previous setup instead of a blank form.
        /// </summary>
        private void DetectSettingsFor(GameObject target)
        {
            _detectedSettings = null;
            if (!target) return;

            foreach (string guid in AssetDatabase.FindAssets("t:VATBakeSettings"))
            {
                VATBakeSettings candidate = AssetDatabase.LoadAssetAtPath<VATBakeSettings>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (candidate && candidate.target == target)
                {
                    _detectedSettings = candidate;
                    return;
                }
            }
        }

        private void ApplySettings(VATBakeSettings settings)
        {
            if (!settings) return;

            _settings = settings;
            _target = settings.target;

            // Rebuilds the renderer and clip lists from the target, and resets everything that
            // depends on them, so it has to run before the stored values are written back.
            Refresh();

            _rendererMode = (VATRendererMode)Mathf.Clamp(settings.rendererMode, 0, 2);
            _rendererIndex = settings.rendererIndex;
            _rootIndex = settings.rootIndex;

            _bakeClips.Clear();
            foreach (AnimationClip clip in settings.clips)
                if (clip) _bakeClips.Add(clip);

            if (_bakeClips.Count == 0 && _clips.Length > 0) _bakeClips.Add(_clips[0]);
            _explicitClip = settings.explicitClip;

            _clipIndex = _bakeClips.Count > 0 ? Mathf.Max(0, System.Array.IndexOf(_clips, _bakeClips[0])) : 0;

            _startFrame = settings.startFrame;
            _endFrame = settings.endFrame;
            _frameStep = Mathf.Clamp(settings.frameStep, 1, 10);
            _trimLoopFrame = settings.trimLoopFrame;
            _blendDuration = settings.blendDuration;

            // Pre-seed this so the "new clip gets the full range" reset does not immediately discard
            // the range that was just restored.
            _frameRangeClip = _explicitClip ? _explicitClip : (_bakeClips.Count > 0 ? _bakeClips[0] : null);

            _removeRootMotion = settings.removeRootMotion;
            _lockRootX = settings.lockRootX;
            _lockRootY = settings.lockRootY;
            _lockRootZ = settings.lockRootZ;

            _perClipRanges = settings.perClipRanges;
            _clipRanges = CloneRanges(settings.clipRanges);
            _sections = CloneSections(settings.sections);

            // Version 2 had no switch: a settings asset with sections in it always baked them.
            _sectionsEnabled = settings.version < 3 ? _sections.Count > 0 : settings.sectionsEnabled;
            _authoredEvents = CloneAuthored(settings.authoredEvents);
            _selectedEvent = -1;

            _textureWidth = settings.textureWidth;
            _bakeNormals = settings.bakeNormals;
            _positionPrecision = (VATPositionPrecision)settings.positionPrecision;

            // Version 1 had a Compact Normals checkbox instead of a precision, so a settings asset
            // written before this carries the old answer and none of the new one.
            _normalPrecision = settings.version < 2
                ? (settings.compactNormals ? VATNormalPrecision.BYTE : VATNormalPrecision.HALF)
                : (VATNormalPrecision)settings.normalPrecision;

            settings.version = VATBakeSettings.CURRENT_VERSION;
            _frameQuality = (VATFrameQuality)Mathf.Clamp(settings.frameQuality, 0, 3);
            _stepTolerance = settings.stepTolerance;

            _outputPath = settings.outputPath;
            _fileName = settings.fileName;
            _createMaterial = settings.createMaterial;
            _materialShader = settings.materialShader;
            _lodGroup = settings.lodGroup;
            _lodLevels = CloneLodLevels(settings.lodLevels);
            _restPoseMesh = settings.restPoseMesh;
            _createPrefab = settings.createPrefab;
            _frameBlend = settings.frameBlend;
            _updateExisting = settings.updateExisting;

            _detectedSettings = null;
            DestroyPreview();
            MarkEdited();
            Repaint();
        }

        private void SaveBakeSettings(string baseName, List<AnimationClip> clips)
        {
            if (_target && !EditorUtility.IsPersistent(_target))
            {
                Debug.LogWarning("[VAT] The target is a scene object, so the saved settings cannot " +
                                 "point back at it. Assign a prefab to make the settings reusable.");
            }

            // Always overwritten rather than versioned: these are editor-only notes, and a stack of
            // numbered copies would be worse than one that is always current.
            string path = $"{_outputPath}/{baseName}_BakeSettings.asset";
            VATBakeSettings settings = AssetDatabase.LoadAssetAtPath<VATBakeSettings>(path);
            bool creating = !settings;
            if (creating) settings = CreateInstance<VATBakeSettings>();

            settings.version = VATBakeSettings.CURRENT_VERSION;
            settings.target = EditorUtility.IsPersistent(_target) ? _target : null;
            settings.rendererMode = (int)_rendererMode;
            settings.rendererIndex = _rendererIndex;
            settings.clips = new List<AnimationClip>(clips);
            settings.explicitClip = _explicitClip;

            settings.startFrame = _startFrame;
            settings.endFrame = _endFrame;
            settings.frameStep = _frameStep;
            settings.trimLoopFrame = _trimLoopFrame;
            settings.blendDuration = _blendDuration;

            settings.removeRootMotion = _removeRootMotion;
            settings.rootIndex = _rootIndex;
            settings.lockRootX = _lockRootX;
            settings.lockRootY = _lockRootY;
            settings.lockRootZ = _lockRootZ;

            settings.perClipRanges = _perClipRanges;
            settings.clipRanges = CloneRanges(_clipRanges);
            settings.sectionsEnabled = _sectionsEnabled;
            settings.sections = CloneSections(_sections);
            settings.authoredEvents = CloneAuthored(_authoredEvents);

            settings.textureWidth = _textureWidth;
            settings.bakeNormals = _bakeNormals;
            settings.positionPrecision = (int)_positionPrecision;
            settings.normalPrecision = (int)_normalPrecision;
            settings.frameQuality = (int)_frameQuality;
            settings.stepTolerance = _stepTolerance;

            settings.outputPath = _outputPath;
            settings.fileName = _fileName;
            settings.createMaterial = _createMaterial;
            settings.materialShader = _materialShader;
            settings.lodGroup = _lodGroup;
            settings.lodLevels = CloneLodLevels(_lodLevels);
            settings.restPoseMesh = _restPoseMesh;
            settings.createPrefab = _createPrefab;
            settings.frameBlend = _frameBlend;
            settings.updateExisting = _updateExisting;

            if (creating) AssetDatabase.CreateAsset(settings, path);
            else EditorUtility.SetDirty(settings);

            _settings = settings;
            _detectedSettings = null;
            Debug.Log($"[VAT] {(creating ? "Saved" : "Updated")} bake settings {path}");
        }

        private string BaseName(AnimationClip clip)
        {
            return string.IsNullOrWhiteSpace(_fileName)
                ? $"{Sanitize(_target.name)}_{Sanitize(clip.name)}"
                : Sanitize(_fileName);
        }

        private static int FrameCount(AnimationClip clip) => Mathf.Max(1, Mathf.CeilToInt(clip.length * clip.frameRate));

        private AnimationClip ResolveClip()
        {
            if (_explicitClip) return _explicitClip;
            if (_clips.Length == 0) return null;

            return _clips[Mathf.Clamp(_clipIndex, 0, _clips.Length - 1)];
        }

        /*
         * Only worth having with more than one clip in the bake. A single clip already has the sliders
         * to itself, so the toggle would offer a choice between one thing and the same thing.
         */
        /// <summary>Whether each clip carries its own frame range rather than sharing one.</summary>
        private bool UsePerClipRanges(int clipCount) => _perClipRanges && clipCount > 1;

        /// <summary>
        /// One clip's stored range, created on first sight and clamped to what the clip actually holds.
        /// </summary>
        /// <param name="clip">The clip to look up, matched by reference.</param>
        /// <returns>The stored range, which the caller may write to directly.</returns>
        private VATClipRange RangeFor(AnimationClip clip)
        {
            int frames = FrameCount(clip);
            VATClipRange range = FindRange(clip);

            if (range == null)
            {
                range = new VATClipRange
                {
                    clip = clip,
                    clipName = clip.name,
                    startFrame = 0,
                    endFrame = frames,
                    frameStep = Mathf.Clamp(_frameStep, 1, 10),
                    trimLoopFrame = _trimLoopFrame
                };

                _clipRanges.Add(range);
                return range;
            }

            // The name is a label rather than the key, so renaming the clip renames its tab
            // instead of orphaning everything set on it.
            range.clipName = clip.name;

            // A re-imported clip can be shorter than it was when the range was set, which would
            // otherwise leave the sliders pointing past the end and bake frames that do not exist.
            range.endFrame = Mathf.Clamp(range.endFrame, 1, frames);
            range.startFrame = Mathf.Clamp(range.startFrame, 0, range.endFrame - 1);
            range.frameStep = Mathf.Clamp(range.frameStep, 1, 10);
            return range;
        }

        /*
         * Ranges used to be keyed by name, which made two clips called "Idle" share one entry: editing
         * either edited both, and the clamp above shrank the long one to the short one's length without
         * saying so. Settings assets written before the reference existed carry only the name, so the
         * first name match adopts the clip - the same clip the old lookup would have picked.
         */
        /// <summary>
        /// The stored range for a clip, if there is one.
        /// </summary>
        /// <param name="clip">The clip to look up.</param>
        /// <returns>Its range, or null when nothing is stored for it yet.</returns>
        private VATClipRange FindRange(AnimationClip clip)
        {
            VATClipRange legacy = null;

            foreach (VATClipRange existing in _clipRanges)
            {
                if (existing.clip && existing.clip == clip) return existing;
                if (!existing.clip && legacy == null && existing.clipName == clip.name) legacy = existing;
            }

            if (legacy != null) legacy.clip = clip;
            return legacy;
        }

        /*
         * Answers for both modes, so the bake loop and the size estimate never have to know which one is on.
         * With per-clip off this hands back a throwaway built from the shared settings rather than storing
         * anything, which is what keeps turning the toggle off from quietly rewriting every stored range.
         */
        /// <summary>
        /// The range one clip will actually be baked with.
        /// </summary>
        /// <param name="clip">The clip being baked.</param>
        /// <param name="clipCount">How many clips are in this bake, which decides the mode.</param>
        /// <returns>A range to read. Only writable when per-clip ranges are on.</returns>
        private VATClipRange EffectiveRange(AnimationClip clip, int clipCount)
        {
            if (UsePerClipRanges(clipCount)) return RangeFor(clip);

            bool singleClip = clipCount == 1;
            return new VATClipRange
            {
                clip = clip,
                clipName = clip.name,
                startFrame = singleClip ? _startFrame : 0,
                endFrame = singleClip ? _endFrame : FrameCount(clip),
                frameStep = _frameStep,
                trimLoopFrame = _trimLoopFrame
            };
        }

        private static List<VATClipRange> CloneRanges(List<VATClipRange> source)
        {
            List<VATClipRange> copy = new List<VATClipRange>();
            if (source == null) return copy;

            foreach (VATClipRange range in source)
            {
                copy.Add(new VATClipRange
                {
                    clip = range.clip,
                    clipName = range.clipName,
                    startFrame = range.startFrame,
                    endFrame = range.endFrame,
                    frameStep = range.frameStep,
                    trimLoopFrame = range.trimLoopFrame
                });
            }

            return copy;
        }

        /// <summary>Clips that become slices of the texture array, in slice order.</summary>
        private List<AnimationClip> SelectedClips()
        {
            if (_explicitClip) return new List<AnimationClip> { _explicitClip };

            return new List<AnimationClip>(_bakeClips);
        }

        /// <summary>
        /// Runs the whole bake: samples every frame of every clip, writes the texture arrays, and
        /// generates the material, mesh, prefab, clip set and bake settings that go with them.
        /// </summary>
        /// <param name="sourceRenderer">The renderer chosen in the window, used by Selected mode.</param>
        /// <param name="clips">Clips to bake, one slice each, in slice order.</param>
        private void Bake(SkinnedMeshRenderer sourceRenderer, List<AnimationClip> clips)
        {
            string baseName = BaseName(clips[0]);
            Directory.CreateDirectory(_outputPath);

            Vector3 rootTravel = Vector3.zero;
            List<VATPartBake> parts = new List<VATPartBake>();
            List<VATClipBake> clipBakes = new List<VATClipBake>();

            GameObject instance = Object.Instantiate(_target);
            instance.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                // Sampling the clip directly is only reliable with the controller out of the way.
                foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
                    animator.runtimeAnimatorController = null;

                SkinnedMeshRenderer[] instanceRenderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                BuildParts(parts, instanceRenderers, sourceRenderer, baseName);

                if (parts.Count == 0)
                {
                    Debug.LogError("[VAT] Nothing to bake - no renderer with a mesh.");
                    return;
                }

                foreach (VATPartBake part in parts)
                {
                    CollectSlotNames(part.Targets, part.SlotNames);
                    part.VertexCount = part.Targets.Sum(t => t.sharedMesh.vertexCount);
                    part.RowsPerFrame = Mathf.CeilToInt((float)part.VertexCount / _textureWidth);
                }

                Transform rootTransform = ResolveRoot(instance, parts[0].Targets[0], _rootIndex);

                // Anchored to the rig's authored REST position, captured before anything is sampled,
                // and shared by every clip so they all land on the same pivot.
                Vector3 rootReference = rootTransform
                    ? ToBakeSpace(instance.transform, rootTransform.position)
                    : Vector3.zero;

                foreach (AnimationClip clip in clips)
                {
                    VATClipRange range = EffectiveRange(clip, clips.Count);
                    int frames = range.Frames;

                    // A seamless loop ends on the pose it started on. Storing it twice makes the shader,
                    // which spreads frames evenly over the loop, run slightly slow and stutter once per
                    // cycle. Checked rather than assumed.
                    bool trimmed = false;
                    if (range.trimLoopFrame && frames > 2 &&
                        LoopFrameIsDuplicate(instance, parts, clip, range.startFrame, frames, range.frameStep,
                            rootTransform, rootReference))
                    {
                        frames--;
                        trimmed = true;
                    }

                    clipBakes.Add(new VATClipBake
                    {
                        Clip = clip,
                        StartFrame = range.startFrame,
                        Frames = frames,
                        Step = range.frameStep,
                        Rate = clip.frameRate / range.frameStep,
                        Trimmed = trimmed
                    });
                }

                // Array slices must all be the same size, so every clip pads up to the longest.
                int sliceFrames = clipBakes.Max(c => c.Frames);

                foreach (VATPartBake part in parts)
                {
                    part.TextureHeight = sliceFrames * part.RowsPerFrame;
                    if (part.TextureHeight > MAX_TEXTURE_DIMENSION)
                    {
                        Debug.LogError($"[VAT] '{part.Name}' needs {part.TextureHeight} rows, over the " +
                                       $"{MAX_TEXTURE_DIMENSION} limit. Increase width or frame step.");
                        return;
                    }

                    int sliceSize = _textureWidth * part.TextureHeight;
                    part.Positions = new Color[sliceSize * clipBakes.Count];
                    part.Normals = _bakeNormals ? new Color[sliceSize * clipBakes.Count] : null;
                    part.Min = Vector3.positiveInfinity;
                    part.Max = Vector3.negativeInfinity;
                }

                // Built at the rest pose, before any sampling, and in the same renderer order the frame
                // loop writes in - the shader addresses vertices by index, so the two must agree.
                /*
                 * An imported mesh's vertices are in whatever units the file was authored in, and on a
                 * rig whose bones carry a scale it is the SKINNING that brings them back to metres. A
                 * VAT prefab has no skinning, and the shader replaces every position from the texture,
                 * so the mesh being wrong was invisible - right up until something else drew it. Unity's
                 * placeholder while a variant compiles, an error shader, a fallback on hardware without
                 * texture arrays: all of them draw the bind pose, at whatever scale the file used.
                 *
                 * Writing the rest pose in root space instead means the worst case is a character
                 * standing still in the right place, rather than a hundred times too big.
                 */
                if (WritesOwnMesh)
                {
                    foreach (VATPartBake part in parts)
                    {
                        // Merging is the only reason to rebuild a mesh from nothing. One renderer keeps
                        // its own asset copied whole, which is what carries Mesh LOD across.
                        Mesh baked = part.Targets.Count == 1
                            ? BuildRestPoseMesh(instance, part.Targets[0], part.Name)
                            : BuildCombinedMesh(instance, part.Targets.ToArray(), part.Name);

                        if (SectionsActive) ApplySectionMask(baked, part);

                        ReportMeshLods(part, baked);
                        part.SourceMesh = SaveMesh(baked, part.Name);

                        if (LodGroupActive) part.LodMeshes = SaveLodMeshes(instance, part, baked);
                    }
                }

                // Hoisted out of the per-vertex write: compacted normals are stored as 0 to 1 and
                // decoded back in the shader, plain ones go in as they are.

                // One pivot per section per frame per clip. Four texels wide and a row per frame, so
                // even a long bake costs a few kilobytes.
                List<VATSectionSetup> orderedSections = OrderedSections();
                int sectionCount = SectionsActive ? Mathf.Min(orderedSections.Count, MAX_SECTIONS) : 0;
                Transform[] sectionBones = new Transform[sectionCount];

                for (int i = 0; i < sectionCount; i++)
                    sectionBones[i] = parts.Count > 0 && parts[0].Targets.Count > 0
                        ? FindBone(parts[0].Targets[0].bones, orderedSections[i].boneName)
                        : null;

                _sectionPivotHeight = clipBakes.Count > 0 ? clipBakes.Max(c => c.Frames) : 1;
                _sectionPivotPixels = sectionCount > 0
                    ? new Color[MAX_SECTIONS * _sectionPivotHeight * clipBakes.Count]
                    : null;

                float[] sectionReach = new float[MAX_SECTIONS];

                List<Vector3> pose = new List<Vector3>();
                List<Vector3> previousPose = new List<Vector3>();
                Mesh scratch = new Mesh();
                int totalFrames = clipBakes.Sum(c => c.Frames);
                int doneFrames = 0;

                for (int ci = 0; ci < clipBakes.Count; ci++)
                {
                    VATClipBake clipBake = clipBakes[ci];
                    previousPose.Clear();

                    for (int f = 0; f < clipBake.Frames; f++)
                    {
                        EditorUtility.DisplayProgressBar("Baking VAT",
                            $"{clipBake.Clip.name}  frame {f + 1}/{clipBake.Frames}",
                            (float)doneFrames++ / Mathf.Max(1, totalFrames));

                        // One sample per frame drives every part, so parts cannot drift out of sync.
                        clipBake.Clip.SampleAnimation(instance,
                            (clipBake.StartFrame + f * clipBake.Step) / clipBake.Clip.frameRate);

                        Vector3 rootOffset = RootOffset(instance, rootTransform, rootReference);
                        rootTravel = Vector3.Max(rootTravel, new Vector3(
                            Mathf.Abs(rootOffset.x), Mathf.Abs(rootOffset.y), Mathf.Abs(rootOffset.z)));

                        if (_sectionPivotPixels != null)
                        {
                            Matrix4x4 worldToRoot = RigidMatrix(instance.transform).inverse;

                            for (int i = 0; i < sectionCount; i++)
                            {
                                Vector3 pivot = sectionBones[i]
                                    ? worldToRoot.MultiplyPoint3x4(sectionBones[i].position) - rootOffset
                                    : Vector3.zero;

                                pivot += orderedSections[i].pivotOffset;

                                int texel = ((ci * _sectionPivotHeight) + f) * MAX_SECTIONS + i;
                                _sectionPivotPixels[texel] = new Color(pivot.x, pivot.y, pivot.z, 1f);

                                if (ci == 0 && f == 0) _sectionRestPivots[i] = pivot;
                            }
                        }

                        pose.Clear();
                        bool measureReach = ci == 0 && f == 0 && sectionCount > 0;

                        foreach (VATPartBake part in parts)
                        {
                            int sliceBase = ci * _textureWidth * part.TextureHeight;
                            int vertexBase = 0;

                            foreach (SkinnedMeshRenderer target in part.Targets)
                            {
                                // BakeMesh writes vertices relative to the RENDERER, which on most rigs sits
                                // at the armature origin rather than where the prefab pivots. Rebase into the
                                // root's space so the pivot matches.
                                Matrix4x4 rendererToRoot = RigidMatrix(instance.transform).inverse
                                                           * RigidMatrix(target.transform);

                                target.BakeMesh(scratch, false);

                                Vector3[] verts = scratch.vertices;
                                Vector3[] norms = scratch.normals;

                                for (int v = 0; v < verts.Length; v++)
                                {
                                    int gv = vertexBase + v;
                                    if (gv >= part.VertexCount) break;

                                    int x = gv % _textureWidth;
                                    int y = f * part.RowsPerFrame + (gv / _textureWidth);
                                    int p = sliceBase + y * _textureWidth + x;

                                    Vector3 pos = rendererToRoot.MultiplyPoint3x4(verts[v]) - rootOffset;
                                    pose.Add(pos);

                                    // Only on the very first baked frame, and only when there is a mask
                                    // to read, so the hot loop is untouched on every other bake.
                                    if (measureReach && part.SectionMasks != null
                                        && gv < part.SectionMasks.Length)
                                        for (int sec = 0; sec < sectionCount; sec++)
                                            if (part.SectionMasks[gv][sec] > .001f)
                                                sectionReach[sec] = Mathf.Max(sectionReach[sec],
                                                    Vector3.Distance(pos, _sectionRestPivots[sec]));

                                    part.Positions[p] = new Color(pos.x, pos.y, pos.z, 1f);
                                    part.Min = Vector3.Min(part.Min, pos);
                                    part.Max = Vector3.Max(part.Max, pos);

                                    if (_bakeNormals && norms.Length == verts.Length)
                                    {
                                        // BakeMesh skins the normals with the same matrices as the vertices
                                        // and never renormalizes, so a rig imported at 0.01 scale hands back
                                        // normals 0.01 long. Harmless in 16-bit float because the shader
                                        // normalizes, fatal once quantized to 8 bits - the whole direction
                                        // collapses into two or three byte values around 128.
                                        Vector3 n = rendererToRoot.MultiplyVector(norms[v]).normalized;
                                        part.Normals[p] = EncodeNormal(n);
                                    }
                                }

                                vertexBase += verts.Length;
                            }
                        }

                        // Reported, never removed: the shader spreads frames evenly over the loop, so
                        // dropping an interior frame would delete its duration and retime the clip.
                        if (f > 0 && PosesMatch(pose, previousPose, PoseEpsilon(pose))) clipBake.Duplicates++;

                        previousPose.Clear();
                        previousPose.AddRange(pose);
                    }
                }

                /*
                 * A vertex r away from its pivot, turned by an angle, travels a chord of 2r sin(half).
                 * With no Max Angle there is nothing to bound the turn by, so the whole sphere is
                 * assumed. Rotation only: an offset is whatever gameplay code passes and cannot be
                 * known from here.
                 */
                _sectionMargin = 0f;
                for (int i = 0; i < sectionCount; i++)
                {
                    float limit = orderedSections[i].maxAngle;
                    float chord = limit <= 0f
                        ? 2f
                        : 2f * Mathf.Sin(Mathf.Min(limit, 180f) * .5f * Mathf.Deg2Rad);

                    _sectionMargin = Mathf.Max(_sectionMargin, sectionReach[i] * chord);
                }

                Object.DestroyImmediate(scratch);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Object.DestroyImmediate(instance);
            }

            if (clipBakes.Count == 0 || parts.Count == 0 || parts[0].Positions == null) return;

            Texture2DArray pivotArray = null;

            foreach (VATPartBake part in parts)
            {
                Vector3 positionExtent = part.Max - part.Min;
                if (_positionPrecision == VATPositionPrecision.NORMALIZED)
                    NormalizePositions(part, out positionExtent);

                Texture2DArray positionArray = SaveTextureArray(
                    BuildTextureArray(part.Positions, _textureWidth, part.TextureHeight, clipBakes.Count,
                        PositionFormat()),
                    $"{part.Name}_Positions");

                Texture2DArray normalArray = _bakeNormals
                    ? SaveTextureArray(
                        BuildTextureArray(part.Normals, _textureWidth, part.TextureHeight, clipBakes.Count,
                            NormalFormat()),
                        $"{part.Name}_Normals")
                    : null;

                part.Bounds = new Bounds();
                part.Bounds.SetMinMax(part.Min, part.Max);

                WarnIfMeshScaleDisagrees(part);

                // A turned section leaves the box the animation was measured in, and Unity would cull
                // the renderer - and its shadow - against the box that no longer contains it.
                if (_sectionMargin > 0f) part.Bounds.Expand(_sectionMargin * 2f);

                if (!_createMaterial) continue;

                // Shared by every part: pivots are in the root's space, which all parts already are.
                if (pivotArray == null && _sectionPivotPixels != null)
                    pivotArray = SaveTextureArray(
                        BuildTextureArray(_sectionPivotPixels, MAX_SECTIONS, _sectionPivotHeight,
                            clipBakes.Count, TextureFormat.RGBAHalf),
                        $"{baseName}_Pivots");

                // One material per submesh so each source part keeps its own base map. They all read
                // the same VAT arrays, because the submeshes index one shared vertex buffer.
                part.Materials = new Material[part.SlotNames.Count];
                for (int i = 0; i < part.SlotNames.Count; i++)
                {
                    string materialName = part.SlotNames.Count == 1
                        ? part.Name
                        : $"{part.Name}_{part.SlotNames[i]}";

                    part.Materials[i] = CreateMaterial(materialName, positionArray, normalArray,
                        pivotArray, clipBakes, part.RowsPerFrame, part.TextureHeight,
                        part.Min, positionExtent);
                }
            }

            // The durable record of which slice is which clip, because a material cannot hold strings.
            VATClipSet clipSet = _createMaterial ? SaveClipSet(baseName, clipBakes) : null;

            if (_createMaterial && _createPrefab && parts.All(p => p.Materials?.All(m => m) ?? false))
                CreatePrefab(baseName, parts, clipSet);

            if (_saveSettings) SaveBakeSettings(baseName, clips);

            AssetDatabase.SaveAssets();
            LogBakeResult(parts, clipBakes, rootTravel);

            // Logged as well as shown in the panel, because the panel is easy to be looking away from
            // at the moment the textures are actually written.
            string unsupported = UnsupportedFormats();
            if (unsupported.Length > 0)
                Debug.LogWarning($"[VAT] Baked with storage this machine reports no support for. " +
                                 $"It may sample incorrectly here or on the target platform.\n{unsupported}");
        }

        /// <summary>
        /// Groups the instance's renderers into output sets according to the renderer mode.
        /// </summary>
        /// <param name="parts">Filled with the output sets, cleared of nothing, expected empty.</param>
        /// <param name="instanceRenderers">Renderers on the throwaway instance being sampled.</param>
        /// <param name="sourceRenderer">The renderer chosen in the window, used by Selected mode.</param>
        /// <param name="baseName">Output name every part derives its own name from.</param>
        private void BuildParts(List<VATPartBake> parts, SkinnedMeshRenderer[] instanceRenderers,
                                SkinnedMeshRenderer sourceRenderer, string baseName)
        {
            switch (_rendererMode)
            {
                case VATRendererMode.SEPARATE_PARTS:
                    for (int i = 0; i < instanceRenderers.Length && i < _renderers.Length; i++)
                    {
                        if (!instanceRenderers[i].sharedMesh) continue;

                        VATPartBake part = new VATPartBake { Name = $"{baseName}_{Sanitize(instanceRenderers[i].name)}" };
                        part.Targets.Add(instanceRenderers[i]);
                        // The ORIGINAL mesh asset, so Unity 6 Mesh LOD levels survive.
                        part.SourceMesh = _renderers[i].sharedMesh;
                        parts.Add(part);
                    }
                    break;

                case VATRendererMode.COMBINED_MESH:
                {
                    VATPartBake part = new VATPartBake { Name = baseName };
                    part.Targets.AddRange(instanceRenderers.Where(r => r.sharedMesh));
                    parts.Add(part);
                    break;
                }

                default:
                {
                    int index = System.Array.IndexOf(_renderers, sourceRenderer);
                    if (index < 0 || index >= instanceRenderers.Length) index = 0;

                    VATPartBake part = new VATPartBake { Name = baseName };
                    part.Targets.Add(instanceRenderers[index]);
                    part.SourceMesh = sourceRenderer.sharedMesh;
                    parts.Add(part);
                    break;
                }
            }
        }

        private void LogBakeResult(List<VATPartBake> parts, List<VATClipBake> clipBakes, Vector3 rootTravel)
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            log.AppendLine($"[VAT] Baked {clipBakes.Count} clip(s) into {parts.Count} part(s)");

            for (int i = 0; i < clipBakes.Count; i++)
            {
                VATClipBake c = clipBakes[i];
                log.Append($"  slice {i}  '{c.Clip.name}'  {c.Frames} frames @ {c.Rate:0.##} fps");
                if (c.Step > 1) log.Append($"  (every {c.Step} frames from {c.StartFrame})");
                if (c.Trimmed) log.Append("  (trimmed duplicate loop frame)");
                if (c.Duplicates > 0) log.Append($"  ({c.Duplicates} frames repeat the one before)");
                log.AppendLine();
            }

            foreach (VATPartBake part in parts)
            {
                log.AppendLine($"  {part.Name}: {part.VertexCount} verts, {_textureWidth}x{part.TextureHeight}" +
                               $"x{clipBakes.Count}, {part.RowsPerFrame} rows/frame, " +
                               $"{part.SlotNames.Count} material(s) [{string.Join(", ", part.SlotNames)}], " +
                               $"bounds size {part.Bounds.size}");
            }

            log.AppendLine("  Normals: " + (_bakeNormals
                ? _normalPrecision == VATNormalPrecision.OCTAHEDRAL ? "octahedral (RG32)"
                    : _normalPrecision == VATNormalPrecision.BYTE ? "8-bit (RGBA32)"
                    : "16-bit float (RGBAHalf)"
                : "not baked, lighting falls back to the mesh's bind pose"));
            log.Append("  Bake In Place: " + (_removeRootMotion
                ? $"yes ({(_lockRootX ? "X" : "")}{(_lockRootY ? "Y" : "")}{(_lockRootZ ? "Z" : "")}), travel removed {rootTravel}"
                : "no"));

            Debug.Log(log.ToString());
        }

        /*
         * Point filtered, clamped, uncompressed, no mips. Every one of those is required:
         * a filtered or compressed VAT reads neighbouring vertices and shreds the mesh.
         * Building the array directly avoids the EXR round trip and its import settings entirely.
         */
        /// <summary>
        /// Packs baked pixels into a texture array with the settings a VAT has to have.
        /// </summary>
        /// <param name="pixels">All slices back to back, each one width by sliceHeight.</param>
        /// <param name="width">Texture width, which is also the vertices-per-row figure.</param>
        /// <param name="sliceHeight">Rows in one slice, which is frames times rows per frame.</param>
        /// <param name="slices">One slice per baked clip.</param>
        /// <param name="format">RGBAHalf for positions, and for normals unless they are compacted.</param>
        /// <returns>The finished array, not yet saved as an asset.</returns>
        private static Texture2DArray BuildTextureArray(Color[] pixels, int width, int sliceHeight, int slices,
                                                        TextureFormat format)
        {
            // Linear, not sRGB: none of this is colour, and a gamma curve applied to a normal or a
            // position would bend it.
            Texture2DArray array = new Texture2DArray(width, sliceHeight, slices, format, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };

            int sliceSize = width * sliceHeight;
            Color[] slice = new Color[sliceSize];
            for (int i = 0; i < slices; i++)
            {
                System.Array.Copy(pixels, i * sliceSize, slice, 0, sliceSize);
                array.SetPixels(slice, i);
            }

            array.Apply(false, false);
            return array;
        }

        private VATClipSet SaveClipSet(string baseName, List<VATClipBake> clips)
        {
            string path = $"{_outputPath}/{baseName}_Clips.asset";
            VATClipSet set = AssetDatabase.LoadAssetAtPath<VATClipSet>(path);
            bool updating = set && _updateExisting;

            if (!updating)
            {
                if (set) path = AssetDatabase.GenerateUniqueAssetPath(path);
                set = CreateInstance<VATClipSet>();
            }

            VATClipEntry[] previous = set.clips;
            set.clips = clips.Select((c, slice) =>
            {
                float length = c.Frames / Mathf.Max(c.Rate, .0001f);
                return new VATClipEntry
                {
                    name = c.Clip.name,
                    frames = c.Frames,
                    frameRate = c.Rate,
                    length = length,
                    events = ImportEvents(c, length, previous, slice)
                };
            }).ToArray();

            set.sections = BuildSectionRecords();

            int eventCount = set.clips.Sum(e => e.events?.Length ?? 0);
            if (eventCount > 0) Debug.Log($"[VAT] Carried {eventCount} animation event(s) into {path}");

            if (updating) EditorUtility.SetDirty(set);
            else AssetDatabase.CreateAsset(set, path);

            return set;
        }

        /*
         * VAT has no Animator, so a marker left on the source clip would never fire on anything the
         * baker produced. This is what lets an attack still tell gameplay code when its hit frame lands.
         *
         * Three sources, in order. A list edited in the Events section wins outright, because the whole
         * point of editing it there is to say something the source clip does not.
         * Otherwise the source clip's own events are imported, as they always were.
         * Failing both, a clip with no source events keeps whatever the clip set already held,
         * so markers written straight onto the asset survive a re-bake.
         */
        /// <summary>
        /// Decides what events one baked slice ends up with.
        /// </summary>
        /// <param name="bake">The clip's slice, which carries the frame range that was baked.</param>
        /// <param name="bakedLength">Seconds the baked slice runs for, used to normalize event times.</param>
        /// <param name="previous">Entries from the clip set being updated, or null when creating one.</param>
        /// <param name="slice">This clip's slice, used to carry events forward from the same slot.</param>
        /// <returns>The events for this slice, never null.</returns>
        private VATClipEvent[] ImportEvents(VATClipBake bake, float bakedLength, VATClipEntry[] previous,
                                            int slice)
        {
            VATAuthoredClipEvents authored = FindAuthored(bake.Clip);
            if (authored != null && authored.authored) return authored.events.ToArray();

            VATClipEvent[] imported = ImportSourceEvents(bake.Clip, bake.StartFrame, bakedLength);
            if (imported.Length > 0 || previous == null) return imported;

            // The slot this clip already occupied, when the set being updated still lines up with this
            // bake. Two clips of the same name each keep their own events instead of both taking the
            // first one's. Once the list has been reordered the name is all there is to go on.
            if (slice >= 0 && slice < previous.Length && previous[slice].name == bake.Clip.name &&
                previous[slice].events != null)
            {
                return previous[slice].events;
            }

            foreach (VATClipEntry entry in previous)
                if (entry.name == bake.Clip.name && entry.events != null) return entry.events;

            return imported;
        }

        /// <summary>
        /// Reads a source AnimationClip's own events and remaps their times into the baked range.
        /// </summary>
        /// <param name="clip">The source clip to read events from.</param>
        /// <param name="startFrame">First source frame the bake covers.</param>
        /// <param name="bakedLength">Seconds the baked slice runs for.</param>
        /// <returns>The events that fall inside the baked range, never null.</returns>
        private static VATClipEvent[] ImportSourceEvents(AnimationClip clip, int startFrame, float bakedLength)
        {
            AnimationEvent[] sourceEvents = AnimationUtility.GetAnimationEvents(clip);
            if (sourceEvents == null || sourceEvents.Length == 0) return new VATClipEvent[0];

            float rangeStart = startFrame / clip.frameRate;
            List<VATClipEvent> mapped = new List<VATClipEvent>();

            foreach (AnimationEvent sourceEvent in sourceEvents)
            {
                float normalized = bakedLength > 0f ? (sourceEvent.time - rangeStart) / bakedLength : 0f;
                if (normalized < -.001f || normalized > 1.001f) continue; // outside the baked range

                mapped.Add(new VATClipEvent
                {
                    name = string.IsNullOrEmpty(sourceEvent.functionName) ? "Event" : sourceEvent.functionName,
                    normalizedTime = Mathf.Clamp01(normalized),
                    stringParameter = sourceEvent.stringParameter,
                    floatParameter = sourceEvent.floatParameter,
                    intParameter = sourceEvent.intParameter
                });
            }

            return mapped.ToArray();
        }

        private Texture2DArray SaveTextureArray(Texture2DArray array, string assetName)
        {
            string path = $"{_outputPath}/{assetName}.asset";
            Texture2DArray existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);

            if (existing && !_updateExisting) path = AssetDatabase.GenerateUniqueAssetPath(path);

            // Named before either branch on purpose. CreateAsset would name it from the file anyway,
            // but CopySerialized carries m_Name across with everything else, so an unnamed array
            // blanks the name of the asset it overwrites and Unity then warns that the main object
            // does not match the filename.
            array.name = Path.GetFileNameWithoutExtension(path);

            if (existing && _updateExisting)
            {
                // Overwrite the contents so the asset keeps its GUID and references hold. CopySerialized
                // brings the readable flag across too, which is why this is cleared after it rather than
                // on the array being copied from.
                EditorUtility.CopySerialized(array, existing);
                Object.DestroyImmediate(array);

                VATTextureMaintenance.ClearReadable(existing);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(array, path);
            VATTextureMaintenance.ClearReadable(array);
            return array;
        }

        private Material CreateMaterial(string materialName, Texture2DArray positions, Texture2DArray normals,
                                        Texture2DArray pivots, List<VATClipBake> clips, int rowsPerFrame,
                                        int sliceHeight, Vector3 positionMin, Vector3 positionExtent)
        {
            Shader shader = _materialShader ? _materialShader : Shader.Find(SHADER_NAME);
            if (!shader)
            {
                Debug.LogError($"[VAT] No shader assigned and '{SHADER_NAME}' was not found. " +
                               "Textures were still baked.");

                return null;
            }

            string path = $"{_outputPath}/{materialName}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool updating = material && _updateExisting;

            if (!updating)
            {
                if (material) path = AssetDatabase.GenerateUniqueAssetPath(path);
                material = new Material(shader);
            }
            else if (material.shader != shader)
                material.shader = shader;

            // Only the VAT properties are written. Surface settings are deliberately left as they are,
            // so a re-bake never discards a look someone tuned by hand.
            material.SetTexture("_VATPositionTex", positions);
            material.SetTexture("_VATNormalTex", normals);

            material.SetFloat("_VATTextureWidth", _textureWidth);
            material.SetFloat("_VATTextureHeight", sliceHeight);
            material.SetFloat("_VATRowsPerFrame", rowsPerFrame);
            material.SetFloat("_VATClipCount", clips.Count);
            material.SetFloat("_VATBlendDuration", _blendDuration);

            // Written as individual vectors, NOT SetVectorArray: material arrays are not serialized,
            // so they survive the bake and vanish on the next asset reload, leaving every clip at
            // rate 0 and frozen on frame 0. Each vector packs two clips.
            for (int pair = 0; pair < MAX_CLIPS / 2; pair++)
            {
                int a = pair * 2;
                int b = a + 1;
                Vector4 packed = new Vector4(
                    a < clips.Count ? clips[a].Frames : 1f,
                    a < clips.Count ? clips[a].Rate : 1f,
                    b < clips.Count ? clips[b].Frames : 1f,
                    b < clips.Count ? clips[b].Rate : 1f);

                material.SetVector($"_VATClipData{pair}", packed);
            }

            material.SetFloat("_VATFrameBlend", _frameBlend ? 1f : 0f);
            if (_frameBlend) material.EnableKeyword("_VAT_FRAMEBLEND");
            else material.DisableKeyword("_VAT_FRAMEBLEND");

            // With no normal texture there is nothing to sample, so the shader has to be told to fall
            // back to the mesh's own normals. Without this it reads an unbound texture and lights the
            // whole mesh from normalize(0, 0, 0).
            material.SetFloat("_VATNoNormals", _bakeNormals ? 0f : 1f);
            if (_bakeNormals) material.DisableKeyword("_VAT_NONORMALS");
            else material.EnableKeyword("_VAT_NONORMALS");

            // Has to travel with the texture it describes: an 8-bit normal read as a half float is a
            // vector between 0 and 1, which points every normal into one corner of the world.
            bool eightBit = _bakeNormals && _normalPrecision == VATNormalPrecision.BYTE;
            bool octahedral = _bakeNormals && _normalPrecision == VATNormalPrecision.OCTAHEDRAL;

            material.SetFloat("_VATNormals8", eightBit ? 1f : 0f);
            material.SetFloat("_VATNormalsOct", octahedral ? 1f : 0f);

            if (eightBit) material.EnableKeyword("_VAT_NORMALS8");
            else material.DisableKeyword("_VAT_NORMALS8");

            if (octahedral) material.EnableKeyword("_VAT_NORMALSOCT");
            else material.DisableKeyword("_VAT_NORMALSOCT");

            // EXPERIMENTAL. Only on when this bake actually wrote a mask into the mesh, because the
            // shader would otherwise rotate every vertex about a pivot that describes nothing.
            /*
             * The float and the keyword have to agree. A material whose toggle reads on while the
             * keyword is off looks fine until someone opens it in the inspector: Unity re-derives the
             * keyword from the toggle, sections switch on with no pivot texture bound, and every one of
             * them starts rotating about the origin.
             */
            bool sectionsOn = SectionsActive && pivots;
            int sectionCount = sectionsOn ? Mathf.Min(_sections.Count, MAX_SECTIONS) : 0;

            material.SetFloat("_VATSections", sectionCount > 0 ? 1f : 0f);
            material.SetFloat("_VATSectionCount", sectionCount);
            material.SetFloat("_VATPivotHeight", Mathf.Max(1, _sectionPivotHeight));
            material.SetTexture("_VATPivotTex", pivots);

            // The pivot texture is what every section rotates about, so the keyword only goes on when
            // one was actually written. Without it the shader would turn vertices about whatever an
            // unbound texture happens to sample as.
            if (sectionCount > 0) material.EnableKeyword("_VAT_SECTIONS");
            else material.DisableKeyword("_VAT_SECTIONS");

            /*
             * Normalized positions are stored as a fraction of the bake's own bounds, so the box has to
             * travel with the texture that describes it. Behind a keyword rather than assumed, because a
             * texture baked as raw half floats read through this decode would land somewhere else
             * entirely.
             */
            bool normalized = _positionPrecision == VATPositionPrecision.NORMALIZED;
            material.SetVector("_VATPositionMin", positionMin);
            material.SetVector("_VATPositionExtent", positionExtent);
            material.SetFloat("_VATPositionNormalized", normalized ? 1f : 0f);

            if (normalized) material.EnableKeyword("_VAT_POSNORM");
            else material.DisableKeyword("_VAT_POSNORM");

            // Per-instance clip state travels in the instancing buffer. Anyone who wants it off can
            // untick it on the material; a bake setting for it only ever meant losing all batching.
            material.enableInstancing = true;

            if (updating) EditorUtility.SetDirty(material);
            else AssetDatabase.CreateAsset(material, path);

            Debug.Log($"[VAT] {(updating ? "Updated" : "Created")} material {path}");
            return material;
        }

        private void CreatePrefab(string baseName, List<VATPartBake> parts, VATClipSet clipSet)
        {
            string path = $"{_outputPath}/{baseName}.prefab";
            bool exists = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (exists && _updateExisting)
            {
                // Edit in place so colliders, scripts and anything else added to it survive.
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    PopulatePrefab(contents, baseName, parts, clipSet, LodScreenPercentages());
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                Debug.Log($"[VAT] Updated prefab {path}");
                return;
            }

            if (exists) path = AssetDatabase.GenerateUniqueAssetPath(path);

            GameObject go = new GameObject(baseName);
            try
            {
                PopulatePrefab(go, baseName, parts, clipSet, LodScreenPercentages());
                PrefabUtility.SaveAsPrefabAsset(go, path);
                Debug.Log($"[VAT] Created prefab {path}");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /*
         * A triangle count says nothing about where a silhouette gives out, so a level can be put on the
         * preview and scrubbed like anything else. Only ever one at a time: the point is to compare it
         * against what the animation is doing, not against another level.
         */
        /// <summary>Source LOD level to draw the preview with, or -1 to draw it whole.</summary>
        private int PreviewedLevel(SkinnedMeshRenderer source)
        {
            if (!LodGroupActive || _previewLod < 0 || _previewLod >= _lodLevels.Count) return -1;

            Mesh mesh = source ? source.sharedMesh : null;
            if (!mesh || mesh.lodCount <= 1) return -1;

            return Mathf.Clamp(_lodLevels[_previewLod].level, 0, mesh.lodCount - 1);
        }

        /// <summary>Drops the preview topology so the next repaint rebuilds it at the chosen level.</summary>
        private void InvalidatePreviewTopology()
        {
            foreach (VATPreviewPart part in _previewParts) part.TopologyReady = false;

            Repaint();
        }

        /*
         * The distance a threshold lands at, which is the number anyone actually reasons about. A
         * fraction of screen height on its own says nothing: half of screen height sounds like a
         * reasonable first step and is a two metre character standing three metres away.
         */
        private float LodDistance(SkinnedMeshRenderer renderer, float screenPercentage)
        {
            if (screenPercentage <= 0f) return 0f;

            Bounds bounds = renderer && renderer.sharedMesh ? renderer.sharedMesh.bounds : default;
            float size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));

            if (size <= 0f) size = 1f;

            // Screen height fraction = size / (2 * distance * tan(fov / 2)), rearranged for distance.
            return size / (2f * screenPercentage * Mathf.Tan(30f * Mathf.Deg2Rad));
        }

        /*
         * Maximum LOD Level is the BEST level a quality setting allows, and LOD 0 is the best there is,
         * so a value above 0 forbids the levels above it on every object in the project. Set higher than
         * a group has levels, nothing qualifies - and Unity draws nothing rather than falling back to the
         * coarsest one it does have. Silent, project wide, and identical on a hand built LODGroup, which
         * is exactly the kind of thing worth catching from inside the tool.
         */
        private void WarnAboutMaximumLodLevel()
        {
            int maximum = QualitySettings.maximumLODLevel;
            if (maximum < _lodLevels.Count) return;

            EditorGUILayout.HelpBox(
                $"Quality level '{QualitySettings.names[QualitySettings.GetQualityLevel()]}' has Maximum " +
                $"LOD Level set to {maximum}, so Unity will not draw anything above LOD {maximum}. This " +
                $"group has {_lodLevels.Count} level(s), so it will be culled at every distance.\n" +
                "Set it to 0 in Project Settings > Quality, or add levels until there are more than " +
                $"{maximum}.",
                MessageType.Error);
        }

        /// <summary>Screen percentages for the group, in the order the levels are listed.</summary>
        private float[] LodScreenPercentages()
        {
            float[] percentages = new float[_lodLevels.Count];
            for (int i = 0; i < _lodLevels.Count; i++) percentages[i] = _lodLevels[i].screenPercentage;

            return percentages;
        }

        /*
         * A renderer per part per level, under one root. That is what keeps instancing: every character
         * sitting at a given level batches with the others at that level, part for part.
         *
         * An LOD holds a Renderer ARRAY, so a bake with several parts puts all of them in each level and
         * they switch together. VATAnimator and VATSectionDriver go on the root and write to all of them,
         * because the group leaves exactly one level enabled at a time.
         */
        private static void ConfigureLodGroup(GameObject root, List<VATPartBake> parts, VATClipSet clipSet,
                                              float[] screenPercentages)
        {
            foreach (Transform child in root.transform.Cast<Transform>().ToArray())
                Object.DestroyImmediate(child.gameObject);

            // A prefab that was not a group has its mesh on the root, and it would go on drawing at
            // full detail underneath every level.
            StripVATComponents(root);

            int levelCount = parts[0].LodMeshes.Length;
            LOD[] levels = new LOD[levelCount];

            for (int i = 0; i < levelCount; i++)
            {
                GameObject holder = new GameObject($"LOD{i}");
                holder.transform.SetParent(root.transform, false);

                List<Renderer> renderers = new List<Renderer>();

                foreach (VATPartBake part in parts)
                {
                    if (part.LodMeshes == null || i >= part.LodMeshes.Length) continue;

                    GameObject child = holder;

                    // One part can live on the level holder itself; several need one child each so they
                    // keep their own material and base map.
                    if (parts.Count > 1)
                    {
                        child = new GameObject(part.Name);
                        child.transform.SetParent(holder.transform, false);
                    }

                    // No clip set: the components that read it belong on the root, once.
                    ConfigureVATObject(child, part.LodMeshes[i], part.Materials, part.Bounds, null);
                    renderers.Add(child.GetComponent<MeshRenderer>());
                }

                float percentage = i < screenPercentages.Length ? screenPercentages[i] : .01f;
                levels[i] = new LOD(percentage, renderers.ToArray());
            }

            LODGroup group = root.GetComponent<LODGroup>();
            if (!group) group = root.AddComponent<LODGroup>();

            group.SetLODs(levels);

            /*
             * Set rather than recalculated. RecalculateBounds reads the renderers, and while a prefab is
             * assembled in memory those have not settled - it came out at 1 for a character nearly two
             * units tall, which halves every transition distance in the group.
             *
             * The animated bounds are already measured and are the better answer anyway: a VAT mesh
             * moves well outside its rest pose, so the group should switch on how big the character
             * gets, not on where it happens to stand at frame zero.
             */
            Bounds groupBounds = parts[0].Bounds;
            group.localReferencePoint = groupBounds.center;
            group.size = Mathf.Max(groupBounds.size.x,
                Mathf.Max(groupBounds.size.y, groupBounds.size.z));

            AttachAnimator(root, clipSet);
        }


        private static void PopulatePrefab(GameObject root, string baseName, List<VATPartBake> parts,
                                           VATClipSet clipSet, float[] lodScreenPercentages)
        {
            if (parts.Count > 0 && parts[0].LodMeshes != null && parts[0].LodMeshes.Length > 0)
            {
                ConfigureLodGroup(root, parts, clipSet, lodScreenPercentages);
                return;
            }

            StripLodGroup(root);

            if (parts.Count == 1)
            {
                // One part lives on the root, so the prefab drops straight in for the original.
                ConfigureVATObject(root, parts[0].SourceMesh, parts[0].Materials, parts[0].Bounds, clipSet);
                return;
            }

            // Several parts get a child each, so every part keeps its own material and base map while
            // sharing one transform, one pivot and one bake.
            StripVATComponents(root);

            foreach (VATPartBake part in parts)
            {
                string childName = part.Name.StartsWith(baseName + "_")
                    ? part.Name.Substring(baseName.Length + 1)
                    : part.Name;

                Transform child = root.transform.Find(childName);
                if (!child)
                {
                    GameObject childObject = new GameObject(childName);
                    childObject.transform.SetParent(root.transform, false);
                    child = childObject.transform;
                }

                ConfigureVATObject(child.gameObject, part.SourceMesh, part.Materials, part.Bounds, clipSet);
            }
        }

        /// <summary>Clears renderer components off a root that used to be a single-part prefab.</summary>
        /*
         * A prefab that was an LOD group and is not one any more keeps its level children and its group
         * component, and they go on drawing beside whatever replaces them - so a re-bake with the
         * section turned off would quietly draw the character twice.
         *
         * Only the objects this baker makes are touched: direct children named LOD followed by a number.
         */
        private static void StripLodGroup(GameObject root)
        {
            LODGroup group = root.GetComponent<LODGroup>();
            if (!group) return;

            foreach (Transform child in root.transform.Cast<Transform>().ToArray())
            {
                string name = child.name;
                if (!name.StartsWith("LOD", System.StringComparison.Ordinal)) continue;

                if (int.TryParse(name.Substring(3), out int _)) Object.DestroyImmediate(child.gameObject);
            }

            Object.DestroyImmediate(group, true);
        }

        private static void StripVATComponents(GameObject go)
        {
            VATBoundsOverride bounds = go.GetComponent<VATBoundsOverride>();
            if (bounds) Object.DestroyImmediate(bounds, true);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer) Object.DestroyImmediate(renderer, true);

            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (filter) Object.DestroyImmediate(filter, true);
        }

        private static void ConfigureVATObject(GameObject go, Mesh mesh, Material[] materials, Bounds bounds,
                                               VATClipSet clipSet)
        {
            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (!filter) filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (!renderer) renderer = go.AddComponent<MeshRenderer>();

            // One generated material per submesh. Assigning them outright is safe because each is
            // updated in place on a re-bake, so anything tuned on the material itself survives.
            Material[] slots = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < slots.Length && materials.Length > 0; i++)
                slots[i] = i < materials.Length ? materials[i] : materials[materials.Length - 1];

            renderer.sharedMaterials = slots;

            VATBoundsOverride boundsOverride = go.GetComponent<VATBoundsOverride>();
            if (!boundsOverride) boundsOverride = go.AddComponent<VATBoundsOverride>();
            boundsOverride.bounds = bounds;

            AttachAnimator(go, clipSet);
        }

        /*
         * Split out because an LOD Group puts the renderers on children and the components that drive
         * them on the root, so the two are not always the same object. Both walk their children for
         * renderers, which covers either shape.
         */
        /// <summary>Puts the playback components on an object, when there is a clip set to give them.</summary>
        private static void AttachAnimator(GameObject go, VATClipSet clipSet)
        {
            if (!clipSet) return;

            VATAnimator animator = go.GetComponent<VATAnimator>();
            if (!animator) animator = go.AddComponent<VATAnimator>();

            // Serialized directly: the component's clip set field is private, and this is what lets
            // the inspector show clip names instead of bare indices.
            SerializedObject serialized = new SerializedObject(animator);
            serialized.FindProperty("clipSet").objectReferenceValue = clipSet;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Only when the bake actually wrote sections, so a prefab that has none is not handed a
            // component with nothing to drive.
            if (clipSet.SectionCount <= 0) return;

            VATSectionDriver driver = go.GetComponent<VATSectionDriver>();
            if (!driver) driver = go.AddComponent<VATSectionDriver>();

            SerializedObject driverObject = new SerializedObject(driver);
            driverObject.FindProperty("clipSet").objectReferenceValue = clipSet;
            driverObject.ApplyModifiedPropertiesWithoutUndo();
        }

        /*
         * A copy of the source mesh with only its positions and normals rewritten, rather than a mesh
         * built from scratch. Unity 6 Mesh LOD levels are extra INDEX buffers over the same vertex
         * buffer - a decimated level reuses the very same vertices - so copying the asset carries them
         * across, and overwriting vertex data leaves them addressing exactly what they addressed
         * before. Rebuilding the mesh would drop them, along with blend shapes and any vertex channel
         * this baker does not know to copy.
         *
         * SV_VertexID keeps meaning the same vertex at every level, which is the whole reason VAT and
         * Mesh LOD work together at all.
         */
        /*
         * Mesh LOD levels are extra index buffers over the same vertex buffer, so they cost nothing to
         * carry and a decimated level still addresses the vertices the textures were baked against.
         * Reported rather than assumed: whether they survive a copy is a property of Unity's Mesh API,
         * and the bake log is where anyone would look to find out.
         */
        private static void ReportMeshLods(VATPartBake part, Mesh baked)
        {
            Mesh source = part.Targets.Count == 1 && part.Targets[0] ? part.Targets[0].sharedMesh : null;
            int before = source ? source.lodCount : 1;

            if (before <= 1) return;

            int after = baked ? baked.lodCount : 1;

            /*
             * Kept, but almost certainly not used. Mesh LOD is selected by the GPU Resident Drawer, and
             * that skips any renderer with a MaterialPropertyBlock on it - which is every VAT instance,
             * because a property block is how one material plays a different clip per character.
             * Said out loud because the levels being present makes it look like they are working.
             */
            if (after >= before)
                Debug.Log($"[VAT] '{part.Name}' kept all {after} Mesh LOD levels. Note that Mesh LOD is " +
                          "driven by the GPU Resident Drawer, which ignores renderers carrying a " +
                          "MaterialPropertyBlock - so a VAT instance always draws level 0. Use an " +
                          "LODGroup of separately baked prefabs instead.");
            else
                Debug.LogWarning($"[VAT] '{part.Name}' came out with {after} Mesh LOD level(s) where the " +
                                 $"source had {before}. Turn Bake Rest Pose Mesh off to keep them, at the " +
                                 "cost of the prefab pointing at the imported mesh.");
        }

        /*
         * One mesh per chosen level, each keeping the FULL vertex buffer and taking only that level's
         * triangles. Keeping the vertices is the point: SV_VertexID goes on meaning the same vertex, so
         * every level reads the same textures and none of them needs a bake of its own.
         *
         * The vertex shader still only runs for vertices the indices reach, so the work drops with the
         * triangle count even though the buffer is whole.
         */
        /// <summary>Writes a mesh asset per LOD level and returns them in group order.</summary>
        private Mesh[] SaveLodMeshes(GameObject instance, VATPartBake part, Mesh full)
        {
            Mesh[] meshes = new Mesh[_lodLevels.Count];

            for (int i = 0; i < _lodLevels.Count; i++)
            {
                string name = $"{part.Name}_LOD{i}";

                /*
                 * One renderer can have its finished mesh cut down, which keeps blend shapes and every
                 * channel. Several have to be merged again at that level, because a merged mesh has no
                 * levels of its own to cut - they belonged to the meshes it was built from.
                 */
                Mesh level = part.Targets.Count == 1
                    ? BuildLodMesh(full, ClampLevel(part.Targets[0], _lodLevels[i].level), name)
                    : BuildCombinedMesh(instance, part.Targets.ToArray(), name, _lodLevels[i].level);

                if (SectionsActive && part.Targets.Count > 1) ApplySectionMask(level, part);

                meshes[i] = SaveMesh(level, name);
            }

            return meshes;
        }

        private static int ClampLevel(SkinnedMeshRenderer target, int level)
        {
            Mesh mesh = target ? target.sharedMesh : null;
            return mesh ? Mathf.Clamp(level, 0, Mathf.Max(0, mesh.lodCount - 1)) : 0;
        }

        /// <summary>A copy of a mesh carrying one of its LOD levels as its only index buffer.</summary>
        private static Mesh BuildLodMesh(Mesh full, int level, string name)
        {
            Mesh copy = Object.Instantiate(full);
            copy.name = name;

            // The copy carries every level; collapsing it to one means this mesh is what it says it is,
            // and stops Unity trying to pick a level inside a level.
            copy.lodCount = 1;

            for (int sub = 0; sub < full.subMeshCount; sub++)
                copy.SetTriangles(full.GetIndices(sub, level), sub, false);

            copy.RecalculateBounds();
            return copy;
        }

        /// <summary>One renderer's mesh, holding the rest pose in the root's space.</summary>
        /// <param name="instance">The throwaway instance being sampled, which defines the root space.</param>
        /// <param name="target">The renderer whose mesh is being copied.</param>
        /// <param name="name">Name given to the copy.</param>
        /// <returns>The copy, not yet saved as an asset.</returns>
        private static Mesh BuildRestPoseMesh(GameObject instance, SkinnedMeshRenderer target, string name)
        {
            Mesh copy = Object.Instantiate(target.sharedMesh);
            copy.name = name;

            Matrix4x4 toRoot = RigidMatrix(instance.transform).inverse * RigidMatrix(target.transform);
            Mesh scratch = new Mesh();

            try
            {
                target.BakeMesh(scratch, false);

                Vector3[] vertices = scratch.vertices;
                Vector3[] normals = scratch.normals;

                // A mesh whose vertex count changed under BakeMesh cannot be rebased safely, and the
                // copy is still better than the import, so it is left as it is.
                if (vertices.Length != copy.vertexCount) return copy;

                for (int i = 0; i < vertices.Length; i++) vertices[i] = toRoot.MultiplyPoint3x4(vertices[i]);
                copy.SetVertices(vertices);

                if (normals.Length == vertices.Length)
                {
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] = toRoot.MultiplyVector(normals[i]).normalized;

                    copy.SetNormals(normals);
                }
            }
            finally
            {
                Object.DestroyImmediate(scratch);
            }

            copy.RecalculateBounds();
            return copy;
        }

        /// <summary>
        /// Concatenates every target renderer into one vertex buffer, in the same order the frame loop
        /// writes them, so SV_VertexID keeps addressing the right texel. Submeshes are kept per source
        /// submesh, so parts with different materials stay separable.
        /// </summary>
        /// <param name="instance">The throwaway instance being sampled, which defines the root space.</param>
        /// <param name="targets">Renderers to merge, in the order the frame loop visits them.</param>
        /// <param name="name">Name given to the generated mesh.</param>
        /// <returns>The merged mesh at the rest pose, not yet saved as an asset.</returns>
        private static Mesh BuildCombinedMesh(GameObject instance, SkinnedMeshRenderer[] targets, string name,
                                              int level = 0)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector4> tangents = new List<Vector4>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector2> uv2s = new List<Vector2>();
            List<Color> colors = new List<Color>();
            List<int[]> subMeshes = new List<int[]>();

            Mesh scratch = new Mesh();
            try
            {
                foreach (SkinnedMeshRenderer target in targets)
                {
                    Matrix4x4 toRoot = RigidMatrix(instance.transform).inverse * RigidMatrix(target.transform);
                    target.BakeMesh(scratch, false);

                    Vector3[] v = scratch.vertices;
                    Vector3[] n = scratch.normals;
                    Vector4[] t = scratch.tangents;
                    Vector2[] uv = scratch.uv;
                    Vector2[] uv2 = scratch.uv2;
                    Color[] c = scratch.colors;
                    int offset = vertices.Count;

                    for (int i = 0; i < v.Length; i++)
                    {
                        vertices.Add(toRoot.MultiplyPoint3x4(v[i]));
                        normals.Add(n.Length == v.Length
                            ? toRoot.MultiplyVector(n[i]).normalized
                            : Vector3.up);

                        // The w is the bitangent's sign, not a coordinate, so it is carried across
                        // rather than transformed.
                        if (t.Length == v.Length)
                        {
                            Vector3 rotated = toRoot.MultiplyVector(t[i]).normalized;
                            tangents.Add(new Vector4(rotated.x, rotated.y, rotated.z, t[i].w));
                        }
                        else tangents.Add(new Vector4(1f, 0f, 0f, 1f));

                        uvs.Add(uv.Length == v.Length ? uv[i] : Vector2.zero);
                        uv2s.Add(uv2.Length == v.Length ? uv2[i] : Vector2.zero);

                        // White rather than clear: a shader that multiplies by vertex colour would
                        // otherwise turn a part that never had colours completely black.
                        colors.Add(c.Length == v.Length ? c[i] : Color.white);
                    }

                    /*
                     * Indices come from the SOURCE mesh rather than the baked snapshot, because that is
                     * where the Mesh LOD levels live - BakeMesh returns a pose, not a level. Vertex
                     * order is identical between the two, so a level's indices address the snapshot's
                     * vertices exactly as they address the source's.
                     */
                    Mesh indexSource = target.sharedMesh ? target.sharedMesh : scratch;
                    int wanted = Mathf.Clamp(level, 0, Mathf.Max(0, indexSource.lodCount - 1));

                    for (int sm = 0; sm < indexSource.subMeshCount; sm++)
                    {
                        int[] tris = indexSource.GetIndices(sm, wanted);
                        for (int i = 0; i < tris.Length; i++) tris[i] += offset;

                        subMeshes.Add(tris);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(scratch);
            }

            Mesh mesh = new Mesh { name = name };
            if (vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uvs);

            mesh.SetUVs(1, uv2s);
            mesh.SetColors(colors);
            mesh.subMeshCount = subMeshes.Count;
            for (int i = 0; i < subMeshes.Count; i++)
                mesh.SetTriangles(subMeshes[i], i);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The bone the section turns on, or null when the picker points at nothing.</summary>
        /*
         * The figure that makes the choice concrete. Taken from the preview's measured bounds, because
         * the step a precision gives you depends entirely on how big the thing being baked is.
         */
        private void DrawPrecisionNote()
        {
            Vector3 extent = _previewBoundsValid ? _previewBounds.size : Vector3.one;
            float step = PositionStepMillimetres(extent);

            string detail = _positionPrecision == VATPositionPrecision.HALF
                ? $"about {step:0.###} mm at the far end of the model, and finer near its origin"
                : $"{step:0.####} mm everywhere";

            string cost = _positionPrecision == VATPositionPrecision.FLOAT
                ? "  Doubles the position texture."
                : _positionPrecision == VATPositionPrecision.NORMALIZED
                    ? "  Same size as half floats."
                    : string.Empty;

            EditorGUILayout.LabelField($"Vertices land on a {detail}.{cost}", EditorStyles.miniLabel);
        }

        /*
         * RGBA64 and RG32 are not universally supported, and the machine a bake is authored on is not
         * always the platform it ships to. Checked where the consequence lands rather than only when
         * the dropdown moves, because a settings asset loaded months later, or a switch to another
         * build target, both arrive with no warning at all otherwise.
         */
        /// <summary>What is unsupported here, and what to fall back to. Empty when all is well.</summary>
        private string UnsupportedFormats()
        {
            List<string> problems = new List<string>();

            if (_positionPrecision == VATPositionPrecision.NORMALIZED
                && !SystemInfo.SupportsTextureFormat(TextureFormat.RGBA64))
                problems.Add("Position Precision is Normalized, which needs RGBA64. Switch to Half.");

            if (_bakeNormals && _normalPrecision == VATNormalPrecision.OCTAHEDRAL
                && !SystemInfo.SupportsTextureFormat(TextureFormat.RG32))
                problems.Add("Normal Precision is Octahedral, which needs RG32. Switch to Byte or Half.");

            return problems.Count > 0 ? string.Join("\n", problems) : string.Empty;
        }

        private TextureFormat NormalFormat()
        {
            switch (_normalPrecision)
            {
                case VATNormalPrecision.OCTAHEDRAL: return TextureFormat.RG32;
                case VATNormalPrecision.BYTE: return TextureFormat.RGBA32;
                default: return TextureFormat.RGBAHalf;
            }
        }

        private int NormalBytesPerPixel() => _normalPrecision == VATNormalPrecision.HALF ? 8 : 4;

        /*
         * Folds the sphere onto a square: project onto the octahedron by dividing through the L1 norm,
         * then reflect the lower hemisphere outward into the corners the upper one leaves empty. Every
         * direction lands somewhere distinct, which is what makes two channels enough.
         */
        /// <summary>Octahedral encoding of a unit normal, each component in -1 to 1.</summary>
        private static Vector2 OctEncode(Vector3 normal)
        {
            float sum = Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z);
            if (sum < .000001f) return Vector2.zero;

            float x = normal.x / sum;
            float y = normal.y / sum;

            if (normal.z < 0f)
            {
                float foldedX = (1f - Mathf.Abs(y)) * (x >= 0f ? 1f : -1f);
                y = (1f - Mathf.Abs(x)) * (y >= 0f ? 1f : -1f);
                x = foldedX;
            }

            return new Vector2(x, y);
        }

        /// <summary>One baked normal, written the way the chosen precision stores it.</summary>
        private Color EncodeNormal(Vector3 normal)
        {
            switch (_normalPrecision)
            {
                case VATNormalPrecision.OCTAHEDRAL:
                {
                    Vector2 folded = OctEncode(normal);
                    return new Color((folded.x * .5f) + .5f, (folded.y * .5f) + .5f, 0f, 1f);
                }

                case VATNormalPrecision.BYTE:
                    return new Color((normal.x * .5f) + .5f, (normal.y * .5f) + .5f,
                        (normal.z * .5f) + .5f, 1f);

                default:
                    return new Color(normal.x, normal.y, normal.z, 1f);
            }
        }

        /// <summary>Typical angular error the chosen precision introduces, in degrees.</summary>
        private float NormalErrorDegrees()
        {
            switch (_normalPrecision)
            {
                case VATNormalPrecision.OCTAHEDRAL: return .0013f;
                case VATNormalPrecision.BYTE: return .1694f;
                default: return .0211f;
            }
        }

        private TextureFormat PositionFormat()
        {
            switch (_positionPrecision)
            {
                case VATPositionPrecision.NORMALIZED: return TextureFormat.RGBA64;
                case VATPositionPrecision.FLOAT: return TextureFormat.RGBAFloat;
                default: return TextureFormat.RGBAHalf;
            }
        }

        private int PositionBytesPerPixel() =>
            _positionPrecision == VATPositionPrecision.FLOAT ? 16 : 8;

        /*
         * The bounds are only known once every frame has been sampled, so positions go into the buffer
         * raw and are rescaled here, in one pass over pixels that are about to be walked again anyway.
         * Encoding during the frame loop would mean baking twice to find out where the model goes.
         *
         * Padding texels are left to fall wherever the maths puts them. Nothing samples them: a texel
         * only gets read if some vertex id addresses it.
         */
        /// <summary>Rescales a part's positions into 0 to 1 across its own bounds.</summary>
        private static void NormalizePositions(VATPartBake part, out Vector3 extent)
        {
            extent = part.Max - part.Min;

            // A flat axis would divide by zero, and a constant maps to 0 just as well as to anything.
            extent = new Vector3(
                Mathf.Max(extent.x, .0001f), Mathf.Max(extent.y, .0001f), Mathf.Max(extent.z, .0001f));

            for (int i = 0; i < part.Positions.Length; i++)
            {
                Color raw = part.Positions[i];

                part.Positions[i] = new Color(
                    (raw.r - part.Min.x) / extent.x,
                    (raw.g - part.Min.y) / extent.y,
                    (raw.b - part.Min.z) / extent.z, 1f);
            }
        }

        /// <summary>Smallest position step the chosen precision can represent, in millimetres.</summary>
        private float PositionStepMillimetres(Vector3 extent)
        {
            float largest = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));

            switch (_positionPrecision)
            {
                case VATPositionPrecision.NORMALIZED: return largest / 65535f * 1000f;
                case VATPositionPrecision.FLOAT: return largest * Mathf.Pow(2f, -24f) * 1000f;

                // Half floats step by a fraction of the VALUE, so the figure that matters is the one at
                // the far end of the model rather than an average across it.
                default: return largest * Mathf.Pow(2f, -11f) * 1000f;
            }
        }

        /*
         * Combined mode has to merge, sections need somewhere to put their mask, and the rest pose
         * option is the whole point. Anything else keeps pointing at the imported mesh, which is the
         * only way Unity 6 Mesh LOD survives a bake.
         */
        /*
         * The signature of a rig whose bones carry a scale: the animation measures one size and the
         * mesh the prefab carries measures another. Harmless while the VAT shader is running and very
         * visible the moment it is not, so it is worth saying out loud rather than leaving to be
         * discovered in a playtest.
         */
        private void WarnIfMeshScaleDisagrees(VATPartBake part)
        {
            if (WritesOwnMesh || !part.SourceMesh) return;

            float baked = part.Bounds.size.magnitude;
            float mesh = part.SourceMesh.bounds.size.magnitude;

            if (baked <= .0001f || mesh <= .0001f) return;

            float ratio = Mathf.Max(mesh / baked, baked / mesh);
            if (ratio < 4f) return;

            Debug.LogWarning($"[VAT] '{part.SourceMesh.name}' is about {ratio:0.#}x the size of the " +
                             "animation baked from it, because its bones carry a scale that only " +
                             "skinning applies. Nothing is wrong while the VAT shader is running, but " +
                             "anything else that draws this prefab - a variant still compiling, a " +
                             "failed shader - will draw it at that size. Turn on Bake Rest Pose Mesh, " +
                             "which copies the mesh rather than rebuilding it and keeps its Mesh LOD " +
                             "levels.");
        }

        /// <summary>Whether this bake writes its own mesh rather than reusing the imported one.</summary>
        private bool WritesOwnMesh => _rendererMode == VATRendererMode.COMBINED_MESH
                                      || SectionsActive || LodGroupActive || _restPoseMesh;

        /*
         * Unity 6 Mesh LOD cannot be used directly here: an instanced batch is one draw over one index
         * range, and Mesh LOD needs a different range per renderer, so instancing wins and the levels go
         * unused. Extracting them into an LODGroup gets both - every character at a given level still
         * instances with the others at that level.
         *
         * And because the levels share the source's vertex buffer, every extracted mesh keeps the full
         * one. SV_VertexID goes on meaning the same vertex, so ONE texture set serves every level and
         * the only thing that grows is a mesh asset per level.
         */
        /// <summary>The LOD Group section: which source levels to bake, and when each takes over.</summary>
        private void DrawLodGroupSettings(SkinnedMeshRenderer renderer)
        {
            bool wanted = VATUi.BeginSection("LOD Group",
                VATIcons.First("LODGroup Icon", "PreMatCube", "Mesh Icon"), _lodGroup,
                "Bake several of the mesh's own LOD levels into one prefab under an LODGroup. Costs a " +
                "mesh per level and no extra texture memory, and keeps GPU instancing.");

            if (wanted != _lodGroup)
            {
                _lodGroup = wanted;
                if (_lodGroup && _lodLevels.Count == 0) SeedLodLevels(renderer);

                _previewLod = -1;
                InvalidatePreviewTopology();
                MarkEdited();
            }

            if (!_lodGroup)
            {
                VATUi.EndSection();
                return;
            }

            int available = AvailableLods(renderer);

            if (available <= 1)
            {
                EditorGUILayout.HelpBox(
                    "This mesh has no Mesh LOD levels to take. Select the model, turn on " +
                    "Generate Mesh LODs in its Model import settings, and apply.",
                    MessageType.Warning);

                VATUi.EndSection();
                return;
            }

            EditorGUILayout.LabelField(
                _rendererMode == VATRendererMode.COMBINED_MESH
                    ? $"Source has {available} Mesh LOD levels, merged per level as the mesh is."
                    : $"Source has {available} Mesh LOD levels.",
                EditorStyles.miniLabel);

            int removeAt = -1;
            for (int i = 0; i < _lodLevels.Count; i++)
                if (DrawLodLevelRow(_lodLevels[i], renderer, available, i)) removeAt = i;

            if (removeAt >= 0)
            {
                _lodLevels.RemoveAt(removeAt);

                // Rows are keyed by position, so anything below the one removed has shifted.
                _previewLod = -1;
                InvalidatePreviewTopology();
                MarkEdited();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_lodLevels.Count >= available))
                {
                    if (VATUi.Button(VATUi.Content("Add Level",
                            "One more step between full detail and the furthest away.",
                            VATIcons.First("Toolbar Plus", "CreateAddNew")), VATUi.GENTLE,
                            GUILayout.Width(120f)))
                    {
                        AddLodLevel(renderer, available);
                        MarkEdited();
                    }
                }
            }

            DrawLodGroupCost(renderer);
            WarnAboutMaximumLodLevel();
            VATUi.EndSection();
        }

        private bool DrawLodLevelRow(VATLodLevel entry, SkinnedMeshRenderer renderer, int available, int index)
        {
            bool remove = false;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"LOD {index}", EditorStyles.boldLabel, GUILayout.Width(60f));

                    EditorGUI.BeginChangeCheck();
                    entry.level = EditorGUILayout.IntPopup(
                        entry.level,
                        System.Linq.Enumerable.Range(0, available)
                            .Select(l => $"Source level {l}  ({LodTriangles(renderer, l):n0} tris)").ToArray(),
                        System.Linq.Enumerable.Range(0, available).ToArray());

                    if (EditorGUI.EndChangeCheck() && _previewLod == index) InvalidatePreviewTopology();

                    bool shown = _previewLod == index;
                    bool wantsPreview = GUILayout.Toggle(shown,
                        VATUi.Content("Preview", "Draw the preview with this level's triangles.",
                            VATIcons.First("ViewToolOrbit", "SceneViewFx")),
                        EditorStyles.miniButton, GUILayout.Width(84f));

                    if (wantsPreview != shown)
                    {
                        _previewLod = wantsPreview ? index : -1;
                        InvalidatePreviewTopology();
                    }

                    // The first level is what everything else is a reduction of, so it always draws.
                    using (new EditorGUI.DisabledScope(index == 0 && _lodLevels.Count == 1))
                    {
                        if (VATUi.Button(VATUi.Content("Remove", "Drop this level from the group.",
                                VATIcons.First("Toolbar Minus", "TreeEditor.Trash")),
                                VATUi.DESTRUCTIVE, GUILayout.Width(90f)))
                            remove = true;
                    }
                }

                float distance = LodDistance(renderer, entry.screenPercentage);

                EditorGUILayout.LabelField(entry.screenPercentage <= 0f
                    ? "Never stops drawing."
                    : $"Takes over past about {distance:0.#} m, at a 60 degree field of view.",
                    EditorStyles.miniLabel);

                entry.screenPercentage = EditorGUILayout.Slider(
                    index == _lodLevels.Count - 1
                        ? new GUIContent("Cull Below",
                            "Fraction of screen height at which the object stops drawing altogether. " +
                            "0 never culls it, which is usually what you want - anything higher makes " +
                            "distant characters vanish.")
                        : new GUIContent("Switch Below",
                            "Fraction of screen height at which the next level takes over. Lower is " +
                            "further away, so these descend down the list."),
                    entry.screenPercentage, 0f, 1f);
            }

            return remove;
        }

        /*
         * The number people expect to go up here is texture memory, and it does not. Levels share the
         * source's vertex buffer, so they share the textures too - what grows is one mesh asset per
         * level, which next to a VAT texture set is nothing. Worth showing both so the trade is clear.
         */
        private void DrawLodGroupCost(SkinnedMeshRenderer renderer)
        {
            Mesh mesh = renderer ? renderer.sharedMesh : null;
            if (!mesh) return;

            long baseTriangles = LodTriangles(renderer, 0);
            long groupTriangles = 0L;

            foreach (VATLodLevel entry in _lodLevels) groupTriangles += LodTriangles(renderer, entry.level);

            // Positions, normals, one UV and the mask channel, near enough for a figure to judge by.
            float meshMegabytes = mesh.vertexCount * 44f * _lodLevels.Count / (1024f * 1024f);

            EditorGUILayout.HelpBox(
                $"{_lodLevels.Count} level(s), {groupTriangles:n0} triangles across all of them against " +
                $"{baseTriangles:n0} at full detail.\n" +
                $"No extra texture memory - every level reads the same textures. About " +
                $"{meshMegabytes:0.#} MB of extra mesh assets.",
                MessageType.None);
        }

        private void SeedLodLevels(SkinnedMeshRenderer renderer)
        {
            int available = AvailableLods(renderer);
            _lodLevels.Clear();

            // Three steps by default: full detail, something around the middle, and the coarsest.
            int[] wanted = available >= 3
                ? new[] { 0, available / 2, available - 1 }
                : new[] { 0 };

            for (int i = 0; i < wanted.Length; i++)
                _lodLevels.Add(new VATLodLevel
                {
                    level = wanted[i],

                    // The last entry is where the object stops drawing altogether, so it starts at 0.
                    // The rest spread from a sixth of screen height down to a fiftieth, which on a
                    // two metre character is roughly ten metres out to sixty. Half of screen height,
                    // the obvious looking number, is a character at arm's length.
                    screenPercentage = i == wanted.Length - 1
                        ? 0f
                        : Mathf.Lerp(.15f, .03f, wanted.Length > 2 ? i / (wanted.Length - 2f) : 0f)
                });
        }

        private void AddLodLevel(SkinnedMeshRenderer renderer, int available)
        {
            int last = _lodLevels.Count > 0 ? _lodLevels[_lodLevels.Count - 1].level : -1;

            // Whatever was last becomes an ordinary step, and the new level takes over as the point the
            // object stops drawing.
            if (_lodLevels.Count > 0)
            {
                VATLodLevel previous = _lodLevels[_lodLevels.Count - 1];
                if (previous.screenPercentage <= 0f) previous.screenPercentage = .05f;
            }

            _lodLevels.Add(new VATLodLevel
            {
                level = Mathf.Min(last + 1, available - 1),
                screenPercentage = 0f
            });
        }

        /// <summary>True when this bake writes an LODGroup rather than a single renderer.</summary>
        private bool LodGroupActive => _lodGroup && _lodLevels.Count > 0;

        private static List<VATLodLevel> CloneLodLevels(List<VATLodLevel> source)
        {
            List<VATLodLevel> copy = new List<VATLodLevel>();
            if (source == null) return copy;

            foreach (VATLodLevel level in source)
                copy.Add(new VATLodLevel { level = level.level, screenPercentage = level.screenPercentage });

            return copy;
        }

        /// <summary>Mesh LOD levels the chosen renderer's mesh actually carries.</summary>
        private int AvailableLods(SkinnedMeshRenderer renderer)
        {
            // Every renderer in the bake has to have a level for it to be worth offering, so the
            // fewest anyone carries is the number the group can actually use.
            if (_rendererMode == VATRendererMode.SELECTED)
            {
                Mesh single = renderer ? renderer.sharedMesh : null;
                return single ? Mathf.Max(1, single.lodCount) : 1;
            }

            int fewest = int.MaxValue;

            foreach (SkinnedMeshRenderer other in _renderers)
            {
                Mesh mesh = other ? other.sharedMesh : null;
                if (mesh) fewest = Mathf.Min(fewest, Mathf.Max(1, mesh.lodCount));
            }

            return fewest == int.MaxValue ? 1 : fewest;
        }

        /// <summary>Triangles one source level draws, which is what makes a level worth picking.</summary>
        private long LodTriangles(SkinnedMeshRenderer renderer, int level)
        {
            long indices = 0L;

            foreach (SkinnedMeshRenderer target in LodSourceRenderers(renderer))
            {
                Mesh mesh = target ? target.sharedMesh : null;
                if (!mesh) continue;

                int wanted = Mathf.Clamp(level, 0, Mathf.Max(0, mesh.lodCount - 1));
                for (int sub = 0; sub < mesh.subMeshCount; sub++) indices += mesh.GetIndexCount(sub, wanted);
            }

            return indices / 3L;
        }

        /// <summary>The renderers a level is counted over, which is all of them outside Selected mode.</summary>
        private IEnumerable<SkinnedMeshRenderer> LodSourceRenderers(SkinnedMeshRenderer renderer)
        {
            if (_rendererMode == VATRendererMode.SELECTED) return new[] { renderer };

            return _renderers;
        }

        /// <summary>True when this bake has at least one section to write.</summary>
        private bool SectionsActive => _sectionsEnabled && _sections.Count > 0;

        /*
         * A section is a part of the baked mesh that a script can still turn or move: a head that looks
         * at something, a torso that leans, an arm that recoils. The region is taken from the rig's own
         * skin weights, so the falloff at its base is the one the rigger painted.
         *
         * Four at most, because the mask rides in the four components of UV3 and adding a fifth would
         * mean another vertex channel and another texture fetch in every pass.
         */
        /// <summary>The section list, its bone pickers and the warnings they earn.</summary>
        private void DrawSectionSettings(SkinnedMeshRenderer renderer)
        {
            bool wanted = VATUi.BeginSection("Sections",
                VATIcons.First("Avatar Icon", "AvatarSelector", "Animator Icon"), _sectionsEnabled,
                "Keep part of the mesh drivable after the bake. Off means no mask, no pivot texture " +
                "and no section code in the shader, whatever is configured below.");

            if (wanted != _sectionsEnabled)
            {
                _sectionsEnabled = wanted;

                // The highlight paints and poses the preview, and neither makes sense for a section
                // this bake is not going to write.
                _highlightSection = -1;
                _previewPivotValid = false;
                InvalidateSectionCache();
                MarkEdited();
            }

            if (!_sectionsEnabled)
            {
                VATUi.EndSection();
                return;
            }

            if (_sections.Count == 0)
                EditorGUILayout.LabelField(
                    "None. Add one to keep part of the mesh drivable after the bake.",
                    EditorStyles.miniLabel);

            int removeAt = -1;
            for (int i = 0; i < _sections.Count; i++)
                if (DrawSectionRow(_sections[i], renderer, i)) removeAt = i;

            if (removeAt >= 0)
            {
                _sections.RemoveAt(removeAt);
                if (_highlightSection >= _sections.Count) _highlightSection = -1;

                // Rows are keyed by position, so removing one shifts everything below it.
                _expandedSections.Clear();

                InvalidateSectionCache();
                MarkEdited();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_sections.Count >= MAX_SECTIONS))
                {
                    if (VATUi.Button(VATUi.Content("Add Section",
                            $"Up to {MAX_SECTIONS}, one per component of the mesh's fourth UV channel.",
                            VATIcons.First("Toolbar Plus", "CreateAddNew")), VATUi.GENTLE, GUILayout.Width(140f)))
                    {
                        _sections.Add(new VATSectionSetup
                        {
                            name = $"Section {_sections.Count + 1}",
                            boneName = FirstFreeBone(renderer)
                        });
                        MarkEdited();
                    }
                }
            }

            DrawSectionWarnings(renderer);

            VATUi.EndSection();
        }

        /// <summary>One section's row. Returns true when its remove button was pressed.</summary>
        private bool DrawSectionRow(VATSectionSetup section, SkinnedMeshRenderer renderer, int index)
        {
            bool remove = false;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    section.name = EditorGUILayout.TextField(section.name);

                    bool lit = _highlightSection == index;
                    bool wants = GUILayout.Toggle(lit,
                        VATUi.Content("Highlight",
                            "Paints this section's weights onto the preview: cold where it has no hold, " +
                            "warm where it owns the vertex outright.",
                            VATIcons.First("ViewToolOrbit", "SceneViewFx")),
                        EditorStyles.miniButton, GUILayout.Width(96f));

                    if (wants != lit)
                    {
                        _highlightSection = wants ? index : -1;
                        _previewPivotValid = false;
                        InvalidateSectionCache();
                    }

                    if (VATUi.Button(VATUi.Content("Remove", "Drop this section from the bake.",
                            VATIcons.First("Toolbar Minus", "TreeEditor.Trash")),
                            VATUi.DESTRUCTIVE, GUILayout.Width(90f)))
                        remove = true;
                }

                List<SkinnedMeshRenderer> targets = SectionRenderers(renderer);
                Transform[] bones = SectionBones(targets);

                if (bones.Length == 0)
                {
                    EditorGUILayout.HelpBox(
                        targets.Count > 1
                            ? "None of the renderers being baked has bones, so there are no skin " +
                              "weights to derive a section from."
                            : "This renderer has no bones, so there are no skin weights to derive a " +
                              "section from.",
                        MessageType.Warning);
                    return remove;
                }

                // Bones another section already claimed are left out of the list, so two sections can
                // never end up on the same one. This section's own bone stays in, or picking anything
                // else would be a one way trip.
                List<string> choices = new List<string>();
                for (int i = 0; i < bones.Length; i++)
                {
                    if (!bones[i]) continue;

                    bool mine = bones[i].name == section.boneName;
                    if (!mine && BoneTaken(section, bones[i].name)) continue;
                    if (!mine && !VATUiSettings.ShowWeightlessBones &&
                        !BoneMovesAnything(targets, bones[i].name))
                    {
                        continue;
                    }

                    choices.Add(bones[i].name);
                }

                if (choices.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Every bone on the mesh being baked is already claimed by another section.",
                        MessageType.Warning);
                    return remove;
                }

                /*
                 * Folded away by default. A section is usable with nothing but a bone, and four rows of
                 * tuning per section - times four sections - buried the rest of the window behind
                 * numbers most bakes never touch.
                 */
                bool expanded = _expandedSections.Contains(index);
                bool tuned = section.priority != 0 || section.pivotOffset != Vector3.zero
                             || section.maxAngle > 0f
                             || (section.falloff > 0f && !Mathf.Approximately(section.falloff, 1f));

                string[] names = choices.ToArray();
                int current = System.Array.IndexOf(names, section.boneName);

                using (new EditorGUILayout.HorizontalScope())
                {
                    int picked = EditorGUILayout.Popup(
                        new GUIContent("Bone", "The section is this bone and every bone parented under it."),
                        Mathf.Max(0, current), names);

                    if (picked != current)
                    {
                        section.boneName = names[picked];
                        InvalidateSectionCache();
                    }

                    // The star carries what a tint alone would not: colours can be turned off, and a
                    // section tuned once and folded away otherwise looks identical to a default one.
                    if (VATUi.Button(VATUi.Content(tuned && !expanded ? "Adjustments *" : "Adjustments",
                            "Priority, falloff, pivot nudge and angle limit for this section." +
                            (tuned ? " This one has been adjusted." : string.Empty),
                            VATIcons.First("Settings", "_Popup", "EditorSettings Icon")),
                            expanded ? VATUi.PRIMARY : (tuned ? VATUi.CAUTION : Color.white),
                            GUILayout.Width(140f)))
                    {
                        if (expanded) _expandedSections.Remove(index);
                        else _expandedSections.Add(index);
                    }
                }

                if (expanded) DrawSectionAdjustments(section);

                if (_highlightSection == index) DrawSectionTestDrive();

                int channel = OrderedSections().IndexOf(section);
                Vector2Int coverage = Vector2Int.zero;
                int vertexCount = 0;
                int boneCount = 0;

                foreach (SkinnedMeshRenderer target in targets)
                {
                    coverage += SectionCoverage(target, channel);
                    vertexCount += target.sharedMesh.vertexCount;
                    boneCount = Mathf.Max(boneCount, BoneSubtree(target, section.boneName).Count);
                }

                int covered = coverage.x + coverage.y;

                if (covered == 0)
                    EditorGUILayout.HelpBox(
                        $"'{section.boneName}' moves no vertices on " +
                        (targets.Count > 1 ? "any mesh being baked" : "this mesh") +
                        ", so this section would bake an empty mask. Either the bone carries no skin " +
                        "weight, or a higher priority section has taken every vertex it claimed.",
                        MessageType.Warning);
                else
                    EditorGUILayout.LabelField(
                        $"{covered} of {vertexCount} vertices  ({coverage.x} full, {coverage.y} partial)" +
                        $"   {boneCount} bone(s)",
                        EditorStyles.miniLabel);
            }

            return remove;
        }

        /*
         * Two sections that share vertices are fine - priority hands the contested ones to the higher
         * section and the falloff between them stays smooth. Two sections where one bone is an ANCESTOR
         * of the other are a different thing entirely, and priority cannot fix it: the inner section
         * owns its vertices outright, so moving the outer one leaves the inner one behind.
         */
        private void DrawSectionWarnings(SkinnedMeshRenderer renderer)
        {
            if (_sections.Count < 2 || !renderer) return;

            for (int i = 0; i < _sections.Count; i++)
            {
                for (int j = i + 1; j < _sections.Count; j++)
                {
                    if (!string.Equals(_sections[i].name, _sections[j].name,
                            System.StringComparison.OrdinalIgnoreCase)) continue;

                    EditorGUILayout.HelpBox(
                        $"Two sections are both called '{_sections[i].name}'. Gameplay code addresses " +
                        "them by name, so every call would reach the first one and the second would be " +
                        "undrivable. Rename one.",
                        MessageType.Error);
                    break;
                }
            }

            Transform[] bones = renderer.bones;
            List<string> nested = new List<string>();

            foreach (VATSectionSetup outer in _sections)
            {
                Transform outerBone = FindBone(bones, outer.boneName);
                if (!outerBone) continue;

                foreach (VATSectionSetup inner in _sections)
                {
                    if (inner == outer) continue;

                    Transform innerBone = FindBone(bones, inner.boneName);
                    if (innerBone && innerBone != outerBone && innerBone.IsChildOf(outerBone))
                        nested.Add($"'{inner.name}' sits inside '{outer.name}'");
                }
            }

            if (nested.Count == 0) return;

            EditorGUILayout.HelpBox(
                string.Join("\n", nested) +
                "\n\nPriority still decides who owns the shared vertices, so nothing moves twice. But " +
                "the inner section will NOT follow the outer one: turn the outer and the inner stays " +
                "where it was, which on a spine and a head means the head comes off. Chained sections " +
                "are not built yet.",
                MessageType.Warning);
        }

        /*
         * The mask is a per-vertex number and the interesting part of it is the FALLOFF, so this paints
         * it as a ramp on the preview mesh rather than picking out the affected triangles. A hard
         * boundary would hide exactly the thing worth looking at.
         *
         * What it shows is the weight AFTER priority has been resolved, so a section that has lost its
         * skull to a higher one looks like it has lost it, rather than showing a claim it will not get.
         */
        /// <summary>Ramp for one section weight: unclaimed, through claimed, to fully owned.</summary>
        private static Color MaskColor(float weight)
        {
            Color cold = new Color(.16f, .17f, .2f);

            return weight <= .5f
                ? Color.Lerp(cold, VATUi.PRIMARY, weight * 2f)
                : Color.Lerp(VATUi.PRIMARY, VATUi.CAUTION, (weight - .5f) * 2f);
        }

        /// <summary>The unlit vertex colour material the highlight draws with.</summary>
        private Material MaskMaterial()
        {
            if (_maskMaterial) return _maskMaterial;

            Shader shader = Shader.Find("Hidden/Mi/VAT Mask Preview");
            if (!shader) return null;

            _maskMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _maskMaterial;
        }

        /// <summary>Drops everything derived from the section list so the next repaint rebuilds it.</summary>
        private void InvalidateSectionCache()
        {
            _sectionCoverage.Clear();
            foreach (VATPreviewPart part in _previewParts) part.SectionKey = null;
        }

        /// <summary>Fills a preview part's cached section weights when they are missing or stale.</summary>
        private void EnsureSectionWeights(VATPreviewPart part, int vertexCount)
        {
            string key = $"{vertexCount}:{SectionFingerprint()}";
            if (part.SectionKey == key && part.SectionWeights != null) return;

            List<Vector4> masks = new List<Vector4>();
            int affected = 0;
            AppendSectionMasks(part.Source, masks, ref affected);

            Vector4[] weights = new Vector4[vertexCount];
            for (int v = 0; v < vertexCount && v < masks.Count; v++) weights[v] = masks[v];

            part.SectionWeights = weights;
            part.HighlightColors = null;
            part.SectionKey = key;
        }

        /*
         * The same blend VAT_ApplySection does: lerp toward the turn by the vertex weight and normalize,
         * NOT a slerp. Matching the shader matters more here than being marginally more correct than it,
         * because the whole point of the preview is to answer what the bake will look like.
         */
        private static Quaternion SectionBlend(Quaternion turn, float mask)
        {
            Quaternion blended = new Quaternion(
                turn.x * mask, turn.y * mask, turn.z * mask, Mathf.Lerp(1f, turn.w, mask));

            float length = Mathf.Sqrt((blended.x * blended.x) + (blended.y * blended.y)
                                      + (blended.z * blended.z) + (blended.w * blended.w));

            if (length < .0001f) return Quaternion.identity;

            return new Quaternion(blended.x / length, blended.y / length,
                blended.z / length, blended.w / length);
        }

        /// <summary>The section's Max Angle, applied the same way the runtime driver applies it.</summary>
        private static Quaternion LimitTurn(Quaternion turn, float maxAngle)
        {
            if (maxAngle <= 0f) return turn;

            float angle = Quaternion.Angle(Quaternion.identity, turn);
            if (angle <= maxAngle) return turn;

            return Quaternion.Slerp(Quaternion.identity, turn, maxAngle / angle);
        }

        /// <summary>The section being test driven, or null when none is.</summary>
        private VATSectionSetup TestSection()
        {
            return _highlightSection >= 0 && _highlightSection < _sections.Count
                ? _sections[_highlightSection]
                : null;
        }

        /// <summary>Bone indices that actually carry weight somewhere on the mesh.</summary>
        private HashSet<int> WeightedBones(SkinnedMeshRenderer renderer)
        {
            Mesh mesh = renderer ? renderer.sharedMesh : null;
            int key = mesh ? mesh.GetInstanceID() : 0;

            if (_weightedBones.TryGetValue(key, out HashSet<int> cached)) return cached;

            HashSet<int> used = new HashSet<int>();
            BoneWeight[] weights = mesh ? mesh.boneWeights : new BoneWeight[0];

            foreach (BoneWeight weight in weights)
            {
                if (weight.weight0 > 0f) used.Add(weight.boneIndex0);
                if (weight.weight1 > 0f) used.Add(weight.boneIndex1);
                if (weight.weight2 > 0f) used.Add(weight.boneIndex2);
                if (weight.weight3 > 0f) used.Add(weight.boneIndex3);
            }

            _weightedBones[key] = used;
            return used;
        }

        /// <summary>Whether a bone, or anything parented under it, moves any vertex at all.</summary>
        private bool BoneMovesAnything(SkinnedMeshRenderer renderer, string boneName)
        {
            HashSet<int> weighted = WeightedBones(renderer);

            foreach (int index in BoneSubtree(renderer, boneName))
                if (weighted.Contains(index)) return true;

            return false;
        }

        /// <summary>Whether a bone moves any vertex on any renderer this bake will read.</summary>
        private bool BoneMovesAnything(List<SkinnedMeshRenderer> renderers, string boneName)
        {
            foreach (SkinnedMeshRenderer renderer in renderers)
                if (BoneMovesAnything(renderer, boneName)) return true;

            return false;
        }

        /*
         * Six meshes skinned to one armature hand back the same bone array six times, so this is
         * usually a copy. It is a union rather than "whichever renderer is selected" because nothing
         * guarantees that: a character can carry a prop skinned to its own extra bones, and picking
         * the selected renderer's array would hide them or hide the body's, depending on the index.
         */
        /// <summary>Every bone across the renderers being baked, in first-seen order, without repeats.</summary>
        private static Transform[] SectionBones(List<SkinnedMeshRenderer> renderers)
        {
            List<Transform> bones = new List<Transform>();
            HashSet<Transform> seen = new HashSet<Transform>();

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                if (!renderer) continue;

                foreach (Transform bone in renderer.bones)
                    if (bone && seen.Add(bone)) bones.Add(bone);
            }

            return bones.ToArray();
        }

        /*
         * A section's mask is built over every renderer the bake reads - BuildSectionMasks walks
         * part.Targets, and Combined Mesh puts all of them in one part - so anything that reports on a
         * section has to span the same set. Asking one renderer said a head bone moved nothing on a
         * six mesh character whenever the selected renderer was not the head, while the preview
         * highlight, which does walk every part, painted the head correctly.
         */
        /// <summary>Every renderer a section's mask will be built over, given the current mode.</summary>
        private List<SkinnedMeshRenderer> SectionRenderers(SkinnedMeshRenderer selected)
        {
            List<SkinnedMeshRenderer> list = new List<SkinnedMeshRenderer>();

            if (_rendererMode == VATRendererMode.SELECTED)
            {
                if (selected && selected.sharedMesh) list.Add(selected);
                return list;
            }

            foreach (SkinnedMeshRenderer renderer in _renderers)
                if (renderer && renderer.sharedMesh) list.Add(renderer);

            return list;
        }

        /*
         * Cached for every channel at once rather than one at a time. Building the mask hands back all
         * four weights per vertex whichever channel was asked for, so keying the cache by channel meant
         * four sections across six renderers rebuilt the same masks twenty-four times instead of six.
         */
        /// <summary>Vertices this section ends up owning, as (full weight, partial weight).</summary>
        private Vector2Int SectionCoverage(SkinnedMeshRenderer renderer, int channel)
        {
            if (channel < 0 || channel >= MAX_SECTIONS) return Vector2Int.zero;

            Mesh mesh = renderer ? renderer.sharedMesh : null;
            string key = $"{(mesh ? mesh.GetInstanceID() : 0)}:{SectionFingerprint()}";

            if (_sectionCoverage.TryGetValue(key, out Vector2Int[] cached)) return cached[channel];

            List<Vector4> masks = new List<Vector4>();
            int affected = 0;
            AppendSectionMasks(renderer, masks, ref affected);

            Vector2Int[] coverage = new Vector2Int[MAX_SECTIONS];

            for (int v = 0; v < masks.Count; v++)
            {
                Vector4 mask = masks[v];

                for (int c = 0; c < MAX_SECTIONS; c++)
                {
                    if (mask[c] >= .999f) coverage[c].x++;
                    else if (mask[c] > .001f) coverage[c].y++;
                }
            }

            _sectionCoverage[key] = coverage;
            return coverage[channel];
        }

        /// <summary>Everything about the section list that changes what a mask comes out as.</summary>
        private string SectionFingerprint()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            foreach (VATSectionSetup section in OrderedSections())
                builder.Append(section.boneName).Append('/').Append(section.priority)
                    .Append('/').Append(section.falloff).Append('|');

            return builder.ToString();
        }

        /// <summary>Paints one preview part with the highlighted section, or puts it back.</summary>
        private void ApplyHighlight(VATPreviewPart part, int childIndex)
        {
            bool wanted = _highlightSection >= 0 && _highlightSection < _sections.Count;
            MeshRenderer child = _previewDisplay
                ? _previewDisplay.transform.GetChild(childIndex).GetComponent<MeshRenderer>()
                : null;

            if (!child || !part.Display) return;

            if (!wanted)
            {
                if (!part.Highlighted) return;

                part.Display.colors = null;
                child.sharedMaterials = part.Source ? part.Source.sharedMaterials : new Material[0];
                part.Highlighted = false;
                return;
            }

            VATSectionSetup section = _sections[_highlightSection];
            int channel = OrderedSections().IndexOf(section);

            EnsureSectionWeights(part, part.Display.vertexCount);

            if (part.HighlightColors == null || part.HighlightColors.Length != part.Display.vertexCount)
            {
                Color[] colors = new Color[part.Display.vertexCount];
                for (int v = 0; v < colors.Length; v++)
                    colors[v] = MaskColor(channel >= 0 ? part.SectionWeights[v][channel] : 0f);

                part.HighlightColors = colors;
            }

            part.Display.colors = part.HighlightColors;

            if (part.Highlighted) return;

            Material mask = MaskMaterial();
            if (!mask) return;

            Material[] materials = new Material[Mathf.Max(1, part.Display.subMeshCount)];
            for (int i = 0; i < materials.Length; i++) materials[i] = mask;

            child.sharedMaterials = materials;
            part.Highlighted = true;
        }

        /// <summary>The tuning a section can do without, folded away until it is wanted.</summary>
        private void DrawSectionAdjustments(VATSectionSetup section)
        {
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            section.priority = EditorGUILayout.IntField(
                new GUIContent("Priority",
                    "Higher wins the vertices two sections both claim. Equal priorities fall back " +
                    "to the order they were added in."),
                section.priority);

            section.falloff = EditorGUILayout.Slider(
                new GUIContent("Falloff",
                    "Shapes the rig's own blend without changing what the section covers. Above 1 " +
                    "pulls it toward the core for a crisper hinge, below 1 spreads it further down " +
                    "the neck or the waist. Turn Highlight on and drag this to see it."),
                section.falloff <= 0f ? 1f : section.falloff, .25f, 4f);

            if (EditorGUI.EndChangeCheck()) InvalidateSectionCache();

            section.pivotOffset = EditorGUILayout.Vector3Field(
                new GUIContent("Pivot Nudge",
                    "Moves the hinge in object space. The bone is usually right, but a head reads " +
                    "better turning slightly higher than the neck joint itself."),
                section.pivotOffset);

            section.maxAngle = Mathf.Max(0f, EditorGUILayout.FloatField(
                new GUIContent("Max Angle",
                    "Largest rotation the runtime will apply, in degrees. 0 means no limit. " +
                    "Recorded on the clip set so gameplay code cannot over-rotate a neck."),
                section.maxAngle));

            EditorGUI.indentLevel--;
        }

        /*
         * Turning the section in the PREVIEW, before anything is baked. Without this, checking whether a
         * falloff creases or a pivot nudge sits right means baking, entering play mode, driving it from
         * the inspector, and coming back - which is the loop the preview exists to remove.
         *
         * The preview instance has already been sampled to the current frame, so the bone it reads is
         * the animated pivot: the head stays on the neck through a walk cycle here exactly as it will
         * once the pivot texture is doing the same job on the GPU.
         */
        private void DrawSectionTestDrive()
        {
            EditorGUILayout.Space(2f);

            EditorGUI.BeginChangeCheck();

            _testRotation = EditorGUILayout.Vector3Field(
                new GUIContent("Test Turn",
                    "Turns this section in the preview only. Nothing here is baked - it is for seeing " +
                    "whether the falloff, the pivot and the angle limit behave before committing."),
                _testRotation);

            _testWeight = EditorGUILayout.Slider(
                new GUIContent("Test Weight", "Blends the test turn in, the way a script would at runtime."),
                _testWeight, 0f, 1f);

            if (EditorGUI.EndChangeCheck()) Repaint();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (VATUi.Button(VATUi.Content("Reset Turn", "Put the section back to its baked pose.",
                        VATIcons.First("Refresh", "RotateTool")), VATUi.GENTLE, GUILayout.Width(110f)))
                {
                    _testRotation = Vector3.zero;
                    _testWeight = 1f;
                    Repaint();
                }
            }
        }

        /// <summary>Whether some other section is already using a bone.</summary>
        private bool BoneTaken(VATSectionSetup exclude, string boneName)
        {
            foreach (VATSectionSetup section in _sections)
                if (section != exclude && section.boneName == boneName) return true;

            return false;
        }

        /// <summary>The first unclaimed bone, so a new section never lands on one already in use.</summary>
        private string FirstFreeBone(SkinnedMeshRenderer renderer)
        {
            // Across the whole bake, not just the selected renderer: on a character split into six
            // meshes, a bone that only weights the head is still a perfectly good section.
            List<SkinnedMeshRenderer> targets = SectionRenderers(renderer);
            Transform[] bones = SectionBones(targets);

            for (int i = 0; i < bones.Length; i++)
            {
                if (!bones[i] || BoneTaken(null, bones[i].name)) continue;
                if (!VATUiSettings.ShowWeightlessBones && !BoneMovesAnything(targets, bones[i].name)) continue;

                return bones[i].name;
            }

            return null;
        }

        /// <summary>Sections highest priority first. Ties keep the order they were added in.</summary>
        private List<VATSectionSetup> OrderedSections()
        {
            return _sections.OrderByDescending(section => section.priority).ToList();
        }

        private static Transform FindBone(Transform[] bones, string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return null;

            for (int i = 0; i < bones.Length; i++)
                if (bones[i] && bones[i].name == boneName) return bones[i];

            return null;
        }

        /// <summary>Indices of a bone and every bone under it, which is what the mask sums over.</summary>
        private HashSet<int> BoneSubtree(SkinnedMeshRenderer renderer, string boneName)
        {
            // Keyed on the renderer rather than its mesh, because two renderers can share a mesh and
            // still be skinned to different bone arrays.
            string key = $"{(renderer ? renderer.GetInstanceID() : 0)}:{boneName}";
            if (_boneSubtrees.TryGetValue(key, out HashSet<int> cached)) return cached;

            HashSet<int> indices = new HashSet<int>();
            Transform[] bones = renderer ? renderer.bones : new Transform[0];
            Transform root = FindBone(bones, boneName);

            if (root)
            {
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] && bones[i].IsChildOf(root)) indices.Add(i);
            }

            _boneSubtrees[key] = indices;
            return indices;
        }

        /*
         * A power curve rather than anything cleverer, because it has the property that matters: 0 stays
         * 0 and 1 stays 1, so the vertices the section fully owns and the ones it never touched are
         * both left exactly alone. Only the blend band between them moves.
         */
        /// <summary>Tightens or spreads the rig's falloff without changing what the section covers.</summary>
        private static float ShapeFalloff(float weight, float falloff)
        {
            if (falloff <= 0f || Mathf.Approximately(falloff, 1f)) return weight;

            return Mathf.Pow(weight, falloff);
        }

        private static float SkinWeight(BoneWeight weight, HashSet<int> subtree)
        {
            float total = 0f;

            if (subtree.Contains(weight.boneIndex0)) total += weight.weight0;
            if (subtree.Contains(weight.boneIndex1)) total += weight.weight1;
            if (subtree.Contains(weight.boneIndex2)) total += weight.weight2;
            if (subtree.Contains(weight.boneIndex3)) total += weight.weight3;

            return total;
        }

        /*
         * The rig already knows which vertices belong to a limb and how the influence fades at its base,
         * so a section's weight is the sum of each vertex's skin weights for its bone and that bone's
         * descendants. Nothing here invents a falloff.
         *
         * Priority is resolved HERE rather than in the shader, and costs nothing at runtime as a result.
         * Compositing each section against what higher ones already claimed - the same arithmetic as
         * drawing one layer over another - hands vertices over across the falloff instead of at a hard
         * edge, so the seam between two neighbouring sections stays smooth.
         */
        /// <summary>Per-vertex section weights for one part, concatenated in target order.</summary>
        private List<Vector4> BuildSectionMasks(VATPartBake part, out int affected)
        {
            List<Vector4> masks = new List<Vector4>();
            affected = 0;

            foreach (SkinnedMeshRenderer target in part.Targets)
                AppendSectionMasks(target, masks, ref affected);

            return masks;
        }

        /// <summary>One renderer's worth of weights, appended in vertex order.</summary>
        private void AppendSectionMasks(SkinnedMeshRenderer target, List<Vector4> masks, ref int affected)
        {
            List<VATSectionSetup> ordered = OrderedSections();
            Mesh mesh = target ? target.sharedMesh : null;
            int count = mesh ? mesh.vertexCount : 0;
            BoneWeight[] weights = mesh ? mesh.boneWeights : new BoneWeight[0];

            // A mesh with no skin weights cannot say which vertices belong to a bone, so it
            // contributes zeros rather than a guess.
            bool skinned = count > 0 && weights.Length == count;

            HashSet<int>[] subtrees = new HashSet<int>[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
                subtrees[i] = BoneSubtree(target, ordered[i].boneName);

            for (int v = 0; v < count; v++)
            {
                Vector4 mask = Vector4.zero;
                float claimed = 0f;

                for (int i = 0; i < ordered.Count && i < MAX_SECTIONS; i++)
                {
                    float weight = skinned ? SkinWeight(weights[v], subtrees[i]) : 0f;

                    weight = ShapeFalloff(Mathf.Clamp01(weight), ordered[i].falloff) * (1f - claimed);
                    claimed += weight;
                    mask[i] = weight;
                }

                if (claimed > .001f) affected++;
                masks.Add(mask);
            }
        }

        /// <summary>Writes the mask into UV3 of a mesh that is about to be saved.</summary>
        private void ApplySectionMask(Mesh mesh, VATPartBake part)
        {
            List<Vector4> masks = BuildSectionMasks(part, out int affected);

            if (masks.Count != mesh.vertexCount)
            {
                Debug.LogWarning($"[VAT] Section mask skipped on '{part.Name}': " +
                                 $"{masks.Count} weights for {mesh.vertexCount} vertices.");
                return;
            }

            mesh.SetUVs(3, masks);

            // Rebuilt once per LOD level as well as for the mesh itself, and saying so each time would
            // bury everything else the bake reports.
            bool first = part.SectionMasks == null;
            part.SectionMasks = masks.ToArray();

            if (first)
                Debug.Log($"[VAT] Section mask on '{part.Name}': {affected}/{masks.Count} vertices affected");
        }

        /// <summary>What goes into the clip set so scripts can address sections by name.</summary>
        private VATSection[] BuildSectionRecords()
        {
            if (!SectionsActive) return new VATSection[0];

            List<VATSectionSetup> ordered = OrderedSections();
            int count = Mathf.Min(ordered.Count, MAX_SECTIONS);
            VATSection[] records = new VATSection[count];

            for (int i = 0; i < count; i++)
                records[i] = new VATSection
                {
                    name = ordered[i].name,
                    channel = i,
                    pivotBone = ordered[i].boneName,
                    restPivot = _sectionRestPivots[i],
                    priority = ordered[i].priority,
                    maxAngle = ordered[i].maxAngle
                };

            return records;
        }

        private static List<VATSectionSetup> CloneSections(List<VATSectionSetup> source)
        {
            List<VATSectionSetup> copy = new List<VATSectionSetup>();
            if (source == null) return copy;

            foreach (VATSectionSetup section in source)
                copy.Add(new VATSectionSetup
                {
                    name = section.name,
                    boneName = section.boneName,
                    priority = section.priority,
                    falloff = section.falloff,
                    pivotOffset = section.pivotOffset,
                    maxAngle = section.maxAngle
                });

            return copy;
        }

        private Mesh SaveMesh(Mesh mesh, string baseName)
        {
            string path = $"{_outputPath}/{baseName}_Mesh.asset";
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);

            if (existing && !_updateExisting) path = AssetDatabase.GenerateUniqueAssetPath(path);

            // BuildCombinedMesh names it after the bake, not after the file it lands in, which is a
            // mismatch on its own and is also what CopySerialized would stamp onto the overwritten asset.
            mesh.name = Path.GetFileNameWithoutExtension(path);

            if (existing && _updateExisting)
            {
                // Overwrite the contents so the asset keeps its GUID and existing references hold.
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(mesh);
                Debug.Log($"[VAT] Updated mesh {path}");
                return existing;
            }

            AssetDatabase.CreateAsset(mesh, path);
            Debug.Log($"[VAT] Created mesh {path}");
            return mesh;
        }

        /// <summary>
        /// One name per submesh, in the same order BuildCombinedMesh emits them, taken from the source
        /// renderer's own materials so the generated assets are recognisable.
        /// </summary>
        /// <param name="targets">Renderers making up one part, in frame-loop order.</param>
        /// <param name="names">Filled with one unique name per submesh, cleared first.</param>
        private static void CollectSlotNames(List<SkinnedMeshRenderer> targets, List<string> names)
        {
            names.Clear();
            HashSet<string> used = new HashSet<string>();

            foreach (SkinnedMeshRenderer target in targets)
            {
                Material[] sourceMaterials = target.sharedMaterials;
                int subMeshCount = target.sharedMesh ? Mathf.Max(1, target.sharedMesh.subMeshCount) : 1;

                for (int i = 0; i < subMeshCount; i++)
                {
                    string raw = i < sourceMaterials.Length && sourceMaterials[i]
                        ? sourceMaterials[i].name
                        : target.name;

                    string candidate = Sanitize(raw);
                    string unique = candidate;
                    int suffix = 1;
                    while (!used.Add(unique))
                        unique = $"{candidate}_{suffix++}";

                    names.Add(unique);
                }
            }
        }

        private Vector3 RootOffset(GameObject instance, Transform rootTransform, Vector3 rootReference)
        {
            if (!_removeRootMotion || !rootTransform) return Vector3.zero;

            Vector3 delta = ToBakeSpace(instance.transform, rootTransform.position) - rootReference;
            return new Vector3(_lockRootX ? delta.x : 0f,
                               _lockRootY ? delta.y : 0f,
                               _lockRootZ ? delta.z : 0f);
        }

        /// <summary>
        /// True when the last frame of the range poses identically to the first, which is what a
        /// seamlessly looping clip does. Compared after root-motion removal, so a walk cycle that
        /// travels still registers as a loop when Bake In Place is on, and correctly does not when it
        /// is off, because then the travel really is part of the baked result.
        /// </summary>
        private bool LoopFrameIsDuplicate(GameObject instance, List<VATPartBake> parts, AnimationClip clip,
                                          int startFrame, int frameCount, int frameStep,
                                          Transform rootTransform, Vector3 rootReference)
        {
            List<Vector3> first = new List<Vector3>();
            List<Vector3> last = new List<Vector3>();
            Mesh scratch = new Mesh();

            try
            {
                CapturePose(instance, parts, clip, startFrame / clip.frameRate,
                    rootTransform, rootReference, scratch, first);
                CapturePose(instance, parts, clip, (startFrame + (frameCount - 1) * frameStep) / clip.frameRate,
                    rootTransform, rootReference, scratch, last);
            }
            finally
            {
                Object.DestroyImmediate(scratch);
            }

            return PosesMatch(first, last, PoseEpsilon(first));
        }

        private void CapturePose(GameObject instance, List<VATPartBake> parts, AnimationClip clip, float time,
                                 Transform rootTransform, Vector3 rootReference, Mesh scratch, List<Vector3> output)
        {
            output.Clear();
            clip.SampleAnimation(instance, time);
            Vector3 rootOffset = RootOffset(instance, rootTransform, rootReference);

            foreach (VATPartBake part in parts)
            {
                foreach (SkinnedMeshRenderer target in part.Targets)
                {
                    Matrix4x4 toRoot = RigidMatrix(instance.transform).inverse * RigidMatrix(target.transform);
                    target.BakeMesh(scratch, false);

                    Vector3[] verts = scratch.vertices;
                    for (int v = 0; v < verts.Length; v++)
                        output.Add(toRoot.MultiplyPoint3x4(verts[v]) - rootOffset);
                }
            }
        }

        /// <summary>Scaled to the model so the test behaves the same in metres or centimetres.</summary>
        private static float PoseEpsilon(List<Vector3> pose)
        {
            float extent = 0f;
            for (int i = 0; i < pose.Count; i++)
                extent = Mathf.Max(extent, pose[i].magnitude);

            return Mathf.Max(1e-5f, extent * 1e-4f);
        }

        private static bool PosesMatch(List<Vector3> a, List<Vector3> b, float epsilon)
        {
            if (a.Count == 0 || a.Count != b.Count) return false;

            float squared = epsilon * epsilon;
            for (int i = 0; i < a.Count; i++)
                if ((a[i] - b[i]).sqrMagnitude > squared) return false;

            return true;
        }

        /*
         * Transform.InverseTransformPoint is NOT equivalent - it also divides by the transform scale,
         * so on a typical FBX import scaled to 0.01 it inflates the root offset 100x
         * and throws the baked mesh far off into the distance.
         */
        /// <summary>
        /// Expresses a world point in the space BakeMesh writes vertices into, which is the renderer's
        /// local space with the transform's scale left out.
        /// </summary>
        private static Vector3 ToBakeSpace(Transform space, Vector3 worldPoint)
        {
            return Quaternion.Inverse(space.rotation) * (worldPoint - space.position);
        }

        /// <summary>Rotation and translation only, deliberately dropping scale. See ToBakeSpace.</summary>
        private static Matrix4x4 RigidMatrix(Transform t) => Matrix4x4.TRS(t.position, t.rotation, Vector3.one);

        private static string[] BuildRootOptions(SkinnedMeshRenderer renderer)
        {
            List<string> names = new List<string> { "Object Root" };
            if (renderer.bones != null)
                names.AddRange(renderer.bones.Select(b => b ? b.name : "<missing>"));

            return names.ToArray();
        }

        private static int DetectRootIndex(SkinnedMeshRenderer renderer)
        {
            if (!renderer || !renderer.rootBone || renderer.bones == null) return 0;

            int i = System.Array.IndexOf(renderer.bones, renderer.rootBone);
            return i >= 0 ? i + 1 : 0;
        }

        private static Transform ResolveRoot(GameObject instance, SkinnedMeshRenderer renderer, int index)
        {
            if (index <= 0) return instance.transform;

            Transform[] bones = renderer.bones;
            if (bones == null || index - 1 >= bones.Length) return instance.transform;

            return bones[index - 1] ? bones[index - 1] : instance.transform;
        }

        /*
         * Path.GetInvalidFileNameChars() only reports '/' and '\0' on Linux and macOS,
         * so a Blender-style clip name like "Armature|Activate" would produce a file
         * that cannot be checked out on Windows. Whitelist instead.
         */
        /// <summary>
        /// Reduces a name to characters that are safe in a file name on every platform.
        /// </summary>
        private static string Sanitize(string name)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_');

            return sb.ToString();
        }

    }
}
