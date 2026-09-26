using System;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's water simulation (ZibraAI "Liquid") renders once per camera, from a callback on every
    /// camera that is about to render, and keeps ONE shared set of GPU textures for all of them, resized
    /// to suit each camera as it comes up. With the game's main camera (window size, e.g. 3840x2160)
    /// and the two eye cameras (2064x2208) all rendering every frame, those shared textures were being
    /// destroyed and recreated over and over, which is the likely cause of the random graphics-driver
    /// crashes (most often in the control room, where the simulation is active).
    ///
    /// The simulation skips a camera whose culling mask does not include the layer the simulation's
    /// GameObject is on. So while VR runs, that layer is removed from the game's main camera (which only
    /// feeds the monitor and the screen capture) and kept for the eye cameras: the simulation then only
    /// ever sees the two eye cameras, which are the same size. The water still shows in the headset; it
    /// no longer shows in the monitor mirror.
    /// </summary>
    internal partial class VrManager
    {
        private Type _liquidType;
        private bool _searchedForLiquidType;
        private int _liquidLayerMask;
        private float _nextLiquidScan;

        // Layers that must never be hidden from the main camera (Default, UI): if the simulation were
        // on one of these, hiding it would blank the monitor view and the menus.
        private const int NeverStripLayers = (1 << 0) | (1 << 5);

        // The simulation skips a camera outright when its own IsCameraFiltered says so. Make that say yes
        // for the CCTV cameras, so it never registers them or touches its shared textures for them.
        private static void PatchLiquidCameraFilter(Type liquidType)
        {
            try
            {
                var filter = HarmonyLib.AccessTools.Method(liquidType, "IsCameraFiltered", new[] { typeof(Camera) });
                if (filter == null)
                {
                    Plugin.Logger.LogWarning("Water simulation: IsCameraFiltered not found - CCTV cameras are not excluded.");
                    return;
                }
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".liquidfilter");
                harmony.Patch(filter, postfix: new HarmonyLib.HarmonyMethod(typeof(VrManager), nameof(LiquidFilterPostfix)));
                Plugin.Logger.LogInfo("Water simulation: CCTV cameras are excluded.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Water simulation camera filter patch failed: {ex}");
            }
        }

        private static void LiquidFilterPostfix(Camera cam, ref bool __result)
        {
            if (!__result && VrRunning && cam != null && cam.name.StartsWith("VV_Camera"))
            {
                __result = true;
            }
        }

        private bool LiquidStripSafe => (_liquidLayerMask & NeverStripLayers) == 0;

        /// <summary>The eye cameras are ordinary cameras (the simulation draws for them) unless the setting is off or stripping is not safe.</summary>
        private bool LiquidVisibleInEyes => Plugin.LiquidInVr.Value && LiquidStripSafe;

        private void UpdateLiquidLayers()
        {
            if (Time.unscaledTime < _nextLiquidScan)
            {
                return;
            }
            _nextLiquidScan = Time.unscaledTime + 1f;

            if (!_searchedForLiquidType)
            {
                _searchedForLiquidType = true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _liquidType = assembly.GetType("com.zibra.liquid.Solver.ZibraLiquid", throwOnError: false);
                    if (_liquidType != null)
                    {
                        break;
                    }
                }
                if (_liquidType == null)
                {
                    Plugin.Logger.LogInfo("Water simulation type not found - nothing to work around.");
                }
                else
                {
                    PatchLiquidCameraFilter(_liquidType);
                }
            }
            if (_liquidType == null)
            {
                return;
            }

            var mask = 0;
            foreach (var instance in FindObjectsOfType(_liquidType))
            {
                if (instance is Component component)
                {
                    mask |= 1 << component.gameObject.layer;
                }
            }
            if (mask != _liquidLayerMask)
            {
                _liquidLayerMask = mask;
                Plugin.Logger.LogInfo($"Water simulation objects are on layer mask {mask} " +
                                      (LiquidStripSafe ? "(hidden from the game's main camera while VR runs)." : "(Default/UI layer - cannot be hidden safely; headset cameras will skip the simulation instead)."));
            }
            HideLiquidFromSecurityCameras();
        }

        // The CCTV system draws each of its cameras by hand (Camera.Render) into small textures. The
        // simulation would resize its shared textures for every one of those, and switching the CCTV on
        // crashed the graphics driver. The game already swaps in a plain water plane for the reactor
        // camera, so the simulation is not wanted there: take its layer out of those cameras' masks.
        private static readonly System.Reflection.FieldInfo SecurityCamerasField =
            HarmonyLib.AccessTools.Field(typeof(controlVideoVigilancia), "Camaras");
        private readonly System.Collections.Generic.Dictionary<Camera, int> _securityCameraMasks = new System.Collections.Generic.Dictionary<Camera, int>();
        private readonly System.Collections.Generic.Dictionary<Camera, CameraType> _securityCameraTypes = new System.Collections.Generic.Dictionary<Camera, CameraType>();

        private void HideLiquidFromSecurityCameras()
        {
            try
            {
                var system = controlVideoVigilancia.Instancia;
                if (system == null || SecurityCamerasField == null || _liquidLayerMask == 0 || !LiquidStripSafe)
                {
                    return;
                }
                var cameras = SecurityCamerasField.GetValue(system) as Camera[];
                if (cameras == null)
                {
                    return;
                }
                foreach (var camera in cameras)
                {
                    // The simulation ignores Reflection-type cameras before it does anything at all with
                    // them (it still notes every other camera's size even when its layer is masked out).
                    if (camera != null && Plugin.SecurityCameraReflectionType.Value && camera.cameraType != CameraType.Reflection)
                    {
                        _securityCameraTypes[camera] = camera.cameraType;
                        camera.cameraType = CameraType.Reflection;
                        Plugin.Logger.LogInfo($"[cctv] security camera '{camera.name}' marked as a reflection-type camera.");
                    }
                    if (camera != null && (camera.cullingMask & _liquidLayerMask) != 0)
                    {
                        if (!_securityCameraMasks.ContainsKey(camera))
                        {
                            _securityCameraMasks[camera] = camera.cullingMask;
                        }
                        camera.cullingMask &= ~_liquidLayerMask;
                        Plugin.Logger.LogInfo($"[cctv] water simulation hidden from security camera '{camera.name}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogThrottled("cctv-mask-error", $"Security camera mask error: {ex.Message}");
            }
        }

        private void RestoreSecurityCameraMasks()
        {
            foreach (var pair in _securityCameraMasks)
            {
                if (pair.Key != null)
                {
                    pair.Key.cullingMask = pair.Value;
                }
            }
            _securityCameraMasks.Clear();
            foreach (var pair in _securityCameraTypes)
            {
                if (pair.Key != null)
                {
                    pair.Key.cameraType = pair.Value;
                }
            }
            _securityCameraTypes.Clear();
        }
    }
}
