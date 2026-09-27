# NuclearesVR

An unofficial VR mod for [Nucleares](https://store.steampowered.com/app/1428420) (Unity 2022.3.9f1,
Mono scripting backend, built-in render pipeline, Deferred rendering), built as a
[BepInEx](https://github.com/BepInEx/BepInEx) 5 plugin using the OpenVR SDK directly. The game has no
XR support, so there is no Unity XR pipeline to hook: the mod adds two extra "eye" cameras, renders
them to textures, and submits those to SteamVR's compositor itself.

## Status

Working, tested on hardware (Quest 3 over Steam Link / SteamVR, RTX 4090). The game runs at about 90 fps.

- Stereo rendering and 6DoF head tracking (orientation and position).
- VR only runs while a game is loaded: the start menu is a normal 2D window, VR starts on loading a
  game and shuts down cleanly on exit to the menu (repeatable; see "Startup and shutdown").
- The monitor window shows the headset's view (setting `MonitorView`).
- **Menus on a floating "virtual screen"** (see below): the pause menu, confirmation dialogs (for example putting
  on the hazmat suit), the inventory and the main menu. Its size and position are settings (`MenuScreen...`).
  The tutorial's instruction box floats below eye level, and the gauge information panel shows in front of you.
- In-game monitor text (was the big open problem): see "The monitor problem" below.
- Tablet, including all its pages, and the ALT / focus modes.
- **Motion controllers** (Quest 3 tested; other controllers via SteamVR's binding screen):
  - A laser per hand; the last hand to pull its trigger is the active pointer. Trigger clicks; hold the
    trigger and move the hand to turn dials, move sliders and valves, and step the 3-position switches.
  - Left stick walks (in the direction the head faces), right stick turns smoothly.
  - Right grip is the right mouse button (component information, switch covers, zoom); left grip is time control (T).
  - Buttons map to keys (defaults: A flashlight, B tablet, X jump and Enter, Y menu, left stick click run,
    right stick click Geiger counter, both stick clicks together the inventory). Holding both triggers for
    2 seconds recentres the view (the End key also does).
- **Crane seat:** the buttons change (A grabber, B laser sight, Y next camera, X leave, right stick up/down moves the
  grabber). The crane's outside camera views are shown in the headset.
- **Inventory:** the game's ring of 3D crates and its 2D buttons show on the virtual screen; dragging spins the ring.

Known issues:
- **Use a new save.** The mod has not been tested on existing saves.
- **Chemical systems are untested** (only played with them switched off).
- If your head or hands end up in a strange position or orientation (usually after using a keypad or a
  ladder), press B to open the tablet and press it again to close it. Your view will recenter.
- **CCTV cameras** are switched off in VR (the game crashed the graphics driver): see the last section.
- The water is the game's flat water surface in VR (the fluid simulation is switched off, see below): it shows the
  right level but has no waves or splashes.
- The inventory is a flat picture, not 3D.

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

## Hotkeys (Ctrl+Shift+..., diagnostics only, safe to ignore)

| Key | What |
|---|---|
| End (no modifiers) | Recenter the headset (also: hold both triggers for 2 seconds) |
| B | Benchmark: applies candidate performance settings one by one and logs SteamVR GPU time for each ([bench]) |
| V | View probe: groups what is drawn in the current view (by type, shader, distance band, size), hides each group in turn and logs the GPU time ([probe]) |

(The experiment hotkeys used to find the monitor-text and lighting problems have been removed; they are in git history.)

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

How it was found (experiment code since removed, see git history): a hotkey forced text on top (unlit, no depth
test), which fixed it and proved it was a depth-ordering problem; the queue-only variant
is the gentle version that became the fix. Earlier experiments that changed nothing (emission, depth precision, near/far,
unlit text, glass, text depth test) were all sound tests of the wrong things; the depth-test one never
applied because the lit text shader has no `unity_GUIZTestMode` property.

## Performance: what was found and what the mod does

Tools: the `[perf]` log line (every 5 s: game fps, SteamVR GPU ms, reprojection) and the B and V hotkeys.
Measure with SteamVR's GPU time (`Compositor_FrameTiming.m_flTotalRenderGpuMs`), not fps: SteamVR's motion
smoothing snaps a frame that is a little over budget to exactly half rate (45 at 90 Hz).

- **VSync** (the game's option) made the game wait for the monitor as well as the headset (stuck at 30).
  Turned off while VR runs, restored afterwards.
- **The cost is almost entirely the two eye views** (about 1.5 ms per eye in a light spot, 10 ms in a heavy
  one; everything else in the game is about 1.5 ms). It was independent of resolution, MSAA, water,
  volumetric lights and pixel lights: it is vertex/draw-call bound, so lower resolution only made it blurry
  (an adaptive-resolution feature was built and removed for that reason).
- **The game's own camera** rendered the whole scene again for the monitor. It now draws nothing in VR
  (every layer's culling distance set to 2 cm, plus its heavy effects switched off), and the game window shows
  the left eye instead (a command buffer on that camera, before the UI overlay). Do NOT empty its culling mask:
  Unity also uses that mask for OnMouse events and UI raycasts (it made nothing clickable).
- **Eye cameras follow the game camera's clip planes** (the game changes the far plane per area every 2 s; the
  hand-built projection was stuck at 3000 m). Projection is rebuilt when they change.
- **Occlusion culling on the eyes** (on): saved roughly half the cost of a busy view.
- **Default-layer draw distance** (150 m): almost everything is on that layer; other layers keep the far plane.
- **Shadow distance 60 m** in VR (game default 200): each eye re-draws all geometry in range into its shadow map.
- **Far small objects hidden** (under 12 m across, nearest point over 100 m, and also under 0.6 degrees on screen):
  the worst views showed about 3,000 such objects over 100 m away costing half the frame. Cached bounds (re-read
  before hiding, since some objects had moved), re-checked in slices, `forceRenderingOff`. The angle test keeps big
  things such as pipes and pump bodies drawn: the plant is larger than it looks, and a plain 100 m cut-off hid them.
- **Water reflection** (`NVWaterShaders.OnWillRenderObject`) renders an extra mirrored scene pass per camera
  that sees the water: the right eye reuses the left eye's, refreshed every 2nd frame.
- **Water simulation (ZibraAI):** the game's fluid simulation crashed the game at random in VR (a null read inside
  `ZibraLiquidNative_Win.dll`, which keeps ONE set of GPU textures shared by all cameras; the same crash location
  turned up in the CCTV crashes), and it looked different in each eye and was drawn over the lasers and menu. While VR
  runs the mod switches the game to its own flat water surface, as if the "advanced water simulation" option were
  off (`CConfiguracion.COpciones.AguaSimulada` and its private `ActivarAguaSimulada`, see `VrManager.Water.cs`); the
  player's setting is restored when VR stops. The eye cameras are also marked `CameraType.VR`, which the simulation
  skips.

## Motion controllers: how it works

- **Input:** SteamVR Input. `src/NuclearesVR/Input/*.json` (action manifest and default Oculus Touch
  bindings) are copied next to the DLL. Remap or add other controllers in SteamVR's binding screen for
  "Nucleares VR". Poses use the `pose/tip` path; `PointerPitchDegrees` in the config tilts the pointer.
- **Pointing and clicking** (`VrManager.Pointing.cs`): everything interactive in the game is Unity's
  `OnMouseEnter/Down` (a raycast from the camera through the mouse position). So the OS cursor is held at
  the window centre, the game camera is moved to the active controller at the end of each frame (after
  rendering) so Unity's mouse handling at the start of the next frame aims from there, and it is put back
  before the game's scripts run. The trigger sends a real left mouse button press.
- **Dragging:** while the trigger is held, `Input.mousePosition` is offset by the hand's movement
  (dead zone 1.5 cm, `DragPixelsPerMeter`), which all the dials, sliders and valves read.
- **Keys and turning:** Harmony postfixes on `Input.GetKey/GetKeyDown/GetKeyUp`, `GetAxis(Raw)` ("Mouse X"
  for turning), `Input.mousePosition` and `Interface.CTeclas.SinPosicionar` (the instant-activation
  shortcut, "hold shift to skip the hand animation") in `VrKeys.cs`. Edges are stamped with the frame the
  game will read them in.
- **Walking where the head faces:** while moving, the body is turned to the head's heading and the same
  angle is removed from the head mapping (`_yawAdjust`) so the view does not change.
- **Menu:** the ray is intersected with the virtual screen quad and converted to a desktop pixel for the
  OS cursor.
- **Sticky drag:** the game's 3-position switches step one way or the other on ANY change of the horizontal
  mouse position, so the hand offset trails the hand by the dead zone (it only follows once the hand is more than
  1.5 cm past the last value), and the real cursor position is replaced by "window centre + offset" while dragging
  (the game locks and unlocks the cursor around switch steps, which moves the real one).
- **Crane:** while the player is in the crane seat (`controlCrane._jugador`), buttons send the crane's own keys
  (Z, F, Space, X) and the right stick sends the game's Up/Down keys. An outside camera view puts the headset at
  the crane's camera (re-applied just before each eye renders). Body-turning towards the head and the lasers are off.
- **Dialogs and the tutorial:** confirmation dialogs and pop-up help (`Interface.IsHayMenuEnPantalla`) and the
  inventory get the virtual screen; the tutorial box (`Interface.CAvisos.Tutorial`) is a panel cropped from the
  same screen capture, like the information panel.
- **Inventory:** it uses a dedicated camera (`controlCamaras.CamMochila`, layers 6 and 9) for its 3D crates, which
  are a real object in the room, so the eye cameras stop drawing those layers while it is open and the flat
  capture (which still contains them) is shown on the virtual screen. Dragging feeds the "Mouse X" axis.
- **Both stick clicks / both triggers:** the inventory key is pulsed when the second stick joins; the right stick's
  key (Geiger) is pulsed on release so the two-stick press does not also fire it.
- Config: `BepInEx/config/com.mjh.nuclearesvr.cfg` (button-to-key mapping, turn speed, drag scale, menu screen
  size and position, performance settings, ...).

Ideas not done: haptics, snap turning, a comfort vignette, hands/arms model, tablet-specific pointing, a 3D inventory,
Steam Frame default bindings (only Oculus Touch defaults are written).

## Project layout

```
src/NuclearesVR/            plugin source (partial class VrManager split by concern)
  Plugin.cs                  BepInEx entry point
  Vr/OpenVR.cs               Valve's official C# bindings (vendored)
  Vr/VrMath.cs               OpenVR <-> Unity conversions
  Vr/VrManager*.cs           stereo rendering, tracking, virtual screen, pointing and input, crane, inventory,
                             performance (culling, shadows, far objects), water and monitor workarounds, diagnostics
  Vr/VrKeys.cs               Harmony patches that feed controller input into the game's Input reads
  Vr/VrWaterPatches.cs       shared water reflection; VrManager.Water.cs switches to flat water; Vr/VrSecurityPatches.cs blocks the CCTV cameras
docs/CCTV-INVESTIGATION.md   everything tried on the CCTV crash
scripts/setup-dependencies.ps1  scripts/make-release.ps1 (builds dist/NuclearesVR-<version>.zip)
reference/  lib/  downloads/ gitignored (game code, game/BepInEx DLLs, downloaded archives)
```

## Known issue: CCTV cameras in VR

Switching the in-game CCTV on froze or crashed the game in VR (the graphics driver failed while the CCTV
cameras drew beside the two headset views), though not in 2D. Many attempts did not give a reliable fix, so
`SecurityCameras` defaults to false: the game does not draw those cameras in VR and the CCTV screens stay black.
Everything that was tried and learned is in [docs/CCTV-INVESTIGATION.md](docs/CCTV-INVESTIGATION.md).
