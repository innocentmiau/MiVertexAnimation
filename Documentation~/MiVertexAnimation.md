# MiVertexAnimation - reference

Bake skinned animation to vertex textures for large crowds.

Turns a `SkinnedMeshRenderer` + `AnimationClip` into a Vertex Animation Texture, so crowds of identical animated entities render with no Animator, no CPU skinning and no bone Transforms - then plays them back at runtime with clip switching, cross-fading and animation events.

Self-contained: copy the whole `MiVertexAnimation` folder into any URP project. No models, scenes, textures or packages come with it.

```
MiVertexAnimation/
  Runtime/
    Clips/
      VATClipSet.cs          records which slice holds which clip
      VATClipEntry.cs        one clip's slice: name, frames, rate, events
      VATClipEvent.cs        a marker at a point inside a baked clip
    Playback/
      VATAnimator.cs         runtime clip selection and cross-fading
      VATAnimatorDriver.cs   ticks only the animators that need it
    Rendering/
      VATBoundsOverride.cs   fixes culling bounds on baked objects
    Events/
      VATEventReceiver.cs    raises UnityEvents at markers, no code needed
      VATEventBinding.cs     one marker name wired to one UnityEvent
  Editor/
    Baking/
      VATBakerWindow.cs      the baker window
      VATBakeSettings.cs     saved bake setup, editor-only
      VATRendererMode.cs     how a source object's renderers are grouped
      VATClipRange.cs        one clip's frame range and step
      VATFrameQuality.cs     how much error Auto Frame Step may introduce
      VATBakerState.cs       one entry on the window's undo stack
      VATPartBake.cs         one output set during a bake
      VATClipBake.cs         one clip's slice as the bake actually wrote it
      VATPreviewPart.cs      a stand-in mesh for one renderer in the preview
    UI/
      VATAnimatorEditor.cs   clip-name dropdown and play buttons
      VATClipPickerPopup.cs  the searchable list behind "+ Add"
      VATEventReceiverEditor.cs      the receiver's inspector
      VATBakeSettingsPickerPopup.cs  the list behind "Load"
      VATUi.cs               section boxes, tinted buttons, icon scope
      VATIcons.cs            cached lookups of Unity's own icons
      VATUiSettings.cs       icons, colours and preview height, in EditorPrefs
  Shaders/
    VAT_Core.hlsl            the sampling maths, shared by every pass
    VAT_Lit.shader           URP lit shader (forward, shadow, depth, depthnormals,
                             scene selection, picking)
    VAT_Unlit.shader         the same six passes with no lighting
    VAT_Minimal.shader       one pass, no includes, written to be read
```

One type per file, grouped into domain folders. `Runtime/` and `Editor/` are the assembly roots, so an `.asmdef` dropped in either one covers everything beneath it.

## The window

Sections are boxed and headed with Unity's own icons, and buttons are tinted by what they do: blue for the bake, green for anything that only looks or opens, red for anything that throws work away. Both icons and colours can be turned off in the window's own menu (the three dots in its tab), which also resets the preview height. Those live in `EditorPrefs`, so they follow you between projects rather than being committed with a bake.

Once the window is wide enough, the settings sit on the left and the preview and events on the right, with a drag bar between them. Each side scrolls on its own, so reading down the output settings does not drag the preview off the top. Below about 650 pixels wide there is no room for two columns and everything stacks into one, as before. The layout and the split position are both in the window menu, and `Preview Beside Settings` turns the split off if you would rather always stack.

**Ctrl+Z** undoes the last change made in the baker, and **Ctrl+Shift+Z** or **Ctrl+Y** redoes it. The baker keeps its own undo stack rather than using Unity's, so this walks back baker edits only and never reaches past them into a scene or asset change. It is bound to the window's shortcut context, so it only fires while the baker has focus and can be rebound in Edit > Shortcuts. A slider drag is one step, and so is a run of typing. Undo and redo are also in the window menu.

The **preview** is resized by dragging the grip underneath it. **Browse...** next to the output folder opens the system folder picker; anything outside the project's `Assets` folder is refused, since `AssetDatabase` cannot write a bake there.

## Adding VAT to a shader you already have

**Create Material** in the Output section has a **Shader** field beside it. Anything that reads a VAT works there, including your own. The baker warns before you bake if the shader you picked is missing something it needs.

The smallest set a shader has to declare:

```
[NoScaleOffset] _VATPositionTex("Position Array", 2DArray) = "" {}
_VATTextureWidth("Texture Width", Float) = 1
_VATTextureHeight("Slice Height", Float) = 1
_VATRowsPerFrame("Rows Per Frame", Float) = 1
[HideInInspector] _VATClipData0("Clip Data 0", Vector) = (1,24,1,24)
```

