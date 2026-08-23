# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
