using HarmonyLib;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's CCTV system draws its twelve cameras by hand (Camera.Render into small textures). In VR,
    /// switching it on froze or crashed the game (the graphics driver failed while those cameras drew beside
    /// the headset views) and no reliable fix was found, so those draws are skipped while VR runs and the
    /// CCTV screens stay black. See docs/CCTV-INVESTIGATION.md for everything that was tried.
    /// </summary>
    internal static class VrSecurityPatches
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                var render = AccessTools.Method(typeof(Camera), "Render", new System.Type[0]);
                if (render == null)
                {
                    Plugin.Logger.LogWarning("Security camera patch: Camera.Render not found - the CCTV may crash the game in VR.");
                    return;
                }
                harmony.Patch(render, prefix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderPrefix)));
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"Security camera patch failed: {ex}");
            }
        }

        private static bool RenderPrefix(Camera __instance)
        {
            if (VrManager.VrRunning && !Plugin.SecurityCameras.Value && __instance != null && __instance.name.StartsWith("VV_Camera"))
            {
                return false;
            }
            return true;
        }
    }
}