Everything else the baker writes is optional - setting a property a shader does not have is ignored.

Then, in the shader itself:

```hlsl
#include "path/to/VAT_Core.hlsl"

struct Attributes
{
    float3 normalOS : NORMAL;
    uint   vertexID : SV_VertexID;   // this is what picks the vertex out of the texture
    // ...
};

// in the vertex shader, instead of using the mesh's own position:
float3 positionOS, normalOS;
VAT_Sample(input.vertexID, input.normalOS, positionOS, normalOS);
```

`VAT_Core.hlsl` brings its own `UnityPerMaterial` buffer, so declare the rest of your material's
properties there rather than in a buffer of your own.

**Read `VAT_Minimal.shader` first.** It is one pass, includes nothing, and spells the arithmetic out in
about a dozen lines, so you can see what is actually happening before wiring it into anything.

Three things that catch people out:

- **Every pass needs it.** Do it only in the visible pass and your shadows, depth and depth-normals
  stay on the bind pose, so the mesh detaches from its own shadow and reads wrong to SSAO.
- **The textures must stay point-filtered, uncompressed and mip-free.** Filtering reads neighbouring
  vertices and shreds the mesh. The baker sets this; don't override it on the asset.
- **Turn GPU instancing on** for the material, or per-instance clip selection has nowhere to travel.

## Requirements

Unity 6 with URP. On Unity 6.0 and earlier, change `_CLUSTER_LIGHT_LOOP` to
`_FORWARD_PLUS` in `VAT_Lit.shader`.

## Use

1. **Tools > MiVertexAnimation > Baker**
2. Drop in a prefab with a SkinnedMeshRenderer and an Animator.
3. Pick a clip, set the frame range and frame step.
4. Set the output folder and press **Bake VAT**.

The preview at the bottom shows the source rig stepping through **exactly the frames that will
be baked** - it uses the same frame arithmetic as the shader and applies Bake In Place - so
raising Frame Step or locking a root axis shows you the real result before you commit. Drag to
orbit, scroll to zoom.

You get the textures, a configured material and a ready prefab. Drop the prefab in a
scene and it animates. The five layout values that have to match the bake
(`Total Frames`, `Frame Rate`, `Texture Width/Height`, `Rows Per Frame`) are filled in
for you - getting any of them wrong scrambles the mesh, so don't hand-edit them.

## How the data is laid out

Vertex `N` of frame `F` lives at:

```
x = N % textureWidth
y = floor(N / textureWidth) + rowsPerFrame * F
```

Each frame occupies a contiguous block of `rowsPerFrame` rows, so there is no vertex
count limit. The shader addresses it with `SV_VertexID`, which means **the bake is tied
to one exact vertex buffer** - anything that changes vertex count or order invalidates it.

Positions are stored as raw object-space floats (`RGBAHalf`), which is why the textures
must import uncompressed, point-filtered, clamped and without mips. The baker sets all
of that automatically.

## Looping clips

A seamlessly looping clip ends on the pose it started on, so baking the full range stores that
pose twice. The shader spreads `TotalFrames` evenly over the loop, so the extra frame makes the
animation run slightly slow and stutter once per cycle.

**Trim Looping Duplicate** (on by default) drops it - but only after checking that the last frame
really does match the first, so non-looping clips keep every frame. The comparison runs after
root-motion removal, so a walk cycle that travels still registers as a loop when Bake In Place is
on, and correctly does not when it is off.

Interior duplicates are **reported, not removed**. Because frames are spread evenly over the loop,
deleting one deletes its duration too: a three-frame hold would collapse to one and everything
after it would shift earlier. The bake log counts them instead, and when they are evenly spaced it
tells you which Frame Step would be lossless:

```
30 of 61 frames are identical to the frame before - evenly spaced, so Frame Step 2 would
be lossless. Left in place: removing them would retime the animation.
```

That usually means the clip was authored at a lower rate than it was exported at.

## Root motion

The baker poses the rig with `AnimationClip.SampleAnimation`, which reads the clip's curves
directly and therefore **ignores the Animator's "Apply Root Motion" setting**. Any travel in
the clip lands in the vertex positions, so the mesh drifts and then snaps back when the loop
restarts.

**Bake In Place** subtracts it. Each frame it measures the chosen root transform and removes
its displacement from every vertex on the locked axes.

The anchor is the rig's **authored rest position**, read before any frame is sampled - not the
first baked frame. That way Start Frame only trims the loop; it never slides the result off the
pivot. Anchoring to the first baked frame would keep whatever displacement that frame already
carries, so starting at frame 5 of a walk would leave the character standing where frame 5 put it.

