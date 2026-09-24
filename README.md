# NuclearesVR

An unofficial VR mod for [Nucleares](https://store.steampowered.com/app/1428420) (Unity 2022.3.9f1,
Mono scripting backend, built-in render pipeline, Deferred rendering), built as a
[BepInEx](https://github.com/BepInEx/BepInEx) 5 plugin using the OpenVR SDK directly. The game has no
XR support, so there is no Unity XR pipeline to hook: the mod adds two extra "eye" cameras, renders
them to textures, and submits those to SteamVR's compositor itself.

## Status

Working, tested on hardware (Quest-class headset over Steam Link / SteamVR):

- Stereo rendering and 6DoF head tracking (orientation and position).
- VR only runs while a game is loaded: the start menu is a normal 2D window, VR starts on loading a
  game and shuts down cleanly on exit to the menu (repeatable; see "Startup and shutdown").
- Pause menu and dialogs, via a "virtual screen" (see below).
- Head tracking keeps working while the tablet / focus modes are active.
- Mouse pointer visible in the headset (arrow on the virtual screen; marker in the world for the
  tablet and ALT interactive mode).
- Skybox, trains, torch, red alarm lights.

Monitor text (was the big open problem) is fixed: see "The monitor problem" below.

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
| Y | Experiment: 3D text ignores depth test (had no effect: the lit text shader lacks that property) |
| Z | Cycle text draw-order experiments (state 2 is what the automatic fix does) |
| X | Dump nearby 3D text (visibility, frustum, material) |

## How it works, and things learned the hard way

- **Startup:** BepInEx runs the plugin in Nucleares' bootstrap scene, which is torn down almost at
  once. Anything created there dies. The manager object is created on the first `sceneLoaded`.
- **Startup and shutdown:** OpenVR is not started at launch, only once `PlayerLook.Instancia` exists, so
  the start menu stays 2D. When the player has been gone for 1.5 s (exit to menu) `ShutdownVr` stops
  the submit loop, destroys the eye cameras, calls `OpenVR.Shutdown()` and only then frees the eye
  textures (freeing them earlier crashed inside the NVIDIA driver). Starting again on the next load
  works repeatedly.
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

## The monitor problem (solved)

In-game monitor text (3D TextMeshPro) only showed from some positions in the headset, popping on and
off and sweeping across the screen as the head moved. The monitor view (main camera) was fine, and it
was fine when the eye cameras used Deferred.

**Cause:** the text sits at essentially the same depth as the screen surface behind it. With a depth
tie the surface drawn last wins, and Forward rendering draws solid objects in a distance-dependent
order, so the winner changed with head position.

**Fix** (`VrManager.MonitorText.cs`, `UpdateLateText`): move the 3D text materials to render queue 2500 so
they always draw after the surface. The game's shader and depth test are otherwise untouched.

How it was found: Ctrl+Shift+Z (in `VrManager.Experiments.cs`) forces text on top (unlit, no depth
test), which fixed it and proved it was a depth-ordering problem; the second Ctrl+Shift+Z state (queue
only) is the gentle version that became the fix. Ctrl+Shift+X dumps nearby 3D text (visibility,
frustum, material). Earlier experiments that changed nothing (emission, depth precision, near/far,
unlit text, glass, text depth test) were all sound tests of the wrong things; the depth-test one never
applied because the lit text shader has no `unity_GUIZTestMode` property.

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
