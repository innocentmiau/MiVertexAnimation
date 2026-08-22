# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