- **Root Transform** - the transform carrying the travel. Defaults to the SkinnedMeshRenderer's
  root bone. A rig with a dedicated root bone gives the cleanest result; if the translation
  lives on the hips instead, locking an axis also flattens hip sway on it.
- **Lock Axes** - X and Z by default. Leave Y free so jumps and crouches survive.

Only translation is removed. If a clip also *turns* the root, that rotation stays baked in.

## Pivot

`BakeMesh` returns vertices relative to the **SkinnedMeshRenderer's** transform, which on most
rigs sits at the armature origin rather than where the prefab pivots. The baker rebases them
into the **source prefab root's** space, so the generated prefab has the same pivot, orientation
and scale as the object you selected - drop-in interchangeable with the original.

Both rebasing and root-motion removal use rotation and translation only, never scale, because
`BakeMesh(mesh, useScale: false)` already leaves the transform's scale out of the vertices.
Using `Transform.InverseTransformPoint` here divides by that scale and throws the mesh far off
the origin on any FBX imported at a scale factor.

## Multiple clips and runtime switching

When the object has more than one clip, the window shows **"N/M animations selected to bake"**
with an **+ Add** button. Add opens a searchable list of the clips you have not picked yet; click
one and it joins the list below, with a **-** to drop it again. Only the clips you actually chose
are on screen, in bake order, each labelled with the **slice index** it will get.

That index is what the shader plays, and it is the position in this list - not the row number in
the Animator Controller. Add clips in whatever order you want them numbered.

Each becomes one slice of a `Texture2DArray`, so **one material can play any of them**. Slices
share a size, so shorter clips pad up to the longest. With several clips selected each bakes its
full range, and the Start/End Frame sliders only appear for a single clip.

### Per-clip ranges

Every slice of a texture array is the same size, so the shortest clips pad up to the longest one. Bake
a 40-frame idle beside an 11-frame run and each of them costs 40 frames of texture. The size box in the
Texture section says how much of the result is padding.

**Per-Clip Ranges**, in the Animation section, gives each clip its own Start Frame, End Frame and Frame
Step. A bar appears listing the clips in the bake; pick one and the sliders below edit that clip, and
the preview follows it. Nothing moves and nothing is duplicated - it is the same three controls in the
same place, pointed at whichever clip you chose.

That is worth more than it sounds. Stepping the idle by 3 while the attack stays at 1 shrinks the idle
*and* shortens the clip that everything else is padding up to, so the saving lands twice.

The shader needed no change for this: `_VATClipData` already stores frames and rate per clip, so every
slice can run at its own length and its own speed.

With the toggle off, every clip bakes in full at the shared Frame Step, exactly as before. Turning it
off does not discard the per-clip numbers - they are kept, and come back if you turn it on again.

### Auto Frame Step

**Auto Frame Step** sits under Frame Step in the Animation section, because Frame Step is what it
writes. Pick a quality beside it and press **Measure**: it measures each clip and writes in the
coarsest Frame Step that clip survives.

It measures rather than guesses. For each candidate step it rebuilds every source frame the way the
shader would - a lerp between the two kept frames either side with Frame Blend on, a held frame with
it off - and compares that against the real pose. The coarsest step whose worst vertex error stays
inside the tolerance wins. The tolerance is a fraction of the model's own size, so the same number
behaves the same whether the rig is authored in metres or centimetres.

This is why per-clip steps matter: one step for a whole bake is decided by its fastest clip, so an
attack that needs every frame drags a 40-frame idle along with it. Measured separately, the idle is
usually stepped by three or four while the attack stays at one.

The answers go into the per-clip ranges rather than straight into the bake, so the size box and the
preview both show what it did before anything is written, and any clip you disagree with can be
changed by hand. It costs a pass over every frame of every clip, which is why it is a button.

The quality dropdown is that button's argument and nothing else - it changes nothing on its own, and
it does not touch anything in the Texture section. **Precise**, **Balanced** and **Aggressive** are
0.05%, 0.2% and 1% of the model; **Custom** reveals the tolerance directly. Not pressing the button at
all is what lossless means, so there is no option for it.

### Which slice is which clip

The baker writes a **`<name>_Clips`** asset next to the material listing every slice with its
name, frame count, rate and length. A material cannot store strings, so without it the mapping
from "slice 3" back to "Attack" would live nowhere.

The generated prefab's `VATAnimator` points at it, which is what makes the inspector show a
**dropdown of clip names** instead of a number, and lets you play clips by name:

```csharp
animator.Play("Attack");
```

In play mode the inspector also gives you a button per clip, so transitions can be tried without
writing anything.

