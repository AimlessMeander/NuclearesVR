using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

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
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderPrefix)));
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"Security camera patch failed: {ex}");
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
            if (Logged.Add(__instance.name))
            {
                var target = __instance.targetTexture;
                Plugin.Logger.LogInfo($"[cctv] first render of '{__instance.name}': target={(target != null ? $"{target.width}x{target.height} aa{target.antiAliasing} depth{target.depth} {target.format}" : "none")}, " +
                                      $"mask={__instance.cullingMask}, path={__instance.actualRenderingPath}, hdr={__instance.allowHDR}, msaa={__instance.allowMSAA}");
            }
            return true;
        }
    }
}
