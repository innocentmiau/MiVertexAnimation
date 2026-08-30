# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.1]

### Added

- `VATTime`, the one clock everything here is timed against. It is whatever the render pipeline last put in `_Time.y`, which is `Time.time` in play mode and `Time.realtimeSinceStartup` in the editor. Anything that writes `_VATClipStart`, `_VATBlendStart` or a section's start time by hand - a driver of your own, or a shader built on `VAT_Core.hlsl` - has to stamp it with this, or it disagrees with the `_Time.y` it will be compared against.

### Fixed

- One-shots, cross-fades and section turns did not play at all in a build with more than one scene in it, and looping clips started from an arbitrary point. `VATAnimator` stamped its playback state with `Time.timeSinceLevelLoad`, and the shader compares those stamps against `_Time.y`, which URP fills from `Time.time`. Those two agree only in the first scene of a run, which is the scene anyone tests in the editor, and after that `timeSinceLevelLoad` resets to zero on every scene load while `_Time.y` carries on counting from application start. A build that opens on a menu and loads the game from there was handing the shader clip start times a whole menu's worth of seconds in the past. `PlayOnce` clamps rather than wraps, so a one-shot opened already holding its last frame and an attack never animated at all; a cross-fade arrived finished; and clip events fired against a pose that was not the one on screen. Looping clips survived it, because `frac()` turns a wrong start time into a wrong phase and nothing worse, which is what kept the whole thing out of sight until a build had a second scene in it. Both components read `VATTime.Now` now.

- `VATSectionDriver` used `Time.time`, which is the right clock in play mode and the wrong one in the editor, where `_Time.y` comes from `Time.realtimeSinceStartup` instead. `TurnTo`, `Release` and the inspector's own posing therefore snapped straight to the target outside play mode rather than easing into it, so a duration could not be judged without entering play. Same clock, same fix.

## [1.3.0]

### Added

- A **Demo** sample, importable from the Package Manager. A scene with a crowd, an orbiting target and a character that turns to follow it, a CC0 rigged character to bake it from (as a prefab with its controller already assigned, which is what the baker's clip list needs), and the four section-driving modes on one component. `VATDemoRig` takes any baked prefab in one field, so the same scene demonstrates your own character rather than the one in the box.
- `VATBakerWindow.ShowWith(settings, outputPath)`, which opens the baker already loaded with a settings asset. The demo sample uses it so its setup button lands on a window that is ready to bake, rather than on whatever was baked last; the preset itself is a `VATBakeSettings` asset in the sample, editable like any other.

### Changed

- Samples moved to `Samples~` and are declared in `package.json`, so they no longer compile in every project that installs the package. Import them from the Package Manager when you want them; the imported copy lives in `Assets/Samples` and is yours to edit.

### Fixed

- The section bone filter re-read every mesh's skin weights once per bone per renderer per repaint. `mesh.boneWeights` allocates a fresh array on each access and the cache held one mesh at a time, so spanning six renderers turned a single read into over a hundred. Skin weights and bone subtrees are both cached per renderer now, and section coverage is worked out for all four channels in one pass instead of rebuilding the same mask once per channel.

- A section on a multi-mesh character reported "moves no vertices on this mesh" while the preview highlight showed it working. The mask is built over every renderer the bake reads — Combined Mesh puts all of them in one part — but the warning, the vertex count, the bone dropdown and the bone a new section starts on all asked a single renderer, the one selected above. On a character split into six meshes, a head bone weights only the head mesh, so anything measured against the body reported nothing. All four now span the same renderers the bake does, and the bone list is the union across them rather than one renderer's array.

- Two clips with the same name no longer share one entry in the baker. Frame ranges and authored events were keyed by clip name, so an `Idle` from one FBX and an `Idle` from another looked like the same clip: editing one range edited both, opening the shorter one clamped the longer one's End Frame down to its length, markers placed on either appeared on both, and **Save Events** wrote one clip's markers over the other's slice in the baked clip set. Both are keyed by clip reference now, and the name is only a label. This also means renaming a clip keeps its range and its events instead of silently orphaning them. Settings assets written before this adopt their clips on load, so nothing needs re-authoring.

## [1.2.0]

### Added

- **Bake Rest Pose Mesh**, on by default. Writes a mesh holding the first baked frame instead of pointing the prefab at the imported one, so a shader that is still compiling or has failed draws the character standing still rather than the bind pose at whatever scale the source file used. The mesh is copied rather than rebuilt, so Unity 6 Mesh LOD levels, blend shapes and every vertex channel come across with it: Mesh LOD stores extra index buffers over the same vertex buffer, so SV_VertexID still addresses the same vertex at every level and a decimated VAT mesh animates correctly.
- **LOD Group** section, off by default. Pick which of the source mesh's own Mesh LOD levels to bake and when each takes over, and the prefab comes out as an LODGroup with a renderer per level. Every level keeps the full vertex buffer, so `SV_VertexID` still addresses the same texel and **one texture set serves them all** - the only thing that grows is a mesh asset per level. This is the way to get level of detail on a VAT crowd: Unity 6 Mesh LOD on its own does nothing here, because an instanced batch is one draw over one index range while Mesh LOD needs a different range per renderer, and instancing wins. Works in every renderer mode: Combined merges each level as it merges the mesh, and Separate Parts puts every part in each level, since an LOD holds a whole array of renderers. Each level can be put on the preview to see where its silhouette gives out, and each threshold shows the distance it lands at rather than a bare fraction of screen height.
- A check for **Maximum LOD Level** in the active quality settings. Set above the number of levels a group has, Unity draws nothing at any distance and says nothing about it - project wide, and identical on a hand-built LODGroup.
- `VATAnimator` and `VATSectionDriver` now drive every renderer beneath them rather than one on their own object, which is what lets them sit on an LODGroup root.
- **Find Unused Baked Meshes**, in Settings. A bake writes a mesh per LOD level, and dropping a level or turning the section off leaves the extras on disk. Lists what no prefab or scene refers to and deletes it only on confirmation - never as part of a bake.
- A bake-time warning when the imported mesh and the animation baked from it disagree on scale, which is what a rig with scaled bones looks like from the outside.

## [1.1.0]

### Added

- **Reset Bake**, in the bake settings panel. Keeps the selected object and puts clip selection, per-clip frame ranges, events, sections, texture and output settings back to their defaults,   after confirming. Nothing already written to disk is touched.

### Changed

- The bake settings asset moved out of its own section at the top of the window and onto the   **Source** row, behind a button that opens it only when it is wanted. The button is marked when   a settings asset is loaded.

## [1.0.0]

First release.

### Added

- Baker window with a two-pane layout, live preview, per-clip frame ranges and undo scoped to the panel.
- Position and normal storage precision, defaulting to 16-bit fixed point across the bake's own bounds
  and octahedral normals.
- Animation events, editable in the baker and carried onto the baked clip set.
- `VATEventReceiver` for wiring those events to UnityEvents without writing code.
- Mesh sections: a region of the baked mesh, taken from the rig's own skin weights, that a script can
  still turn or move afterwards.
- `VATSectionDriver` with GPU-timed transitions and CPU tracking.
- `VAT_Lit`, `VAT_Unlit` and `VAT_Minimal` shaders.
- Bake settings assets, so a bake can be reproduced or re-run later.