### One-shot clips and events

Looping is the default. For an attack or a death you want the clip to run once:

```csharp
animator.PlayOnce("Attack", returnTo: "Idle");   // fades back when it ends
animator.PlayOnce("Die");                        // holds the last pose
```

A one-shot **holds its final frame** rather than wrapping, so nothing restarts underneath the fade
back. "Holding a pose" below covers that and the other way a clip stops, which is freezing it.

Two events to hook:

```csharp
animator.ClipFinished  += (a, clipName) => { /* attack over, resume AI */ };
animator.ClipEventFired += (a, e) => { if (e.name == "Hit") DealDamage(); };
```

`ClipFinished` fires when a one-shot reaches its end. `ClipEventFired` fires at markers **inside**
the clip - which is where damage usually lands, not on the last frame.

Those markers come from the source clip's own **animation events**: the baker copies them into the
Clip Set, remapped into the baked range. VAT has no Animator, so events would otherwise be lost.

### Editing events in the baker

The **Events** section under the preview edits the markers on whichever clip the preview is showing.

Pause the preview or drag the frame slider to park on a frame, then **+ Add at frame N**. Markers
appear on the track above the list and can be dragged along it, snapping to baked frames. Clicking
empty track pauses and scrubs there. Each row gives the event's name, its frame, and - behind
**params** - the string, float and int parameters that reach your listener on `VATClipEvent`.

**Save Events to \<name\>_Clips** writes straight into the existing Clip Set, so retuning a hit frame
costs nothing. Re-baking carries the edits too.

Which events a bake ends up with, in order:

1. A list edited here **overrides** the source clip. **Reset to source** drops the override.
2. Otherwise the source clip's own events are imported.
3. Failing both, a clip with no source events keeps whatever the Clip Set already held, so markers
   written directly onto the asset survive a re-bake.

Times are stored as a fraction of the clip, not as frames. Changing **Frame Step** keeps every marker
on the same moment; changing **Start Frame** slides the animation underneath them, and the window
warns when that has happened.

### Wiring events without code

`ClipEventFired` is a C# event, so using it means writing a script. **VAT Event Receiver** is a
component that does that for you: drop it on the baked prefab, point it at the `VATAnimator`, and it
lists every marker name in the clip set with a `UnityEvent` beside it. Drag your enemy script in and
pick the method, the same as any button.

**Clip Finished** is added a clip at a time with its own **+ Add**, and raised when a clip played with
`PlayOnce` reaches its end. Every clip has an end, so listing them all would open a ten-clip rig with
ten UnityEvents nobody asked for; markers are filled in automatically because there are only ever as
many as somebody deliberately placed.

Nothing is passed to the response. A `UnityEvent` can carry one argument, and only of a type fixed
when the class is written, while a marker carries three - so anything needing the string, float or int
parameters should subscribe to `ClipEventFired` directly and get all of them.

Bindings whose name is no longer in the clip set are kept and marked in the inspector rather than
deleted, so a re-bake cannot silently unwire something.

### Per-frame cost

`VATAnimator` has no `Update`. A shared driver ticks **only** the instances that need it - a
one-shot in flight, or a clip with events. A settled crowd of loopers costs zero CPU per frame,
which is the whole point of VAT.

### Switching at runtime

A vertex shader has no memory between frames, so it cannot notice that a clip index changed. The
generated prefab carries a **`VATAnimator`** component that tells it:

```csharp
var animator = enemy.GetComponent<VATAnimator>();
animator.Play(2);          // cross-fade into slice 2, starting at its first frame
animator.Clip = 2;         // same thing
animator.Snap(0);          // switch with no cross-fade
animator.Play(3, true);    // restart even if slice 3 is already playing
```

`Play` hands the current clip and its start time to the outgoing slot, then stamps the moment the
switch began. The shader fades over **Clip Blend Duration** (a material property, set at bake time
and editable afterwards) and samples the outgoing clip only while the fade is running - the branch
is uniform, so a settled instance pays nothing extra.

### Holding a pose

Two ways a clip stops instead of coming round again.

**On its last frame**, which is what a death needs - the body stays on the ground and no separate
one-frame clip has to be baked for it:

```csharp
animator.PlayOnce("Die");            // no returnTo, so it stays on the last frame
```

Turn **Loop** off on the component to do the same to whatever clip it starts on, for a prop or a
corpse that spawns already in that state. `Play` still loops and `PlayOnce` still holds whatever
the toggle says - it describes the starting clip, not the component.

**On the pose that is on screen**, for a hit stop, a pause menu, or anything that should carry on
afterwards from where it was:

