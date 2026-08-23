# Demo

A scene, a crowd, and a character whose head turns to watch you move around it.

## Setup

The scene opens empty, because a package cannot ship a bake. Baked textures belong to the exact mesh
and clips that went into them, so the only honest thing to include is the model — the bake is yours.
It takes two clicks:

1. Open `Scenes/VATDemo.unity`, select **VAT Demo Rig**, and press **Open the Baker, set up for this
   sample**. The baker opens loaded with `Source/DemoBakeSettings.asset`: `Source/Rig_Medium_General.prefab`,
   its `Idle_A` clip, a mesh section on the `head` bone, and an output folder inside this sample.
2. Press **Bake**.
3. Drag the prefab it wrote into `Baked/` onto the rig's **VAT Prefab** field.

Press play, and watch it in the **Scene** view. Twenty-five copies, and every one of them turning
to follow the orbiting marker. The marker is a gizmo rather than a mesh, so that the sample does
not have to ship a material of its own — which is why the Scene view is where to watch this.

The rig is six separate meshes — head, body, two arms, two legs — so the preset bakes in **Combined
Mesh** mode. That merges them into one mesh sharing one texture set, instead of six of each, and the
whole character comes out as a single renderer.

## Changing the starting settings

Everything that button sets up lives in `Source/DemoBakeSettings.asset` — select it and edit it like
any other asset. Texture width, frame step, precision, which bone the section sits on, whether there
is a section at all. The output folder is the one thing it does not control, because a sample cannot
know where it will be imported to; that is filled in when the baker opens.

## Using your own model

Nothing here is tied to the robot. Drop a rigged model into `Source/`, then either point
`DemoBakeSettings.asset` at it or set the baker up by hand.

The baker reads its clip list off an Animator's controller, so **give the baker a prefab with the
controller already assigned**, not the raw FBX — an imported model carries no controller, so the clip
list comes up empty. That is all `Source/Rig_Medium_General.prefab` is: the FBX in a
scene, controller assigned, saved out. Do the same with yours once and it is done.

Two more things to check when you swap it:

- **The section's bone.** `head` is a bone on the model that ships here. Yours will have different
  names — pick one in the baker and use its **Highlight** button to see which vertices it moves.
- **The section's name.** `VATSectionSample` uses the first baked section when its **Section Name**
  is empty, so renaming the section costs nothing unless you type a name in.

A bake with no sections at all still works. The rig spawns the crowd and the sample quietly does
nothing, which is what you want when the point is the instancing rather than the sections.

## What is in here

| | |
|---|---|
| `VATDemoRig` | Spawns the prefab as a grid and hands the sample its target. The grid is the point: watch the frame time as you raise it. |
| `VATSectionSample` | Four ways to drive a baked section. `LOOK_AT` and `SWAY` write every frame; `GLANCE` and `RECOIL` describe a transition once and let the GPU walk it — which is what almost everything should do. |
| `VATDemoOrbit` | Moves the target, so `LOOK_AT` has something to follow. |
| `Source/Rig_Medium_General.prefab` | The model with its controller assigned, which is what the baker wants. |
| `Source/DemoBakeSettings.asset` | Everything the setup button presets. Edit it like any other asset. |

None of this is meant to ship in a game. It is here to be read and then thrown away.

## Credits

The character is from the [KayKit Character Pack](https://kaylousberg.com) by Kay Lousberg, released
under CC0. See `Source/THIRD-PARTY.md`.
