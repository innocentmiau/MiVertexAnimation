# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.0]

### Added

- **A name of your own for every baked clip.** Each clip in the baker's list draws its name as a field now, and whatever is in it is what the slice is baked under and what `Play` matches. Clips arrive named by whoever exported the FBX, so gameplay code ends up writing `Play("Combat_Walk_1H_Attack")` when it means `Play("Attack")`, and the only way to shorten that before was to rename the clip on the FBX importer, which changes it for every other user of that file and cannot be done per bake at all. The rename is stored on the clip's range, keyed by clip reference like everything else there, so it survives a re-bake, an undo, and a settings asset saved and loaded again. Leaving the field on the clip's own name stores nothing rather than freezing that name, so renaming the clip on the importer afterwards still carries through, and the button beside the field is the same thing as typing the clip's name back in. Only the slice is renamed: the output files still take their names from File Name in the Output section, so a rename cannot orphan the assets a previous bake wrote.

- **The baked clip set has an inspector of its own**, which is where a name can be fixed after a bake without re-baking a texture to change a string. The slice names are editable, and the frames, rate and length beside them are shown but locked - they describe the texture that was written, and the default inspector let them be edited into disagreeing with it silently. Each slice's markers are still there to edit as they were. The next bake writes whatever the baker's clip list says, so a name changed only here comes back; the inspector says so.

- **A warning when two clips would bake under the same name**, in the baker before the bake and in the clip set after it. `Play` matches the first slice of a name, so the second one could only ever be reached by its index, and nothing said so. Two FBX files can each legitimately export an `Idle`, so this is a warning and not a refusal. It was on the roadmap.

### Changed

- Saving events into an existing clip set without re-baking resolves the slice by the name it was baked under rather than the source clip's name, and the bake log names each slice as it was baked, with the source clip beside it when the two differ.

## [1.4.1]

### Fixed

- `VATAnimatorDriver.Update` threw `ArgumentOutOfRangeException` out of `List.RemoveAt` whenever a one-shot finished into a clip that has no events on it, which is to say whenever an ordinary `PlayOnce("Attack", "Idle")` completed. Ticking an animator can change who wants to be ticked: a one-shot reaching its end raises `ClipFinished` and then starts its return clip from inside `Tick`, and starting a plain looping clip with nothing to watch unregisters the animator. So the driver's own list lost an entry midway through the pass that was walking it, and the pass then removed a second entry by an index that no longer meant anything, or ran off the end. Removal is deferred while a pass is running now: a slot being dropped is nulled instead of taken out, so every index means the same thing for the whole pass, and the list is closed up once afterwards. That covers anything else a listener does from inside a clip event or a `ClipFinished` handler, which was equally unsafe before and equally silent about it - freezing the animator, playing something else, pooling or destroying the entity it belongs to.

- The driver could keep hold of animators from a previous run when entering play mode with domain reloading turned off. It clears itself now.

### Changed

- Registering and unregistering with the driver are a subscript rather than a scan. Each animator remembers where the driver is holding it, so joining its list and dropping out again cost the same whatever else is playing. `Contains` on the way in and `Remove` on the way out were a pass over every animator with something to do, on every clip start and every clip finish, which is unnoticeable while a handful of one-shots are ever in flight at once and grows with the square of the crowd once thousands of bodies are all playing an attack. Removal outside a tick is a swap back, and the holes a tick leaves are closed in one sweep after it rather than one shift of the list per body.

## [1.4.0]

### Added

- `VATAnimator.Freeze()` and `Resume()`, which hold the pose that is on screen and then carry on from it - a hit stop, a pause menu, a character caught mid-stride. `Freeze(normalizedTime)` seeks to a point in the clip and holds there instead. `Resume` moves the clip's start time forward by however long it was held, so nothing jumps and no time is lost, and a freeze during a cross-fade stops both clips while letting the fade itself finish onto the frozen pose. A frozen animator drops off the driver, so a field of bodies costs per frame what a field of loopers costs, which is nothing.

