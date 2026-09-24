# NuclearesVR

An unofficial VR mod for [Nucleares](https://store.steampowered.com/app/1428420) (Unity 2022.3.9f1,
Mono scripting backend, built-in render pipeline, Deferred rendering), built as a
[BepInEx](https://github.com/BepInEx/BepInEx) 5 plugin using the OpenVR SDK directly. The game has no
XR support, so there is no Unity XR pipeline to hook: the mod adds two extra "eye" cameras, renders
them to textures, and submits those to SteamVR's compositor itself.

## Status

Working, tested on hardware (Quest-class headset over Steam Link / SteamVR):

- Stereo rendering and 6DoF head tracking (orientation and position).
- Main menu, pause menu and dialogs, via a "virtual screen" (see below).
- Head tracking keeps working while the tablet / focus modes are active.
- Mouse pointer visible in the headset (arrow on the virtual screen; marker in the world for the
  tablet and ALT interactive mode).
- Skybox, trains, torch, red alarm lights.

**Open, and important: text on some in-game monitors is only visible from certain positions in the
headset.** See "The monitor problem" below.

Not started: motion controllers (phase 2), per-eye shadow differences (cosmetic).

## Build and install

```powershell
powershell -File scripts/setup-dependencies.ps1     # once: fills lib/ from your game install
dotnet build src/NuclearesVR/NuclearesVR.csproj      # also deploys into <game>/BepInEx/plugins
```

`GameDir` in the `.csproj` and in `setup-dependencies.ps1` defaults to
`E:\Programs\Steam\steamapps\common\Nuclear Last Darkness` (the install folder really is called that).
BepInEx 5.4.23.5 is installed in the game folder. Logs: `<game>/BepInEx/LogOutput.log`; Unity's own log:
`%USERPROFILE%\AppData\LocalLow\Aerilian\Nucleares\Player.log`.
`reference/` (a decompile of `Assembly-CSharp.dll`, gitignored) is regenerated with
`ilspycmd -p -o reference/Assembly-CSharp <game>/Nucleares_Data/Managed/Assembly-CSharp.dll`.

## Hotkeys (all Ctrl+Shift+...; diagnostics and experiments, safe to ignore)

| Key | What |
|---|---|
| End | Recenter the headset |
| L | Dump cameras and nearby lights to the log |
| M | Dump the renderers/materials/UI under your view direction (used for the monitor problem) |
| R | Toggle Forward / Deferred rendering on the eye cameras (Forward is the default) |
| K | Toggle stripping spot-light shadows (off by default; crashed the game once, see git history) |
| T | Toggle unlit swap for lit 3D text (off by default; did not fix the monitors) |
| G | Experiment: hide glass covers near you (did not fix the monitors) |
| Y | Experiment: 3D text ignores depth test (did not fix the monitors) |

## How it works, and things learned the hard way

- **Startup:** BepInEx runs the plugin in Nucleares' bootstrap scene, which is torn down almost at
  once. Anything created there dies. The manager object is created on the first `sceneLoaded`.
- **SteamVR frame pacing:** `WaitGetPoses` must be called every frame, before any early return.
  Skipping it gave `DoNotHaveFocus` at the menu and `AlreadySubmitted` + SteamVR's "waiting" screen
  when the tablet opened.
- **Pose maths:** OpenVR is right-handed (forward = -Z), Unity left-handed. `VrMath.ToUnity` does a
  proper similarity transform; per-component sign flips were wrong three times.
- **Projection:** use the raw OpenVR projection matrix, flip vertically at submit time through
  `VRTextureBounds_t`. `GL.GetGPUProjectionMatrix` corrupted rendering.
- **Head tracking vs the game's camera code:** `PlayerLook` only rewrites the camera when the mouse
  moves, and `Mirar` (focus mode, used by the tablet) slerps from the current rotation. So `VrManager`
  runs first (execution order -32000), restores the camera's pre-offset rotation, lets the game run,
  then re-applies the head offset in `LateUpdate`.
- **Camera settings** (clear flags, culling mask, skybox) are re-synced from the main camera every
  frame; the game changes them at runtime.
- **Virtual screen:** menus are Screen Space - Overlay canvases that no camera can see. The mod
  captures the real monitor output with `ScreenCapture` onto a quad. That quad is on its own layer
  that only the eye cameras render, otherwise the monitor renders it and the capture feeds back.
  The capture texture must match the screen size.
- **Eye cameras render in Forward.** Under Deferred, spot lights (torch, alarms) light nothing for the
  eye cameras. This is what exposed the monitor problem.

## The monitor problem (unsolved)

In-game monitors such as `Salas/N_SalaControl/SC_Bases/GrupoTerminal/MonitorResumen` show their text
in the headset only from some positions. It pops on and off abruptly (not a fade), and reveals
progressively across the screen as you move sideways (moving left reveals right-to-left; moving right
does not). The monitor view (main camera) is fine. It was fine when the eye cameras used Deferred.

The text is 3D TextMeshPro (`MeshRenderer`) using `TextMeshPro/Distance Field (Surface)`, a lit shader
the game chose deliberately; the black screen renderers under the monitor (`Servicio`, `Terminal`)
are disabled, and there is a `TapaCristal` glass-cover renderer in front.

**Ruled out** (each tried on hardware, no change): missing emission; depth-buffer precision (24-bit vs
32-bit float depth); eye near/far clip planes not following the main camera; lit text shader (swapped
to the unlit variant); glass cover (hidden); depth test on the text (disabled).

**Not yet tried / ideas:**
- Does the same text look right in Forward from the *monitor* camera? (Would separate "Forward" from
  "our custom projection / RT".)
- Try the eye cameras in Deferred again but fix the spot lights another way, so text is back on the
  path it worked on. Spot-light shadow stripping fixes the lights but "messed up" the lighting.
- Frustum/culling: check `Renderer.isVisible` for the text renderers as the head moves; try
  `Camera.cullingMatrix`, and check LOD groups and `Renderer` bounds on the text meshes.
- The TextMeshPro SDF shader derives its sharpness from `_ScreenParams` and `UNITY_MATRIX_P`; compare
  with the eye cameras' render target size and custom projection.
- Dump the *same* monitor from a position where it works and one where it doesn't and diff
  (Ctrl+Shift+M does most of this already).

## Roadmap: motion controllers (phase 2, not started)

- Every interactable (`ObjetoInteractuable`) already has `HandTarget`s and left/right hand gesture
  animation; drive those from real controller poses.
- Interaction today is Unity's `OnMouseDown`/`OnMouseEnter`, which raycasts from the camera through the
  locked cursor and cannot be redirected to a controller. Controllers need their own raycast (or touch
  test) and a call into the same effect: e.g. the private `OnClic` `UnityEvent` on `DetectorDeClic`, plus
  per-type handling for drag controls (`InterruptorPalanca`, `Regulador`, `ReguladorVertical`,
  `PalancaMecanica`, valves).
- Networking is Photon Fusion (co-op); camera and interaction should stay client-local.

## Project layout

```
src/NuclearesVR/            plugin source (partial class VrManager split by concern)
  Plugin.cs                  BepInEx entry point
  Vr/OpenVR.cs               Valve's official C# bindings (vendored)
  Vr/VrMath.cs               OpenVR <-> Unity conversions
  Vr/VrManager*.cs           stereo rendering, tracking, virtual screen, pointer, effects,
                             lighting workarounds, diagnostics and experiments
scripts/setup-dependencies.ps1
reference/  lib/  downloads/ gitignored (game code, game/BepInEx DLLs, downloaded archives)
```
