# CCTV cameras in VR: what was tried and what is known

Status: **unsolved**. Switching the in-game CCTV system on freezes or crashes the game in VR. It works in 2D
(with the mod installed, `VrMode = Never`). The mod now stops the game from drawing the CCTV cameras in VR
(`SecurityCameras = false`, the default), so the CCTV screens stay black and the game stays stable.

Test machine: RTX 4090 (24 GB), Quest 3 over Steam Link, SteamVR, Unity 2022.3.9 (Mono, D3D11, built-in
pipeline), Windows 11. The investigation code is in git history (commits `511ad2a` to `14cb5cd`); the last
commit before the cleanup that removed it is `17c102b`.

## How the game's CCTV works (from the decompiled `controlVideoVigilancia`)

- Twelve cameras named `VV_Camera_CORE_ROOM`, `..._CORE_INSIDE`, `..._TURBINE_ROOM`, `..._CONDENSER`,
  `..._TRANSF`, `..._FUEL_ROOM`, `..._CHEMICAL`, `..._INTERNAL_SUPPLY`, `..._GENSET_ROOM`, `..._TOWERS`,
  `..._LOADING_STATION`, `..._PROVIDER_ACCESS`. Their `Camera` components are disabled.
- A coroutine (`ActivarCamara`) draws them by hand: `Camaras[n].targetTexture = RenderTextures[k]` (a 512x512
  ARGB32 texture, no depth, "RenderCamaraVigilancia N") then `Camaras[n].Render()`, then waits
  `WaitForSeconds(GetRefresco())` (about 0.3 s at normal quality; longer when the game's own FPS is low, see
  `Rendimiento.Nivel`). In multi-view it cycles through the cameras.
- For cameras in the core sector it also turns on a plain water plane in the pool (`SetPlanoDeAguaVisible`) and,
  when the dynamic light quality option is on, enables a realtime light attached to the camera
  (`CanaraDeVideovigilancia.LuzRealtimeAsociada`) just for that draw.
- Each camera: Deferred path, no HDR, no MSAA, far clip 400 (some less), FOV 57 to 101, culling mask
  32237523, a Post-Processing `PostProcessLayer` (which adds four command buffers), and the small
  `ExcluirCameraDeControles` and `CanaraDeVideovigilancia` scripts.

## What the failure looks like

- Unity `Player.log`: `D3D11: Failed to create RenderTexture (...) error 0x887a0005` repeated, then `Crash!!!`
  (0x887a0005 = graphics device removed). Sometimes instead a crash inside the NVIDIA driver
  (`nvwgf2umx`) on a driver worker thread, with no game error before it.
- Sometimes no crash but a freeze: SteamVR shows its "Waiting..." screen, the main thread stuck inside a
  `Camera.Render` call (the log shows "render #N" with no matching "finished").
- Sometimes a permanent slowdown to about 2 fps in the headset: every second frame takes about 1000 ms,
  `WaitGetPoses` takes 0 ms, and the GPU is idle as SteamVR sees it. It stayed slow after the CCTV was switched off.
- fpsVR shows SteamVR's compositor rate (about 21 to 90 fps) while the game itself is at 2 fps. Trust the mod's
  `[perf]` log lines for the game's real rate.
