using HarmonyLib;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's water planes can draw a planar reflection (NVWaterShaders.MirrorReflection): from
    /// OnWillRenderObject, for EVERY camera that is about to draw the water, an extra camera renders the
    /// whole scene mirrored into a texture. With two eye cameras that is two extra scene passes per
    /// frame, and in a view full of objects each pass costs about as much as a whole eye view - which
    /// is why looking at the reactor pool made the frame rate collapse.
    ///
    /// In VR the right eye reuses the left eye's reflection (the reflection texture is shared, so the
    /// right eye simply does not render its own; the small difference in viewpoint is not noticeable
    /// on water), and the reflection is refreshed only every few frames.
    /// </summary>
    internal static class VrWaterPatches
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                var original = AccessTools.Method(typeof(NVWaterShaders), "OnWillRenderObject");
                if (original == null)
                {
                    Plugin.Logger.LogWarning("Water patch: NVWaterShaders.OnWillRenderObject not found - the pool costs two extra scene passes in VR.");
                    return;
                }
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(VrWaterPatches), nameof(OnWillRenderPrefix)));
                Plugin.Logger.LogInfo("Water patch applied: reflections are shared between the eyes.");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"Water patch failed: {ex}");
            }
        }

        private static bool OnWillRenderPrefix()
        {
            if (!VrManager.VrRunning || !Plugin.ShareWaterReflection.Value)
            {
                return true;
            }
            var camera = Camera.current;
            if (camera == null)
            {
                return true;
            }
            switch (camera.name)
            {
                case "NuclearesVR_RightEye":
                    return false; // uses the reflection the left eye made
                case "NuclearesVR_LeftEye":
                    return Time.frameCount % Mathf.Max(1, Plugin.WaterReflectionEveryNthFrame.Value) == 0;
                default:
                    return true;
            }
        }
    }
}
