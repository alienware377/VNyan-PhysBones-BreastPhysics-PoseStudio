# VNyan Physics Plugins

> **v2.0.0 rebrand:** the plugins are now named **PhysBones**, **JigglePhysics** and
> **PoseStudio** (previously Jayo-prefixed — renamed out of respect for
> [Jayo](https://github.com/jayo-exe)'s brand; the originals that inspired this project
> are his). **Updating from v1.x:** delete the old `Jayo*.dll` / `Jayo*.vnobj` files from
> your `Items\Assemblies` folders before copying the v2 files in — your settings JSONs
> are unaffected and carry over.

[![Latest Release](https://img.shields.io/github/v/release/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio?label=download&color=brightgreen)](https://github.com/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Three free VNyan plugins that bring VRChat-style physics and custom animations to your VRM VTuber avatar — no VRChat SDK, no Unity editor skills required.**

- **Hair, skirt, and tail physics** that behave like VRChat PhysBones — just JSON, no SDK.
- **Breast / soft-body jiggle** with real squash-and-stretch deformation.
- **Pose Studio** — build looping dances, toggle poses, and keyframe animations entirely inside VNyan, with 2-bone IK so feet stay planted during wide hip sways.

All three work with any VRM model in VNyan. One-click installer, live-tuning windows, configs save to your AppData folder. No admin rights needed to tune or save.

| Plugin | Button name | What it does |
|--------|-------------|--------------|
| **PhysBones** | "VRChat PhysBones" | VRChat-style dynamic bone physics for hair, skirts, tails. Verlet solver, JSON-configured chains, live sliders, bone-tree picker. |
| **Jiggle Physics** | "Jiggle Physics" | Soft-tissue jiggle (chest / pec / belly) with squash-and-stretch deformation. Leaf-bone solver, volume-preserving scale. |
| **Pose Studio** | "Pose Studio" | Build bone/mesh toggles and looping animations in VNyan — 2-bone IK, quaternion keyframe channels, per-item mesh hiding, **import MMD `.vmd` / BVH / VRMA animations** (with bone translation), and **export/import pose sets** to share. No Unity, no keyframe editor — pick bones from a tree, dial offsets, hit Save. |

All three install together, run in `LateUpdate` independently, and save their configs to your VNyan AppData folder so you never need admin rights to tune or save.

---

## Install — one click

> **The prebuilt files in `dist/` load on VNyan's Unity 2022.3 runtime. You don't need Unity or a compiler.**

1. Download or clone this repository.
2. Double-click **`INSTALL_PORTABLE.bat`**.  
   It will ask for Administrator permission (needed to write into `Program Files\VNyan`), then open a folder browser so you can point it at your VNyan install.
3. Fully close VNyan (check the system tray), then relaunch it.
4. Open the **Plugins** window — you should see buttons for **VRChat PhysBones**, **Jiggle Physics**, and **Pose Studio**.

> If your VNyan is at the default location (`C:\Program Files\VNyan`), you can use **`INSTALL.bat`** instead — it skips the folder picker.

### If the Plugins panel is empty after installing

Two causes account for almost every case, in order of likelihood:

1. **"Allow Third Party Plugins" is turned off.**  
   Go to VNyan → **Settings → Misc → Allow Third Party Plugins** and make sure it's **enabled**. VNyan loads no assembly plugins at all when this is off, and logs nothing — the panel is silently empty.

2. **You have multiple VNyan installs and launched the wrong one.**  
   Plugins were installed into one folder but a different VNyan.exe is actually running. Check VNyan's log (`%APPDATA%\..\LocalLow\Suvidriel\VNyan\Player.log`) — the header line shows which copy launched. Install into that copy and restart.

If neither of those applies, run **`TROUBLESHOOT.bat`** — it produces a `VNyan_Diagnostics_Report.txt` with file placement, Mark-of-the-Web block status, Unity runtime vs bundle version, and the active VNyan path. If it comes back clean and the panel is still empty, it's almost certainly cause #1 above.

---

## PhysBones

VRChat-PhysBone-style dynamic bone physics for hair, skirts, and tails. The simulation is re-implemented from scratch — no VRChat SDK, no runtime or licensing dependencies on VRChat. Parameters mirror VRChat's so settings you've authored for a VRChat avatar map over directly.

### Configuring (`physbones.json`)

The plugin searches these locations in order and loads the first it finds:

1. `%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\physbones.json` ← your saved/edited copy
2. `…\VNyan\physbones.json`
3. `…\VNyan\Items\Assemblies\PhysBones\physbones.json`

**Save** in the tuning window always writes to location 1 (no admin needed). Once saved there, that copy wins on every future load. The file is the shareable artifact — send it to anyone and they drop it in.

A minimal config looks like this:

```json
{
  "chains": [
    {
      "name": "hair",
      "rootBone": "HairRoot",
      "pull": 0.2,
      "spring": 0.4,
      "stiffness": 0.1,
      "gravity": 0.05
    }
  ]
}
```

Copy `config/physbones.example.json` for a fuller starting point.

### Live-tuning window

Click **VRChat PhysBones** in the Plugins panel to open the draggable tuning window:

- **Chain** dropdown — which chain the sliders edit.
- **Physics enabled** — global on/off.
- **Angle limit** — toggles the selected chain between `angle` and `none` limit type.
- **Override native physics** — disables the model's own SpringBone / DynamicBone / MagicaCloth solvers so only these PhysBones drive the bones. Useful when two solvers fight over the same bone and cause jitter.
  - **└ only my configured chains** — scoped version: only disables solvers whose bones overlap your chains; everything else keeps running.
- **Sliders** — `Pull`, `Spring`, `Stiffness`, `Gravity` (−1..1), `Grav Falloff`, `Immobile`, `Max Angle`, `Radius`, `Max Stretch`. Apply **instantly** — no reload needed.
- **Bones** — opens the bone browser (see below).
- **Reload / Save / Close**.

### Bone browser

Click **Bones** to open a collapsible tree of the loaded model's actual bone hierarchy. Click any bone name to add a new PhysBone chain rooted there — it's added to the config immediately and auto-selected in the Chain dropdown. New chains live in memory until **Save**.

### Parameters

| Field | Range | Meaning |
|-------|-------|---------|
| `pull` | 0..1 | Force returning bones to their rest pose. Higher = snappier. |
| `spring` | 0..1 | Bounciness / oscillation. Higher = more bounce. |
| `stiffness` | 0..1 | Resistance to bending away from the rest direction. |
| `gravity` | −1..1 | World gravity (negative pulls up). |
| `gravityFalloff` | 0..1 | Reduces gravity near the rest pose so bones don't sag from their authored shape. |
| `immobile` | 0..1 | How much bones ignore avatar movement (1 = don't react to walking). |
| `limitType` | `none`/`angle` | `angle` clamps each bone within `maxAngle` of its rest direction. |
| `maxAngle` | degrees | Cone limit when `limitType` is `angle`. |
| `radius` | metres | Bone collision radius. |
| `maxStretch` | 0..1 | Allowed stretch beyond rest length (0 = rigid). |
| `colliders` | names | Which colliders this chain collides against. |

### Colliders

`type` is `sphere`, `capsule`, or `plane`.

- **sphere** — `offset` (local center) + `radius`.
- **capsule** — `offset` + (`axis` + `height`, or `offsetEnd`) + `radius`.
- **plane** — `offset` (a point on the plane) + `axis` (the normal). Bones stay on the positive-normal side.

Set `bone` to attach a collider to a bone (follows it), or `null` for a fixed world-space collider (e.g. a floor plane). Collider names are referenced from a chain's `colliders` list.

### Bone names

`rootBone`, `ignore`, and collider `bone` are resolved as: Unity humanoid enum first (`Head`, `Chest`, `Hips`, `Spine`, …), then a case-insensitive transform-name search. For hair/tails/skirts use the actual bone names from your VRM.

### How a chain is built

A chain simulates the whole subtree under `rootBone`. One child → swings (aims at child). Multiple children → treated as a fixed split point, each branch solved independently (matches VRChat's `multiChildType = Ignore`). Put bones you don't want simulated in `ignore`.

### Notes / limitations

- Faithful **approximation** of VRChat PhysBones — feel may differ slightly; tune with the live sliders.
- Implemented: pull, spring, stiffness, gravity, gravity falloff, immobile, angle limit, max stretch, sphere/capsule/plane colliders, multi-child split handling.
- Not yet implemented: grab/pose, squish, hinge/polar limits, per-bone radius curves, collider `inside` mode.

---

## Jiggle Physics

Soft-tissue jiggle for chest, pec, and belly bones — an original implementation with **squash-and-stretch deformation**. Chest/breast bones are usually leaf bones (no child), so a chain solver has nothing to aim. Jiggle Physics synthesises a virtual tip along the bone's local forward axis, simulates it, rotates the bone to follow, then drives `localScale`:

- **Stretch** — tip sags → bone elongates and thins sideways (volume-preserving).
- **Squish** — a collider presses in → bone flattens along the contact and bulges sideways.

### Configuring (`jigglephysics.json`)

Same load/save locations as PhysBones (file is `jigglephysics.json`). The shipped starter defines two bones, `Bust_L` and `Bust_R` — **rename `bone` to match your model** (common names: `Bust_L/R`, `Breast_L/R`, `胸_L/R`).

| Field | Meaning |
|-------|---------|
| `bone` | Bone / transform name |
| `axis` | Local forward the bone points (swing tip direction); default `[0,0,1]` |
| `length` | Virtual swing-arm length in metres (default `0.08`) |
| `weight` `bounce` `stiffness` `damping` `pull` | 0..1 spring feel |
| `limitType` `maxAngle` | Bra limiter (`angle` + degrees) |
| `stretch` `squish` | 0..1 deformation gains |
| `radius` | Collision radius (m) |
| `selfCollide` `selfRadius` | Left/right self-collision sphere |
| `colliders` | World collider names (same shapes as PhysBones) |

Global `settings`: `enabled`, `substeps`, `gravityDir`, `minScale`/`maxScale` (deform clamps, default `0.65`/`1.6`), `scaleSpeed` (ease rate, default `14`).

### Tuning window

Click **Jiggle Physics**: `Weight`, `Bounce`, `Stiffness`, `Damping`, `Pull`, `Bra Angle`, `Stretch`, `Squish`, `Radius` sliders — all apply instantly. **Save** writes to AppData. Disabling the plugin fully restores the authored rotation **and scale**.

### Notes

- Deformation writes `localScale` every frame — if another system also animates these bones' scale, this plugin wins while enabled.
- If the swing plane looks wrong, adjust `axis` for that bone.
- Coexists with PhysBones: point each plugin at different bones (PhysBones for hair/skirt, Jiggle for chest).

---

## Pose Studio

Build **toggles** and **looping animations** from bone / mesh / blendshape offsets — entirely inside VNyan, no Unity or keyframe editor needed. Every entry is a named **PoseItem** (a set of transform offsets and blendshape targets). The only difference between the two types is how it's driven:

- **Toggle** — hold the pose on or off, easing in/out over *Blend Time*.
- **Animation** — oscillate the pose on a loop using a waveform (`sine` / `triangle` / `pulse`) at *Anim Speed* cycles/sec. Or use a **keyframe timeline** for multi-stage looping motions (up to 240 keyframes, each with its own distinct full pose).

### Window walkthrough

1. **+ Toggle / + Animation** — create a new item. **Remove** deletes the selected one.
2. **Name** — rename the selected item.
3. **Hotkey** — optional global key combo (e.g. `F8`, `Ctrl+Shift+E`) that fires even when VNyan is not the foreground window.
4. **Activate** — toggle on/off live. **Animate on a loop** switches between the two types.
5. **Wave / Anim Speed / Blend Time** — waveform, speed, and ease-in/out time.
6. **Keyframe timeline** — tick *Use keyframe timeline* to switch from a wave to a custom sequence of poses. Add/remove keyframes; each has its own bone/mesh/blendshape offsets and a transition duration.
7. **Blendshape trigger** — tick *Drive strength from a blendshape* and pick a source shape (e.g. face-tracked `MouthOpen`). The source's 0–100% drives the item's strength continuously. **Curve** shapes the response (1 = linear, >1 = ease-in, <1 = ease-out). **Strength %** scales the output.
8. **Add Bone / Add Mesh** — opens the collapsible model tree; pick any bone or mesh object. Tick Position / Rotation / Scale and dial the X/Y/Z sliders. **Remove** drops it.
9. **Hide Mesh** — opens the mesh browser with a ☐/☒ toggle per renderer; the picked meshes are **hidden while the item is active** and restored when it turns off. Great for swapping or removing clothing / accessories with a toggle or hotkey.
10. **Add Blendshape** — opens the same browser in mesh→shape mode. Set **Blend Wt** (0–100). **Remove** drops it.
11. **IK goals** — add 2-bone IK goals (legs/arms) that pin end effectors in place after FK runs. See the [IK section](#inverse-kinematics-ik) below.
12. **Import Animation…** — load an external animation and retarget it onto your rig as a keyframe item (see [below](#importing-animations)).
13. **Export Poses… / Import Poses…** — share toggles & animations (see [below](#sharing-poses-export--import)).
14. **Reload / Save / Close**.

### Importing animations

**Import Animation…** loads a motion file, translates its bones onto your avatar's humanoid rig, and creates a keyframe animation item (quaternion channels, resampled to 30 fps). Supported:

- **MMD `.vmd`** — Japanese bone names (センター, 上半身, 左腕, fingers, legs…) → VRM humanoid via a built-in table; **morphs → blendshapes** by name. Because MMD's coordinate convention varies, importing a `.vmd` opens a live **adjustment dialog** — the animation plays on your avatar while you click orientation **presets (A–E)**, **Mirror L/R**, **Face 180°**, and **Lay flat (±90°)** until it looks right, then **Keep this** or **Cancel**.
- **BVH** — Mixamo / Rokoko / Blender joints (handles `mixamorig:` prefixes and common aliases).
- **VRMA / `.glb` / `.gltf`** — reads the VRM-animation humanoid map directly (near 1:1).

Only the bones your rig actually has are driven; the status line reports how many bones/morphs mapped. Then **Save**, **Activate** to play.

### Sharing poses (export / import)

- **Export Poses…** — opens a checklist of your items; tick the toggles/animations to share, then export a `.json` (same schema as `posestudio.json`, just the selected items).
- **Import Poses…** — pick a shared `.json`. Items are **appended** to your existing ones. If any name already exists, a dialog asks whether to **Overwrite same-named**, **Keep both (rename imported)**, or **Skip same-named** — so imports never silently clobber your own work.

> **Physics + Pose Studio dances:** Pose Studio applies its pose *early* in the frame (before physics), so spring/jiggle bones — native VRM SpringBone, PhysBones, and Jiggle — react to a Pose Studio animation the same as they would to face-tracked motion.

### Configuring (`posestudio.json`)

Saved to `%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\posestudio.json` — no admin needed.

| Field (per item) | Meaning |
|------------------|---------|
| `name` | Label in the dropdown |
| `type` | `"toggle"` or `"animation"` |
| `active` | On/off state |
| `blendTime` | Ease seconds |
| `speed` | Animation cycles/sec |
| `waveform` | `"sine"`, `"triangle"`, or `"pulse"` |
| `useKeyframes` | `true` to use the keyframe timeline instead of a wave |
| `keyframes[]` | `{ seconds, channels[] }` — each keyframe has a duration and per-target pose. A transform channel may carry `quat` `[x,y,z,w]` which overrides its euler `rotation` (slerped between keyframes) — handy for animation exported from other tools. A channel `id` of `ik:<GoalName>` animates that IK goal's target as a per-keyframe offset. |
| `useTrigger` | `true` to drive strength from a blendshape |
| `triggerMesh` | Source `SkinnedMeshRenderer` name (empty = any mesh) |
| `triggerShape` | Source blendshape name |
| `triggerCurve` | Response exponent (1 = linear) |
| `triggerScale` | Overall output strength % |
| `hotkey` | Key combo string, e.g. `"F8"` or `"Ctrl+Shift+E"` (empty = no hotkey) |
| `bones[]` | `{ bone, usePosition, useRotation, useScale, position[3], rotation[3], scale[3] }` |
| `meshes[]` | `{ mesh, usePosition, useRotation, useScale, position[3], rotation[3], scale[3] }` |
| `blendshapes[]` | `{ mesh, shape, weight }` (empty `mesh` searches every mesh) |
| `hideMeshes[]` | Renderer names to hide (disable) while the item is active; restored when it turns off |
| `ikGoals[]` | 2-bone IK goals; see [IK section](#inverse-kinematics-ik) |

Bone and mesh names accept Unity humanoid enum names (`Head`, `LeftHand`, …) or raw transform names.

### Inverse kinematics (IK)

Each animation item can carry a list of **2-bone IK goals** that run in `LateUpdate` after FK. A goal pins an end effector (foot, hand, wrist) to a target position in a chosen reference space, so limbs stay planted even as the body sways or rotates.

#### Adding an IK goal

In the Pose Studio panel, scroll to **— Inverse kinematics (IK) —** and click **Add IK Goal**. Fill in:

| Field | Meaning |
|-------|---------|
| **Upper / Lower / End** | The three bones of the limb (e.g. `LeftUpperLeg` / `LeftLowerLeg` / `LeftFoot`) |
| **Space** | Reference space the target is measured in: `root` (avatar root), `world`, or any bone name (e.g. `Chest`) |
| **Capture** | *bind (rest pose)* — pins where the end effector is at avatar bind time (good for feet, which should be centered under the body); *play (first frame)* — pins wherever the end effector lands once FK has run (good for hands already in a specific pose) |
| **Hold end rotation** | Also lock the end bone's orientation (sole of foot stays flat; hand angle preserved) |
| **Weight** | 0–1 blend of the IK result over the FK pose |
| **Enable** | Toggle this goal on/off without removing it |

After editing goals, click **Reload** to re-capture and apply.

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

`captureMode` is `"bind"` or `"play"`. Set `capture: false` and supply explicit `position`/`rotation` to pin to a fixed point instead of auto-capturing.

### Notes

- While active, a toggle **holds** the affected bones/meshes at (base + offset), pausing the avatar's idle animation on those specific transforms. Pick only what you mean to drive.
- Offsets are local-space; very large rotation offsets on humanoid bones can look odd.
- Hotkeys fire globally (even when VNyan is not the foreground window) via Win32 `GetAsyncKeyState`.
- Blendshape triggers read in `LateUpdate` — the source must be driven before then (VNyan face tracking is).
- IK runs in `LateUpdate` after FK, so the FK pose is already set when IK corrects limb positions.
- Runs alongside PhysBones and Jiggle — all three are independent.

---

## Rebuild from source

The prebuilt `dist/` files should load on any VNyan 2022.3 install. If they don't (check the VNyan log for a bundle-version error), you can rebuild against your own VNyan and Unity.

### DLL only (no Unity needed)

```
csc.exe -noconfig @build/build.rsp
csc.exe -noconfig @build/build_jiggle.rsp
csc.exe -noconfig @build/build_pose.rsp
```

Each `.rsp` pulls its references straight from `C:\Program Files\VNyan\VNyan_Data\Managed`. Uses the in-box .NET Framework `csc.exe` (C# 5). Or run **`INSTALL_BUILD.bat`** which handles everything automatically.

### `.vnobj` bundles (needs Unity 2022.3.x)

```
Unity.exe -batchmode -quit -nographics -projectPath "<repo>\_unitybuild" -executeMethod PhysBoneBuild.Build
Unity.exe -batchmode -quit -nographics -projectPath "<repo>\_unitybuild" -executeMethod JiggleBuild.Build
Unity.exe -batchmode -quit -nographics -projectPath "<repo>\_unitybuild" -executeMethod PoseStudioBuild.Build
```

Or run **`INSTALL_BUILD.bat`** which auto-detects Unity, recompiles the DLLs, rebuilds the bundles, and installs everything.

> **Critical:** VNyan loads the autostart prefab by the addressable name `vnyanitem`. The build scripts set this explicitly. If you rebuild manually and the plugin button never appears, confirm the bundle's addressable name is `vnyanitem` (not the prefab's path).

After rebuilding, copy the new DLL and `.vnobj` into `dist/` before running an installer.