- Latest Unity crash reports: `%TEMP%\Aerilian\Nucleares\Crashes\` (`crash.dmp`, not analysed).

## What was tried (in order) and the result

| # | Change | Result |
|---|---|---|
| 1 | Remove the water simulation's layer (ZibraAI liquid) from the CCTV cameras' culling masks | Still crashed |
| 2 | Mark the CCTV cameras as `CameraType.Reflection` (the water simulation skips that type outright), block the pool water plane | **No crash**, but stuck at about 2 fps |
| 3 | Same without the Reflection type, and patch the simulation's `IsCameraFiltered` so it skips `VV_Camera*` | Crash |
| 4 | Switch the post-processing layer off on the CCTV cameras | Picture appeared, then froze or crashed |
| 5 | Skip drawing the core cameras (`SecurityCamerasSkipped`) | Core cameras fine; any other camera crashed |
| 6 | Turn the water simulation off in the headset, or use `MonitorView = Game`, or far-object hiding off and shadow distance back to 200 | Each still crashed |
| 7 | "Light mode": Forward instead of Deferred, far clip 150 m, no shadows from the camera's extra light | Core camera drew 12 times at a steady 90 fps; turbine hall then crashed on its first draw; fuel depot ran 6 draws then fell to a permanent 2 fps |
| 8 | Hide the game's reflective water planes (`NVWaterShaders`) from the CCTV cameras | Log: 0 such planes are active in this scene, so this changed nothing |
| 9 | Per-draw `QualitySettings.shadowDistance = 0` and `lodBias = 0.4` | Turbine hall froze in its 3rd draw |
| 10 | Minimum 1.5 s between CCTV draws | Got through all 12 cameras once, but at 2 fps; another run: cameras 4 to 7 at 90 fps, then camera 8 crashed |
| 11 | Per-camera vertex budget (shorten the far clip until the view has under 8 million vertices) | Crashed on the condenser camera (only 1.5 million vertices) |

## Measurements from the logs

- Vertices in view at 150 m (CPU-side count, overcounts LOD levels): transformers 0.19 M, fuel depot 0.62 M,
  condenser 1.55 M, chemical room 9.7 M, turbine hall 19.7 M, internal supply room 44.8 M (4,722 renderers).
  Crashes did **not** follow this cleanly (the condenser at 1.5 M crashed once; the chemical room at 9.7 M ran).
- 5 lights in range in the turbine hall (2 directional, 3 point), 4 with shadows; `pixelLightCount = 2`.
- Graphics memory was not the cause: texture memory 3301 MB flat, VRAM about 10.3 of 23.6 GB, render texture
  count 107 before and 146 after switching the CCTV on, then constant.
- The mod's own frame parts: `WaitGetPoses` took 0 to 20 ms; nothing on the mod side blocked.
- The game does not cap the frame rate (`Application.targetFrameRate = -1`).

## Ruled out or unlikely

- The water simulation registering the CCTV cameras (excluded three ways; crashes continued).
- Post-processing on the CCTV cameras, shadows, water planes, memory, a vertex-count threshold, refresh rate alone.
- The mod's frame pacing (`WaitGetPoses`, `Submit`).

## Ideas not tried yet

- Do not use the game's cameras: render a copy of each camera ourselves, in our own step (for example right
  after `Submit`, when the eye textures are idle), at a low rate, with a fixed cheap setup.
- Check whether the failure needs the eye cameras' MSAA 4x textures (try `EyeMsaa = 1` while testing).
- Unity launch options to test: `-force-d3d11-singlethreaded`, `-disable-gfx-jobs`, `-force-d3d11-no-singlethreaded`,
  or `-force-vulkan` (needs a check that the mod's texture submission supports Vulkan; it currently assumes D3D11).
- SteamVR side: turn motion smoothing / async reprojection off while testing; try another NVIDIA driver version.
- Windows Event Viewer (Windows Logs > System) for "Display driver nvlddmkm stopped responding" (Event 4101) to
  confirm a Windows GPU timeout (TDR), and its registry setting `TdrDelay` to test with a longer timeout.
- Analyse `crash.dmp` from the Crashes folder with WinDbg for the exact faulting call.
- Ask whether the same crash happens with other SteamVR games that render extra cameras, or with the game's
  `Rendimiento` (dynamic performance) option switched off in the game's own settings.

## If picking this up again

Set `SecurityCameras = true` in the config to let the game draw the cameras in VR. Then the diagnostics that were
removed would be re-added from git history: the `[cctv]` first-draw and view-content logging
(`VrSecurityPatches.cs` at commit `14cb5cd`) are the most useful, plus the per-frame stall breakdown in
`VrManager.cs` at commit `722311c`.

## Update: the crash signature (2026-09-26)

Reading the crash dumps (`%TEMP%\Aerilian\Nucleares\Crashes\Crash_*\crash.dmp`; exception record found with a small
minidump parser) of that day's crashes, including the CCTV ones, showed two repeating signatures: an access
violation (null read) at offset `0x3423c` in `ZibraLiquidNative_Win.dll` (the game's water simulation), and faults in
the NVIDIA driver (`nvwgf2umx.dll`). The same Zibra offset also crashed the game with the CCTV blocked and nothing
else unusual happening. So the CCTV crash may really have been this Zibra crash, with the CCTV cameras only a reliable
way to trigger it. Since 0.1.1 the mod switches the game to its flat water while VR runs (`VrManager.Water.cs`).
If the CCTV is ever retried, do it with the water simulation off first. Not yet tested.
