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
        private bool _mainMaskStripped;
        private int _strippedBits;

        // Layers that must never be hidden from the main camera (Default, UI): if the simulation were
        // on one of these, hiding it would blank the monitor view and the menus.
        private const int NeverStripLayers = (1 << 0) | (1 << 5);

        // ---- correct view rays for the headset cameras ----
        // The simulation builds its per-pixel view rays from the camera's field of view and aspect ratio,
        // assuming an ordinary symmetric camera. The headset cameras use a custom off-centre frustum
        // (set through projectionMatrix), so those rays were wrong and the water looked off in VR. For
        // our cameras, build the rays from the real projection matrix instead.
        private static bool _liquidRaysPatched;

        private static void PatchLiquidEyeRays(Type liquidType)
        {
            if (_liquidRaysPatched)
            {
                return;
            }
            _liquidRaysPatched = true;
            try
            {
                var original = HarmonyLib.AccessTools.Method(liquidType, "CalculateEyeRayCameraCoeficients", new[] { typeof(Camera) });
                if (original == null)
                {
                    Plugin.Logger.LogWarning("Water simulation: CalculateEyeRayCameraCoeficients not found - water may look off in VR.");
                    return;
                }
                var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".liquid");
                harmony.Patch(original, prefix: new HarmonyLib.HarmonyMethod(typeof(VrManager), nameof(EyeRayPrefix)));
                Plugin.Logger.LogInfo("Water simulation: view rays for the headset cameras now use the real projection.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Water simulation ray patch failed: {ex}");
            }
        }

        private static bool EyeRayPrefix(Camera cam, ref Matrix4x4 __result)
        {
            if (cam == null || !cam.name.StartsWith("NuclearesVR_"))
            {
                return true; // the game's own cameras: unchanged
            }
            var p = cam.projectionMatrix;
            var halfWidth = 1f / p[0, 0];
            var halfHeight = 1f / p[1, 1];
            var centreX = p[0, 2] / p[0, 0];
            var centreY = p[1, 2] / p[1, 1];
            var t = cam.transform;
            var right = t.right * halfWidth;
            var down = -t.up * halfHeight;
            var centre = t.forward + t.right * centreX + t.up * centreY;
            __result = new Matrix4x4(
                new Vector4(right.x, right.y, right.z, 0f),
                new Vector4(down.x, down.y, down.z, 0f),
                new Vector4(centre.x, centre.y, centre.z, 0f),
                Vector4.zero).transpose;
            return false;
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
                    PatchLiquidEyeRays(_liquidType);
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
        }

        /// <summary>Called every frame after the eye cameras have copied the main camera's culling mask.</summary>
        private void ApplyLiquidMask()
        {
            if (!LiquidVisibleInEyes || _liquidLayerMask == 0 || _mainCamera == null)
            {
                RestoreMainMask();
                return;
            }
            _mainCamera.cullingMask &= ~_liquidLayerMask;
            _mainMaskStripped = true;
            _strippedBits = _liquidLayerMask;
        }

        private void RestoreMainMask()
        {
            if (_mainMaskStripped && _mainCamera != null)
            {
                _mainCamera.cullingMask |= _strippedBits;
            }
            _mainMaskStripped = false;
        }
    }
}