```csharp
animator.Freeze();                   // holds whatever is showing
animator.Resume();                   // carries on from it

animator.Freeze(0.5f);               // seek to the middle of the clip and hold there
```

`Freeze` reads where the clip is from the **Clip Set**, so an animator without one holds its first
frame rather than the pose on screen. `Resume` moves the clip's start time forward by however long
it was held, so nothing jumps and no time is lost. `Play`, `PlayOnce` and `Snap` all clear a freeze,
and freezing during a cross-fade stops both clips - the fade itself still finishes, onto the frozen
pose.

Either way the animator **drops off the driver** while it is held, so a field of bodies costs the
same per frame as a field of loopers, which is nothing.

`IsFrozen` and `NormalizedTime` report the state, the second being where the clip is as a fraction
of one cycle.

Underneath, both are one per-instance value, `_VATHold`, which is where playback stops as a fraction
of the clip: `0` loops, `>= 1` stops on the last baked frame, and anything between stops there. Zero
has to mean looping, because that is what an instance nothing has written reads as. A shader built on
`VAT_Core.hlsl` gets this from `VAT_Phase` and needs no code of its own.

### Speed

Speed is per instance, so one crowd on one material can have a character sprinting beside one that
walks, in the same batch. Two layers multiply:

```csharp
animator.Speed = 1.5f;                  // this instance, every clip
animator.SetClipSpeed("Run", 1.3f);     // this clip, whether or not it is playing now
animator.Play("Run", 1.3f);             // same thing, from the call that starts it
animator.PlayOnce("Die", null, 0.5f);   // a slow-motion death
```

`SetClipSpeed` is the one for a run cycle that has to keep up with a movement speed. Set it when the
movement speed changes and forget it: it applies to that clip alone, so idle, attack and death stay
at 1 and **nothing has to check which clip is on screen**. Setting the speed of a clip that is not
playing costs nothing at all - no property block is touched until that clip comes round.

Both are multipliers on the rate baked into the clip, and both are clamped to a small positive
minimum. **Speed 0 does not stop playback** - `Freeze` does that.

Changing speed **keeps the clip where it is**. Position in a clip is elapsed time times speed, so
raising the speed would otherwise scale everything already elapsed and jump the character to a pose
it was never heading for - a run cycle 7 seconds old jumps about 16% of the clip on a 1.0 to 1.8
change. The animator moves the clip's start time by the same ratio in the same instant, which
cancels it exactly.

Clip events and `ClipFinished` are read off the same speed, so a clip at half rate fires its hit
marker where it looks like it should rather than twice as early.

> Driving `_VATSpeed` through a `MaterialPropertyBlock` yourself does none of this. It is what the
> animator writes, so it will be overwritten; before 1.4.0 it also sat in the material's constant
> buffer, where writing it per renderer broke the instanced batch and cost a draw call each.

### Per-instance state

`VATAnimator` writes through a `MaterialPropertyBlock`, so **every instance can play a different
clip from one shared material**. That routes drawing through GPU instancing rather than the SRP
Batcher, which is the right trade for a crowd; the baker enables GPU Instancing on the materials
it creates.

Set **Phase Variation** to 0 when using `VATAnimator` - it staggers instances by randomising each
one's clip start time instead, which is controllable and survives clip switches.

### The clock

Every timestamp this package writes into a material is compared against `_Time.y` inside the shader,
so both ends have to be read off the same clock. URP fills `_Time` from
`Application.isPlaying ? Time.time : Time.realtimeSinceStartup`, and `VATTime.Now` mirrors that:

```csharp
float now = VATTime.Now;   // the value _Time.y will be holding when this frame draws
```

`Time.timeSinceLevelLoad` is the one that looks right and is not. It agrees with `Time.time` only in
the first scene of a run, and resets to zero on every scene load after that while `_Time.y` carries
on counting from application start. Stamp a clip start with it and a build that opens on a menu
plays the game scene with every start time a whole menu's worth of seconds in the past: one-shots
hold their last frame from the moment they begin, cross-fades arrive finished, and section turns
snap.

None of this matters unless you are writing the state yourself. `VATAnimator` and `VATSectionDriver`
already use it. It matters if you drive `_VATClipStart`, `_VATBlendStart` or a section's
`FromOff.w` from your own code, or if you write a shader on top of `VAT_Core.hlsl` that compares
anything to `_Time.y`.

## Objects with several SkinnedMeshRenderers

When the window finds more than one SkinnedMeshRenderer it offers three modes.

### Selected only

Bakes one renderer. Repeat per renderer if you want them all. Use this for an **LODGroup**,
where the renderers are alternative levels of one object rather than parts of it - and bake
every level with identical Start/End Frame, Frame Step and clip, or the levels pop at LOD
transitions.

