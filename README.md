# NuclearesVR

An unofficial VR mod for [Nucleares](https://store.steampowered.com/app/1428420) (Unity 2022.3.9f1,
Mono scripting backend), built as a [BepInEx](https://github.com/BepInEx/BepInEx) plugin using the
OpenVR SDK directly (the game ships with no built-in XR support, so there's no Unity XR pipeline to
hook into - this mod does its own stereo rendering and pose tracking, the same approach tools like
[UUVR](https://github.com/Raicuparta/uuvr) use for other unmodified Unity titles).

## Status

**Phase 1: 6DoF head tracking - implemented, not yet tested on hardware.**

- Stereo rendering: two extra cameras are attached to the game's existing player camera, each
  rendering to its own render texture sized to the headset's recommended resolution and submitted
  to the SteamVR compositor every frame.
- Head tracking: the HMD's position and orientation are applied *on top of* whatever the game's own
  mouse-look already set that frame (see "Why additive, not a replacement" below), so mouse/gamepad
  turning and the game's "focus on this gauge" animation both keep working unmodified.
- Graceful fallback: if no headset/SteamVR runtime is present, the mod logs that and does nothing
  further - the game plays exactly as normal.
- Recenter: press **End** to re-zero the seated/standing calibration point.

**Phase 2: motion controllers - not started.** See "Roadmap" below for what's involved.

## Why additive, not a replacement

`PlayerLook.cs` (the game's look script) recomputes the camera's local rotation from scratch every
frame from its own internal yaw/pitch state - it doesn't read back whatever the transform currently
holds. That means we can multiply our own head-tracking delta onto `camera.transform.localRotation`
in `LateUpdate` (which Unity guarantees runs after every `Update` this frame) without fighting the
game's own logic or needing any Harmony patches to disable it. This also means the game's own
"look at this specific object" focus mode (`PlayerLook.Mirar`, used when examining gauges up close)
keeps working - VR head movement just layers on top of it.

## Project layout

```
src/NuclearesVR/          the plugin source
  Plugin.cs                BepInEx entry point
  Vr/OpenVR.cs              Valve's official OpenVR C# bindings (vendored, not modified)
  Vr/VrMath.cs              OpenVR <-> Unity matrix/pose conversions
  Vr/VrManager.cs           stereo rendering + head tracking
reference/                 decompiled Assembly-CSharp.dll, for our own analysis only
                            (gitignored - it's the game's own copyrighted code)
lib/                       build-time references: game DLLs + BepInEx + OpenVR
                            (gitignored - see scripts/setup-dependencies.ps1)
scripts/setup-dependencies.ps1   regenerates lib/ from your local game install
```

## Building

```powershell
# One-time, or after a game update changes its DLLs:
powershell -File scripts/setup-dependencies.ps1

dotnet build src/NuclearesVR/NuclearesVR.csproj
```

The build automatically copies `NuclearesVR.dll` and `openvr_api.dll` into
`<game>/BepInEx/plugins/NuclearesVR/` (see the `DeployToGame` target in the `.csproj` - edit the
`GameDir` property there if your install path differs from
`E:\Programs\Steam\steamapps\common\Nuclear Last Darkness`).

## Testing

1. Have SteamVR running with your headset connected (this targets OpenVR/SteamVR, so a Quest needs
   Link or Virtual Desktop with SteamVR set as the OpenXR/OpenVR runtime).
2. Launch Nucleares normally through Steam.
3. Check `<game>/BepInEx/LogOutput.log` for `NuclearesVR` lines - it logs whether it found a
   headset, the render target size it picked, and any errors.
4. If SteamVR shows a frozen/black view: check the log for the Direct3D11 warning - the frame
   submission path currently assumes Unity is running on D3D11 (the default on Windows), and would
   need adjusting for other graphics APIs.

I couldn't launch the game myself to test this end-to-end (this sandbox can't attach to your
desktop session to run GUI apps), so this first pass is untested against real hardware - expect to
iterate on it together once you can put the headset on.

## What was already here

The game folder had a stale `doorstop_config.ini`/`winhttp.dll` from an earlier attempt to use
[Rai Pal](https://github.com/Raicuparta/rai-pal) to install a VR mod, pointing at a BepInEx install
under `%APPDATA%\raicuparta\...` that no longer exists on this machine. That's been replaced with a
clean, self-contained BepInEx 5.4.23.5 install inside the game folder itself.

## Roadmap: Phase 2, motion controllers

This is the harder half. Key findings from decompiling `Assembly-CSharp.dll`:

- Every interactable (switches, valves, dials - `ObjetoInteractuable.cs`) already has a `HandTarget`
  and a `Interface.Manos` (left/right hand) association, plus hand-IK gesture animation toward it.
  The game already models *which hand reaches where* - we don't need to invent that, just drive it
  from real controller poses instead of the mouse-click-triggered animation.
- Interaction is currently driven by Unity's native `OnMouseDown`/`OnMouseEnter`
  (`DetectorDeClic.cs` and friends), which always raycasts from `Camera.main` through the *locked
  mouse cursor position* (i.e., a fixed reticle at screen center) - there's no way to make that
  raycast originate from a controller's position instead, since it's handled by Unity's engine, not
  script we can redirect.
- So real controller-based interaction means: our own `Physics.Raycast` (or a direct-touch check)
  from each controller's tracked pose every frame, and when it hits an `ObjetoInteractuable`,
  triggering the same effect the mouse click would have - either via reflection into the private
  `OnClic` `UnityEvent` on `DetectorDeClic`, or small per-type hooks for the drag-based controls
  (`InterruptorPalanca.cs`, `Regulador.cs`, `ReguladorVertical.cs`, `PalancaMecanica.cs`, valves)
  that currently compute drag amount from mouse delta and would need to use controller rotation/
  position delta instead.
- Networking is Photon Fusion (`PlayerLook`, `PlayerMove` are `NetworkBehaviour`s) for the co-op
  mode - camera/look and controller interaction should stay client-local and not need any
  networking changes, but this needs verifying once we're actually driving object state from VR
  input instead of the mouse.