- **Loop** on the `VATAnimator` inspector, on by default. Off plays the starting clip once and holds its last frame, for a prop or a corpse that spawns already in that state and never gets a `Play` call. `Play` still loops and `PlayOnce` still holds whatever it is set to - it describes the clip the component starts on, not the component. A clip that is not looping is never given a random start phase, which would have dropped it part way through the single run it gets.

- `VATAnimator.IsFrozen` and `NormalizedTime`, the second being where the current clip is as a fraction of one cycle.

- `VATAnimator.Speed`, and a **Speed** field on the inspector, multiplying playback for one instance across every clip.

- `VATAnimator.SetClipSpeed(clip, speed)` and `GetClipSpeed`, by index or by name, which give one clip a speed of its own whether or not it is the clip playing. This is the one for a run cycle that has to keep up with a movement speed: set it when the movement speed changes and idle, attack and death stay at 1, with nothing left running that has to check which clip is on screen. Setting the speed of a clip that is not playing touches no property block at all. `Play(clip, speed)` and `PlayOnce(clip, returnTo, speed)` reach the same thing from the call that starts the clip, and `CurrentSpeed` reports what the two layers multiply out to.

- A **once** button beside every clip in the inspector, a per-clip speed field beside it in play mode, and a **Freeze** / **Resume** button under the list, so all of it can be judged without writing a test script.

### Changed

- `_VATSpeed` moved out of the material constant buffer and into the per-instance buffer, so speed is per instance and a crowd sharing one material can hold a speed each without leaving the batch. It is still declared in both shaders' Properties, so a renderer nothing writes a property block for takes the material's value and behaves exactly as before; a renderer with a `VATAnimator` now has its speed written for it, and the material's **Playback Speed** is the fallback for one without. `VAT_Phase` in `VAT_Core.hlsl` takes the speed as an argument rather than reading the global.

  Writing `_VATSpeed` through a `MaterialPropertyBlock` was the only way to vary speed before this, and it had two costs that were not obvious: the property lived in `UnityPerMaterial`, so writing it per renderer gave that renderer its own material state and cost a draw call, and speed scales elapsed time, so every change jumped the clip to a different pose - a run cycle seven seconds old moves about 16% of the clip on a change from 1.0 to 1.8. `VATAnimator` moves the clip's start time by the same ratio in the same instant now, which cancels the jump exactly.

- The per-instance `_VATClamp` and `_VATPreviousClamp` are now `_VATHold` and `_VATPreviousHold`, and carry where playback stops as a fraction of the clip rather than a flag: `0` loops, `>= 1` stops on the last baked frame, anything between stops there. Zero still means looping, because that is what an instance nothing has written reads as, so a material driven by hand and never given the property behaves exactly as before. Nothing in a bake stores either name - they are written at runtime through a `MaterialPropertyBlock` - so no material, prefab or clip set needs re-baking. Only a driver of your own that wrote `_VATClamp` directly has to change, and `VAT_Phase` in `VAT_Core.hlsl` takes the fraction in place of the old flag.

### Fixed

- A clip asked to hold its last frame stood back up again when **Frame Blend** was on. Holding was expressed as a phase of `0.999999` of the clip, which is not the last frame but almost all of the way to it: `floor` picked frame N-1, `frac` came out just under 1, and frame blending wraps the frame after N-1 round to frame 0 - so a death animation held a pose that was 99.99% its *first* one. It lands on the last frame exactly now, leaving frame blending nothing to blend toward. This is why the roadmap still asked for a way to stop a clip at its end when `PlayOnce` looked like it already did it.

- Clip events and `ClipFinished` ignored playback speed. Both were worked out from the clip's baked length while the shader was running it at `_VATSpeed`, so anything not playing at 1 fired its markers at the wrong pose and ended its one-shots at the wrong moment. They read the same speed the shader does now.

- `VATAnimator` reported the wrong position for a one-shot that had already finished. `NormalizedTime` and the event bookkeeping decided between clamping and wrapping on whether a one-shot was still in flight, and a finished one is not, so asking where it was started answering with a cycle it had never begun.

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