### Separate parts, one prefab

Bakes every renderer to **its own texture pair and material**, then assembles them into one
prefab with a child per part. Usually the right choice for a character split into body / clothes
/ gear:

- each part keeps its own base map and material,
- the source meshes are referenced directly, so Unity 6 **Mesh LOD levels survive**,
- all parts are sampled from the same frames in a single pass, so they cannot drift out of sync,
- one prefab, one transform, one pivot.

Costs the same draw calls as the parts had before - that is inherent in them having different
materials.

### Combined into one mesh

Concatenates every renderer into one vertex buffer and bakes a single texture pair and material,
writing a combined mesh asset alongside. Cheapest in memory, but:

- the generated mesh is new and does **not** carry the source meshes' Mesh LOD levels,
- source submeshes are preserved, so parts with different materials are still separate draw calls
  anyway - you only collapse to one draw call if they share a base map or atlas.

Never combine an LODGroup - it would stack all levels on top of each other.

## Generated materials

Every submesh gets its own material, named after the source material it came from, so a body +
cloak character bakes to `Name_BodyMat.mat` and `Name_CloakMat.mat` rather than one shared
material you then have to split by hand. All of them read the same VAT textures - the submeshes
index one shared vertex buffer - and differ only in their surface settings.

A single-submesh part keeps the plain `Name.mat` filename.

On a re-bake with **Update Existing** on, every one of those materials is rewritten in place, so
base maps and other surface tweaks survive across all of them.

## Reproducing a bake

Every bake writes a **`<name>_BakeSettings`** asset alongside the outputs (toggle in Output,
on by default). It captures everything the window cannot recover from the generated assets -
clip list and order, frame range, frame step, root axes, renderer mode, texture width, output
paths - so you can come back to a character and adjust one thing instead of rebuilding the
setup from memory.

Three ways back to it:

- **Press Bake Settings**, beside the object field, and drop a settings asset on the **Loaded** field or pick one with **Load...**. The same panel has **Reset Bake**, which keeps the object and puts everything else back to a fresh bake.
- **Load** opens a picker filtered to settings assets only.
- **Assign the source prefab** and, if a settings asset was baked from it, the window offers to
  load it.

Settings are editor-only by construction - the class lives in the editor assembly, so it can
never be dragged into a build the way a runtime ScriptableObject referenced by a prefab could.

It stores object references rather than paths, so renaming clips or the prefab does not break it.
A scene object cannot be referenced from an asset, so bake from a prefab if you want the settings
to be reusable - the window warns when it cannot store the target.

## Re-baking

Texture arrays always overwrite in place, so their GUIDs never change. When a material or prefab of
the same name already exists, an **Update Existing** option appears:

- **on** (default) - the material and prefab are rewritten in place, keeping their GUIDs, so
  everything in your scenes picks up the new bake. On the material only the VAT properties are
  touched; base map, colour, smoothness and metallic are left alone. On the prefab, colliders,
  scripts and any other components you added survive.
- **off** - numbered copies are made instead, and nothing already placed in a scene will use them.

## Surface options

**Render Face** picks the cull mode, and **Alpha Clip** with **Alpha Cutoff** does cut-outs. Both are
applied in every pass, so a hole in the mesh is a hole in its shadow and in the depth prepass too -
without that, a cut-out cape casts the shadow of a solid rectangle and reads as solid to SSAO.

Alpha clipping is behind a shader feature, so a material that does not use it never compiles the extra
texture fetch into the shadow and depth passes.

## Material settings worth knowing

| Property | What it does |
|---|---|
| `Phase Variation` | 0 = every instance in lockstep. Raise it to de-sync a crowd. Derived from world position, so it costs nothing and keeps the SRP Batcher working. |
| `Playback Speed` | Multiplier on the loop rate, for renderers with no `VATAnimator`. One that has an animator writes its own, and the **Speed** field on the component is the one to reach for. |
| `Frame Blend` | Interpolates between frames. Lets you bake with a high frame step and still look smooth - at 4 texture fetches per vertex instead of 2. |

## LOD

- **Unity 6 Mesh LOD** (Model Importer, one renderer, `Level of Detail: N levels` on the
  mesh): all levels share one vertex buffer, so a single bake covers every level. Nothing
  extra to do.
- **Classic LODGroup** (several renderers, one mesh each): each level is a separate vertex
  buffer and needs its own bake. The window shows a renderer picker when it finds more than
  one. Keep the frame range, frame step and frame rate identical across levels or the
  animation will pop at LOD transitions.

Mesh LOD and LODGroup only apply to real MeshRenderers. `Graphics.RenderMeshInstanced`
does no LOD selection - you would have to bucket instances by distance yourself.

