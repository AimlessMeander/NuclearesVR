using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The CCTV system draws its cameras by hand (Camera.Render into small textures). Switching it on in
    /// VR crashed the graphics driver, so this logs each of those draws (the last line before a crash
    /// shows how far it got) and can switch them off while VR runs (setting SecurityCameras).
    /// </summary>
    internal static class VrSecurityPatches
    {
        private static readonly HashSet<string> Logged = new HashSet<string>();

        internal static void Apply(Harmony harmony)
        {
            try
            {
                var original = AccessTools.Method(typeof(Camera), "Render", new System.Type[0]);
                if (original == null)
                {
                    Plugin.Logger.LogWarning("Security camera patch: Camera.Render not found.");
                    return;
                }
                var plane = AccessTools.Method(typeof(ControlReservorioDeAgua), "SetPlanoDeAguaVisible");
                if (plane != null)
                {
                    harmony.Patch(plane, prefix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(WaterPlanePrefix)));
                }
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderPrefix)),
                    postfix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderPostfix)));
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"Security camera patch failed: {ex}");
            }
        }

        /// <summary>The CCTV switches a water plane on in the reactor pool for the core camera; skipped while VR runs unless allowed.</summary>
        private static bool WaterPlanePrefix(bool valor)
        {
            return !(VrManager.VrRunning && valor && !Plugin.SecurityCameraWaterPlane.Value);
        }

        private static readonly HashSet<string> Finished = new HashSet<string>();

        private static void RenderPostfix(Camera __instance)
        {
            if (VrManager.VrRunning && __instance != null && __instance.name.StartsWith("VV_Camera") && Finished.Add(__instance.name))
            {
                Plugin.Logger.LogInfo($"[cctv] first render of '{__instance.name}' finished.");
            }
        }

        private static bool RenderPrefix(Camera __instance)
        {
            if (!VrManager.VrRunning || __instance == null || !__instance.name.StartsWith("VV_Camera"))
            {
                return true;
            }
            if (!Plugin.SecurityCameras.Value)
            {
                return false;
            }
            var skipped = Plugin.SecurityCamerasSkipped.Value;
            if (!string.IsNullOrEmpty(skipped) && skipped.IndexOf(__instance.name, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            if (Logged.Add(__instance.name))
            {
                var target = __instance.targetTexture;
                Plugin.Logger.LogInfo($"[cctv] first render of '{__instance.name}': target={(target != null ? $"{target.width}x{target.height} aa{target.antiAliasing} depth{target.depth} {target.format}" : "none")}, " +
                                      $"mask={__instance.cullingMask}, path={__instance.actualRenderingPath}, hdr={__instance.allowHDR}, msaa={__instance.allowMSAA}");
                Plugin.Logger.LogInfo($"[cctv]   {Describe(__instance)}");
            }
            return true;
        }

        private static string Describe(Camera camera)
        {
            var parts = new List<string>();
            foreach (var component in camera.GetComponents<Component>())
            {
                if (component == null) continue;
                var behaviour = component as Behaviour;
                parts.Add(component.GetType().Name + (behaviour != null && !behaviour.enabled ? "(off)" : ""));
            }
            var buffers = new List<string>();
            foreach (CameraEvent cameraEvent in System.Enum.GetValues(typeof(CameraEvent)))
            {
                var count = camera.GetCommandBuffers(cameraEvent).Length;
                if (count > 0) buffers.Add(cameraEvent + "x" + count);
            }
            return $"components: {string.Join(", ", parts.ToArray())}; command buffers: {(buffers.Count > 0 ? string.Join(", ", buffers.ToArray()) : "none")}; " +
                   $"depthTextureMode={camera.depthTextureMode}, type={camera.cameraType}, pixel={camera.pixelWidth}x{camera.pixelHeight}, fov={camera.fieldOfView:F0}, far={camera.farClipPlane:F0}";
        }
    }
}
