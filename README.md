# VNyan Pose Studio

[![Latest Release](https://img.shields.io/github/v/release/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio?label=download&color=brightgreen)](https://github.com/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**A free VNyan plugin for building toggles, poses and looping dances for your VRM VTuber avatar, right inside VNyan. No Unity, no keyframe editor skills needed.**

- Toggle poses, outfits and expressions with a click or a hotkey.
- Looping animations from a wave or a keyframe timeline, with 2-bone IK so feet stay planted.
- Import MMD `.vmd`, BVH and VRMA animations, and share pose sets with friends.
- Comes with a **Hip Sway Dance** that fits itself to your avatar.

> **PhysBones and Jiggle Physics have moved.** They used to ship in this repo together with Pose
> Studio. From v2.5.0 they have their own home:
> **[VNyan-PhysBones-JigglePhysics](https://github.com/alienware377/VNyan-PhysBones-JigglePhysics)**.
> This repo is now just Pose Studio. Already have all three installed? Nothing changes for you:
> each installer only updates its own plugins.

> **New in v2.5.0:** the bundled **Hip Sway Dance**, **Fit to this model** sliders, VRM expressions
> in the blendshape picker, blendshape weights from -1000% to +1000%, a **Restore unsaved** safety
> copy, folding window sections, and blendshapes that VNyan no longer paints over.

---

## Install: one click

> The prebuilt files in `dist/` load on VNyan's Unity 2022.3 runtime. You don't need Unity or a compiler.

1. Download the latest release zip (or clone this repository) and unzip it anywhere.
2. Fully close VNyan (check the system tray), then double-click **`install.bat`**.
3. The installer **looks for VNyan by itself** (a VNyan that's open, the log VNyan keeps from
   the last time it ran, your Steam libraries, and the itch app) and asks you to confirm what it
   found. If it can't find VNyan, or you say no, it asks you to **open your VNyan folder**: the
   one with `VNyan.exe` inside. VNyan has no installer of its own, so it can live anywhere, and the
   installer never assumes a location for you.
4. Admin rights are requested **only if your VNyan folder is write-protected**. A VNyan you
   unzipped yourself never sees a UAC prompt.
5. Updating from v1.x? The installer retires the old `JayoPoseStudio` files automatically so both
   versions can't load at once. Your settings carry over.
6. Launch VNyan and open the **Plugins** window. You should see **Pose Studio**.

> Installing from a script? `powershell -ExecutionPolicy Bypass -File install.ps1 -Target "D:\Apps\VNyan" -Yes`
> installs without prompting. It still asks Windows for admin rights if that folder is
> write-protected, and stops if VNyan is open. `-ListFound` just prints the VNyan folders the
> search finds.

### If the Plugins panel is empty after installing

Two causes account for almost every case, in order of likelihood:

1. **"Allow Third Party Plugins" is turned off.**
   In VNyan go to **Settings > Misc > Allow Third Party Plugins** and make sure it's **enabled**.
   When it's off VNyan loads no plugins at all and logs nothing, so the panel is silently empty.

2. **You have more than one VNyan and launched the other one.**
   The plugin went into one folder but a different VNyan.exe is running. VNyan's log
   (`%APPDATA%\..\LocalLow\Suvidriel\VNyan\Player.log`) names the copy that launched in its first
   lines. Install into that copy and restart.

If neither applies, run **`TROUBLESHOOT.bat`**. It writes a `VNyan_Diagnostics_Report.txt` with
file placement, Windows download-block status, Unity runtime versus bundle version, and the VNyan
path in use. If it comes back clean and the panel is still empty, it's almost certainly cause 1.

---

## Pose Studio

Build **toggles** and **looping animations** from bone, mesh and blendshape offsets, entirely
inside VNyan. Every entry is a named **item** (a set of transform offsets and blendshape targets).
The two types differ only in how they're driven:

- **Toggle:** holds the pose on or off, easing in and out over *Blend Time*.
- **Animation:** loops the pose with a wave (`sine`, `triangle` or `pulse`) at *Anim Speed*
  cycles per second, or plays a **keyframe timeline** for multi-step motions (up to 240
  keyframes, each with its own full pose).

### Window walkthrough

Click a section heading to fold it open or shut.

1. **+ Toggle / + Animation** create a new item. **Remove** deletes the selected one.
2. **Name** renames the selected item.
3. **Hotkey:** an optional key combo (e.g. `F8`, `Ctrl+Shift+E`) that works even when VNyan isn't the window in front.
4. **Activate** turns it on or off live. **Animate on a loop** switches between the two types.
5. **Wave / Anim Speed / Blend Time:** waveform, speed, and ease in/out time.
6. **Keyframe timeline:** tick *Use keyframe timeline* to swap the wave for your own sequence of
   poses. Add and remove keyframes; each has its own bone, mesh and blendshape offsets and a
   transition time.
7. **Blendshape trigger:** tick *Drive strength from a blendshape* and pick a source shape (e.g.
   face-tracked `MouthOpen`). The source's 0 to 100% drives the item's strength. **Curve** shapes
   the response (1 = linear, above 1 eases in, below 1 eases out). **Strength %** scales the result.
8. **Add Bone / Add Mesh** open the model tree; pick any bone or mesh object, tick Position /
   Rotation / Scale and dial the X/Y/Z sliders. **Remove** drops it.
9. **Hide Mesh** opens the mesh list with a tick box per mesh; ticked meshes are **hidden while
   the item is on** and come back when it turns off. Great for swapping clothes or accessories.
10. **Add Blendshape** opens the same browser in shape mode, including your avatar's **VRM
    expressions** (Joy, A, Blink and so on). Set **Blend Wt** anywhere from **-1000% to +1000%**,
    so you can exaggerate a shape or run it backwards. **Remove** drops it.
11. **Fit to this model** (keyframe animations): **Arms Out**, **Arms Up**, **Arms Fwd**,
    **Feet Apart**, **Feet Fwd** and **Hips Up** move where the animation puts the hands, feet and
    hips, so a dance made on another body fits yours without editing keyframes. Saved per animation.
12. **IK goals:** 2-bone IK goals (legs, arms) that pin hands or feet in place after the pose is
    applied. See [IK](#inverse-kinematics-ik) below.
13. **Import Animation...** loads an animation from another tool and turns it into a keyframe
    item (see [below](#importing-animations)).
14. **Export Poses... / Import Poses...** share toggles and animations (see [below](#sharing-poses-export-and-import)).
15. **Open Timeline Editor** opens a timeline for the selected keyframe item: draggable keys, a
    playhead you can scrub, zoom and pan, retiming by dragging, easing per segment, lanes per group
    (bones, meshes, blendshapes), capture-into-key with masking, and key copy/paste.
16. **Idle animation (auto-play when tracking is lost):** tick this on one item and it plays by
    itself once your head has been still for the **Idle Wait (s)** time (15 s by default, 5 to
    120 s), then blends out the moment you move again.
17. **Reload / Save / Close**, and **Restore unsaved** (see below).

### Restore unsaved

Pose Studio keeps a spare copy of changes you haven't saved yet: a minute after you stop editing,
when you close its windows, before Reload, and when VNyan closes. It goes to a separate file,
`posestudio.unsaved.json`, and never touches your real settings. If VNyan closed before you
pressed Save, a **Restore unsaved** button shows up next to Save the next time; it also undoes an
accidental Reload. Press **Save** afterwards to keep what you restored.

### The bundled Hip Sway Dance

A 25 second dance ships with Pose Studio and appears in your list switched off until you try it.
It measures your avatar when it binds and scales itself to your body, and the **Fit to this
model** sliders fine-tune it. It's offered once: if you delete or rename it, it stays that way.

### Importing animations

**Import Animation...** loads a motion file, maps its bones onto your avatar's humanoid rig, and
creates a keyframe animation item (rotation channels, resampled to 30 fps). Supported:

- **MMD `.vmd`:** Japanese bone names (センター, 上半身, 左腕, fingers, legs and so on) map to VRM
  humanoid bones through a built-in table, and **morphs map to blendshapes** by name. Because MMD's
  axes vary, importing a `.vmd` opens a live **adjustment dialog**: the animation plays on your
  avatar while you try orientation **presets (A to E)**, **Mirror L/R**, **Face 180°** and
  **Lay flat (±90°)** until it looks right, then **Keep this** or **Cancel**.
- **BVH:** Mixamo, Rokoko and Blender joints (handles `mixamorig:` prefixes and common aliases).
- **VRMA / `.glb` / `.gltf`:** reads the VRM animation humanoid map directly (close to 1:1).

Only bones your rig actually has are driven; the status line says how many bones and morphs
mapped. Then **Save**, and **Activate** to play.

### Sharing poses (export and import)

- **Export Poses...** opens a checklist of your items. Tick the ones to share, then export a
  `.json` (same layout as `posestudio.json`, just the chosen items).
- **Import Poses...** picks a shared `.json`. Items are **added** to yours. If a name already
  exists, you choose **Overwrite same-named**, **Keep both (rename imported)** or **Skip
  same-named**, so an import never quietly replaces your own work.

> **Physics and Pose Studio dances:** Pose Studio applies its pose early in the frame, before
> physics, so spring and jiggle bones (VRM SpringBone,
> [PhysBones and Jiggle Physics](https://github.com/alienware377/VNyan-PhysBones-JigglePhysics))
> react to a Pose Studio animation the same way they react to tracked motion.

### Configuring (`posestudio.json`)

Saved to `%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\posestudio.json`, no admin needed. If
the file changes on disk while VNyan runs, Pose Studio reloads it within a second.

| Field (per item) | Meaning |
|------------------|---------|
| `name` | Label in the dropdown |
| `type` | `"toggle"` or `"animation"` |
| `active` | On/off state |
| `blendTime` | Ease time in seconds |
| `speed` | Animation cycles per second |
| `waveform` | `"sine"`, `"triangle"` or `"pulse"` |
| `useKeyframes` | `true` to use the keyframe timeline instead of a wave |
| `keyframes[]` | `{ seconds, channels[] }`: each keyframe has a duration and a pose per target. A transform channel may carry `quat` `[x,y,z,w]`, which overrides its euler `rotation` (blended smoothly between keyframes). A channel `id` of `ik:<GoalName>` animates that IK goal's target as an offset per keyframe. |
| `fit[]` | Fit to this model offsets per body group (`Hands`, `Feet`, `Hips`) |
| `useTrigger` | `true` to drive strength from a blendshape |
| `triggerMesh` | Source mesh name (empty = any mesh) |
| `triggerShape` | Source blendshape name |
| `triggerCurve` | Response exponent (1 = linear) |
| `triggerScale` | Overall output strength % |
| `hotkey` | Key combo string, e.g. `"F8"` or `"Ctrl+Shift+E"` (empty = no hotkey) |
| `bones[]` | `{ bone, usePosition, useRotation, useScale, position[3], rotation[3], scale[3] }` |
| `meshes[]` | `{ mesh, usePosition, useRotation, useScale, position[3], rotation[3], scale[3] }` |
| `blendshapes[]` | `{ mesh, shape, weight }` (empty `mesh` searches every mesh) |
| `hideMeshes[]` | Mesh names to hide while the item is on; shown again when it turns off |
| `ikGoals[]` | 2-bone IK goals; see [IK](#inverse-kinematics-ik) |
| `idleAnim` | `true` = play this item by itself once head tracking goes still (only one item should have it) |

Global `settings`: `idleDelay` (seconds of stillness before the idle item takes over, default
`15`, the *Idle Wait (s)* slider), `idleMotionThreshold` (degrees of head motion per frame that
still count as "you're there", default `0.02`) and `bundledOffered` (bundled animations already
offered once).

Bone and mesh names accept Unity humanoid names (`Head`, `LeftHand`, ...) or the model's own names.

### Inverse kinematics (IK)

Each animation item can carry **2-bone IK goals** that run after the pose is applied. A goal pins
a hand or foot to a target in a chosen space, so limbs stay planted even while the body sways or
turns.

#### Adding an IK goal

In the Pose Studio window, open **Inverse kinematics (IK)** and click **Add IK Goal**. Fill in:

| Field | Meaning |
|-------|---------|
| **Upper / Lower / End** | The three bones of the limb (e.g. `LeftUpperLeg` / `LeftLowerLeg` / `LeftFoot`) |
| **Space** | What the target is measured against: `root` (avatar root), `world`, or any bone name (e.g. `Chest`) |
| **Capture** | *bind (rest pose)* pins where the hand or foot is when the avatar loads (good for feet, centred under the body); *play (first frame)* pins wherever it lands once the pose has run (good for hands already in a pose) |
| **Hold end rotation** | Also lock the end bone's angle (sole stays flat, hand angle kept) |
| **Weight** | 0 to 1 blend of the IK result over the plain pose |
| **Enable** | Turn this goal on or off without removing it |

After editing goals, click **Reload** to capture and apply them again.

#### In `posestudio.json`

```json
"ikGoals": [
  {
    "enabled": true,
    "name": "LeftLeg",
    "upper": "LeftUpperLeg",
    "lower": "LeftLowerLeg",
    "end": "LeftFoot",
    "space": "root",
    "captureMode": "bind",
    "holdRotation": true,
    "weight": 1.0,
    "capture": true,
    "position": [0, 0, 0],
    "rotation": [0, 0, 0]
  }
]
```

`captureMode` is `"bind"` or `"play"`. Set `capture: false` and give an explicit
`position`/`rotation` to pin to a fixed point instead.

### Notes

- While on, a toggle **holds** the bones and meshes it uses at (rest + offset), which pauses the
  avatar's idle animation on those bones only. Pick just what you mean to move.
- Offsets are local; very large rotations on humanoid bones can look odd.
- Hotkeys work everywhere (even when VNyan isn't in front) through Windows' `GetAsyncKeyState`.
- Blendshapes are applied after VNyan's own face update, so VNyan doesn't paint over them and
  tracking still shows underneath.
- Runs alongside PhysBones, Jiggle Physics and VRM spring bones; they're independent.

---

## Rebuild from source

The prebuilt `dist/` files should load on any VNyan with Unity 2022.3. If they don't (check
VNyan's log for a bundle version error), you can rebuild against your own VNyan and Unity.

### DLL only (no Unity needed)

Run this from the repo root (the `.rsp` file uses paths relative to it):

```
csc.exe -noconfig @build/build_posestudio.rsp
```

The `.rsp` takes its references from `C:\Program Files\VNyan\VNyan_Data\Managed`. If your VNyan
lives somewhere else, change those paths in the `.rsp`. It uses the .NET Framework `csc.exe`
that comes with Windows (C# 5). The DLL **must** stay named `PoseStudio.dll`: the bundle finds its
scripts by assembly name.

### `.vnobj` bundle (needs Unity 2022.3.x)

```
Unity.exe -batchmode -quit -nographics -projectPath "<repo>\_unitybuild" -executeMethod PoseStudioBuild.Build
```

> **Important:** VNyan loads the autostart prefab by the addressable name `vnyanitem`. The build
> script sets it for you. If you rebuild by hand and the plugin button never shows up, check that
> the bundle's addressable name is `vnyanitem` (not the prefab's path).

After rebuilding, copy the new DLL and `.vnobj` into `dist/PoseStudio/` before running the installer.

---

## Credits

Pose Studio was originally named `JayoPoseStudio`. It was renamed in v2.0.0 out of respect for
[Jayo](https://github.com/jayo-exe)'s brand; the originals that inspired this project are his.

MIT licensed, see [LICENSE](LICENSE).