## Limits

- No tangents are baked, so normal maps will light incorrectly on deformed areas.
- No lightmap support (animated meshes are never lightmapped).
- **Compact Normals** stores normals as 8 bits per channel instead of 16-bit float, halving what the
  normal texture costs, which is about 25% off a bake that has one. A normal component is always
  between -1 and 1, so half floats spend far more range and precision on it than it can use; the error
  is roughly a third of a degree. The only place it shows is banding in specular highlights on large,
  smooth, curved surfaces, and the preview reproduces the rounding so you can check before baking.
  The format travels with the texture as a shader keyword, so bakes in both formats coexist and
  anything baked before this existed is still read as it was written.
- With **Bake Normals** off, lighting uses the mesh's bind-pose normals. Those are correct only where
  the animation does not bend the surface, so a limb that rotates will light as though it never moved.
  The preview shows this honestly - toggle it and watch.
- No tangent-space normal maps (see above); normals themselves are baked and correct.
- No motion vectors, so expect ghosting under TAA or motion blur.
- Root motion, IK and blend trees do not survive baking (animation events do - see above) - VAT has no
  skeleton at runtime. Bone-attached hitboxes, sockets and ragdolls need a separate plan.

## Performance notes

VAT removes CPU cost, not GPU cost - and it makes each vertex slightly more expensive
(2–4 dependent texture fetches). Every render pass re-runs that vertex work, so with a
depth-normals prepass and four shadow cascades you can pay it 6 times per frame. If you
are GPU-bound with a large crowd, look at shadow distance, cascade count and SSAO before
you look at draw call counts.

## Mesh Sections

A VAT is frozen: every vertex sits exactly where the texture says, and nothing can react to
anything. Sections are the exception. One is a region of the mesh that stays drivable after the
bake - a head that turns to look at the player, a torso that leans, an arm that recoils.

### Baking one

In the baker's **Sections** panel, add a section and pick a bone. The region comes from the rig's
own skin weights: the bone you pick, plus everything parented under it. That means the falloff
down the neck or the waist is the one the rigger painted, not something this package guessed.

- **Priority** decides who wins the vertices two sections both claim. It is resolved at bake time
  and costs nothing at runtime. Where a higher section fully owns a vertex the lower gets zero,
  where it does not claim at all the lower is untouched, and in between they hand over smoothly.
- **Falloff** reshapes that blend without changing what the section covers. Above 1 pulls it
  toward the core for a crisper hinge, below 1 spreads it further out.
- **Pivot Nudge** moves the hinge. The bone is usually right, but a head reads better turning
  from slightly above the neck joint.
- **Max Angle** is recorded on the clip set, and the runtime clamps to it.

**Highlight** paints the weights onto the preview - cold where the section has no hold, warm where
it owns the vertex outright - and marks the pivot. While it is on, **Test Turn** rotates the section
in the preview with the same arithmetic the shader uses, so a falloff can be judged before baking
rather than after.

Sections cost a mesh. The baker writes its own copy to hold the mask in UV3, which means Unity 6
Mesh LOD on the imported asset does not survive a bake that has sections.

### Driving one

Baked prefabs get a `VATSectionDriver`. Sections are addressed by the name given in the baker,
never by index, so reordering them in the baker cannot silently repoint existing calls.

```csharp
driver.TurnTo("Head", new Vector3(0f, 35f, 0f), 0.4f);  // glance and hold
driver.LookAt("Head", player.position, 0.4f);           // aim at a point
driver.Release("Head", 0.6f);                           // ease back
driver.Track("Head", driver.LookRotation("Head", player.position));  // keep following
```

`TurnTo` describes the transition once - start, end, when it began, how long it takes - and the GPU
walks the curve. Nothing runs per frame, so two hundred characters glancing at the player cost two
hundred writes rather than two hundred a frame. Redirecting one mid-turn is continuous: the driver
evaluates where the section is right now and makes that the new starting pose.

`Track` is for a target that keeps moving, which no curve can describe in advance. It smooths on the
CPU and pushes every frame. Everything else should use `TurnTo`.

Both go through the same four shader values, and a duration of 0 is what makes a write land
immediately - there is no separate CPU code path in the shader.

`Samples/VATSectionSample.cs` is a worked example of all of it, and the inspector on `VATSectionDriver` will pose any baked section by hand, in edit mode, without writing any code.

### Limits

- Four sections per bake, one per component of UV3.
- Sections do not chain. If one bone sits under another - a head inside a spine - priority still
  decides who owns the shared vertices, but turning the outer section will not carry the inner one
  with it. The baker warns when it sees this.
- Bounds are padded from Max Angle at bake time. A large positional offset is whatever gameplay
  code passes and cannot be known in advance, so it is not covered.

### A note on texture memory

Baked textures are written non-readable. A texture built from script keeps a full copy of its
pixels in system memory by default, on top of the copy on the GPU, and that copy is only worth
having if something calls `GetPixels` on it - nothing here does, because the shader reads these
on the GPU. Bakes made before this changed can be fixed in place from
**Tools > MiVertexAnimation > Settings**, which reports what it saves
before doing anything. It changes nothing about how they render.

### Shadow acne on a close-up character

Blotchy angular patches on a curved surface, which vanish when the main light stops casting
shadows, are self-shadowing acne rather than anything the bake did. Every pass here writes the
same vertex positions - the shadow caster carries the identical keyword set to the lit pass, so
the shadow map always describes the geometry that is actually being lit - and the caster biases
along the animated normal, not the bind-pose one.

Tune it on the light and the URP asset: **Normal Bias** first, then shadow resolution and cascade
count so more texels land on the character. Soft Shadows hide what remains.

Bias itself is free. Both values feed a function the shadow caster already runs on every vertex, so
raising them changes nothing about what the GPU does - only how far the caster is pushed, and past a
point that shows as light leaking through thin geometry or shadows detaching from where objects
touch. The other cures are not free: shadow resolution costs memory and fill, and cascade count
costs a full re-render of every caster. Cascades are worth avoiding here in particular, because
every pass re-runs the whole VAT vertex stage - the fetches, the frame blend, the sections - where
an ordinary skinned mesh skins once and reuses the result.

One interaction is worth knowing: with **Bake Normals off**, the shadow caster has only the mesh's
bind-pose normals to bias along, and those are wrong wherever the animation bends the surface. Acne
gets noticeably worse exactly where the mesh deforms most.

### Mesh LOD

Unity 6 Mesh LOD stores extra index buffers over the same vertex buffer, so a decimated level reuses
the very same vertices and `SV_VertexID` keeps addressing the right texel. A baked mesh carries its
levels across intact, and in principle VAT and Mesh LOD fit together perfectly.

In practice they do not, and it is worth knowing why before spending an afternoon on it. Mesh LOD
levels are selected by the **GPU Resident Drawer**, and the GPU Resident Drawer skips any renderer
with a `MaterialPropertyBlock` set on it. A property block is exactly how VAT gives every instance
its own clip and playback time while they all share one material, so every VAT renderer is skipped
and always draws level 0.

For level of detail on a VAT character, bake each level as its own prefab in Selected mode and
assemble them under a classic **LODGroup**, which has no such restriction. Keep Start/End Frame,
Frame Step and the clip identical across levels or they pop as they swap.

### Mesh LOD

Unity 6 Mesh LOD stores extra index buffers over the same vertex buffer, so a decimated level reuses
the very same vertices and `SV_VertexID` still addresses the right texel. A baked mesh carries its
levels across intact and animates correctly at every one of them. Tested down to level 9.

It will not do anything while **GPU Instancing** is on, and that is not a bug in either feature. An
instanced batch is one draw call over one index range; Mesh LOD needs a different range per
renderer. Unity cannot do both, and instancing wins.

That leaves a real choice:

| | Draw calls | Vertex work | Texture memory |
| --- | --- | --- | --- |
| GPU Instancing, the default | low | full | one set |
| Mesh LOD, instancing off | one draw per character per pass | reduced | one set |
| LODGroup of separate bakes | low | reduced | one set per level |

Turning instancing off costs more than it appears to. Per-instance playback rides in a
`MaterialPropertyBlock`, which already rules out the SRP Batcher, so without instancing there is no
batching left at all - every character becomes a full draw, in each of six passes.

The **LOD Group** section does this for you. Pick which of the source's Mesh LOD levels to use and
when each takes over, and the bake writes one mesh per level and assembles them under an `LODGroup`.
Every character at a given level still instances with the others at that level, so batching and
vertex reduction both survive.

Every level keeps the full vertex buffer and takes only its own triangles, so `SV_VertexID` goes on
meaning the same vertex and **one texture set serves them all**. Only mesh assets grow, by roughly
the vertex data once per level - nothing next to a VAT texture set. The vertex shader still runs
only for vertices the indices reach, so the work drops with the triangle count regardless.

It works in every renderer mode. Combined merges each level as it merges the mesh, taking each
source's indices for that level. Separate Parts puts every part into each level, because an `LOD`
holds an array of renderers and they switch together.

Mesh LOD is worth the trade only for a few characters seen at very different distances, where draw
calls were never the problem.

