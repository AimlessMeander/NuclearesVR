using System.Collections.Generic;
using System.Linq;
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
                var refresh = AccessTools.Method(typeof(controlVideoVigilancia), "GetRefresco");
                if (refresh != null)
                {
                    harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RefreshPostfix)));
                }
                var plane = AccessTools.Method(typeof(ControlReservorioDeAgua), "SetPlanoDeAguaVisible");
                if (plane != null)
                {
                    harmony.Patch(plane, prefix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(WaterPlanePrefix)));
                }
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderPrefix)),
                    postfix: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderPostfix)),
                    finalizer: new HarmonyMethod(typeof(VrSecurityPatches), nameof(RenderFinalizer)));
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

        private static int _renderLogCount, _finishedCount;
        private static readonly HashSet<string> Finished = new HashSet<string>();

        /// <summary>How long the game waits between drawing the CCTV cameras: never less than the setting while VR runs.</summary>
        private static void RefreshPostfix(ref float __result)
        {
            if (VrManager.VrRunning)
            {
                __result = Mathf.Max(__result, Plugin.SecurityCameraRefreshSeconds.Value);
            }
        }

        private static bool _cheapApplied;
        private static float _savedShadowDistance, _savedLodBias;

        /// <summary>Runs even if the draw throws: puts the shadow and detail settings back, for the CCTV camera's own draw only.</summary>
        private static System.Exception RenderFinalizer(Camera __instance, System.Exception __exception)
        {
            if (_cheapApplied && __instance != null && __instance.name.StartsWith("VV_Camera"))
            {
                _cheapApplied = false;
                QualitySettings.shadowDistance = _savedShadowDistance;
                QualitySettings.lodBias = _savedLodBias;
            }
            return __exception;
        }

        private static void RenderPostfix(Camera __instance)
        {
            if (VrManager.VrRunning && __instance != null && __instance.name.StartsWith("VV_Camera") && _renderLogCount <= 12 && _finishedCount < 12)
            {
                _finishedCount++;
                Plugin.Logger.LogInfo($"[cctv] render #{_finishedCount} of '{__instance.name}' finished at t={Time.realtimeSinceStartup:F2}");
            }
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
            if (Plugin.SecurityCameraCheapRender.Value)
            {
                // Just for this camera's draw: no shadows and lower-detail models. A view of the turbine
                // hall holds about 20 million vertices, and every shadow-casting light draws them again.
                _savedShadowDistance = QualitySettings.shadowDistance;
                _savedLodBias = QualitySettings.lodBias;
                _cheapApplied = true;
                QualitySettings.shadowDistance = 0f;
                QualitySettings.lodBias = Mathf.Min(_savedLodBias, 0.4f);
            }
            if (_renderLogCount < 12)
            {
                _renderLogCount++;
                Plugin.Logger.LogInfo($"[cctv] render #{_renderLogCount}: '{__instance.name}' at frame {Time.frameCount}, t={Time.realtimeSinceStartup:F2}");
            }
            if (Logged.Add(__instance.name))
            {
                var target = __instance.targetTexture;
                Plugin.Logger.LogInfo($"[cctv] first render of '{__instance.name}': target={(target != null ? $"{target.width}x{target.height} aa{target.antiAliasing} depth{target.depth} {target.format}" : "none")}, " +
                                      $"mask={__instance.cullingMask}, path={__instance.actualRenderingPath}, hdr={__instance.allowHDR}, msaa={__instance.allowMSAA}");
                Plugin.Logger.LogInfo($"[cctv]   {Describe(__instance)}");
                try
                {
                    DescribeView(__instance);
                }
                catch (System.Exception ex)
                {
                    Plugin.Logger.LogWarning($"[cctv] view description failed: {ex.Message}");
                }
            }
            return true;
        }

        /// <summary>What the camera is about to draw: renderers in its view grouped by shader, and lights in range.</summary>
        private static void DescribeView(Camera camera)
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var position = camera.transform.position;
            var far = camera.farClipPlane;
            var shaders = new Dictionary<string, int>();
            var meshes = 0;
            long vertices = 0;
            var skinned = 0;
            var particles = 0;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if ((camera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                {
                    continue;
                }
                var bounds = renderer.bounds;
                if (bounds.SqrDistance(position) > far * far || !GeometryUtility.TestPlanesAABB(planes, bounds))
                {
                    continue;
                }
                meshes++;
                if (renderer is SkinnedMeshRenderer) skinned++;
                if (renderer is ParticleSystemRenderer) particles++;
                var material = renderer.sharedMaterial;
                var name = material != null && material.shader != null ? material.shader.name : "(none)";
                shaders[name] = shaders.TryGetValue(name, out var count) ? count + 1 : 1;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) vertices += filter.sharedMesh.vertexCount;
            }
            Plugin.Logger.LogInfo($"[cctv]   view: {meshes} renderers ({skinned} skinned, {particles} particle), {vertices:N0} vertices; shaders: " +
                                  string.Join(", ", shaders.OrderByDescending(pair => pair.Value).Take(14).Select(pair => $"{pair.Key} x{pair.Value}").ToArray()));

            var lightCount = 0;
            var shadowed = 0;
            var typeCounts = new Dictionary<LightType, int>();
            foreach (var light in Object.FindObjectsOfType<Light>())
            {
                if (light == null || !light.enabled || !light.gameObject.activeInHierarchy || light.intensity <= 0f)
                {
                    continue;
                }
                if (light.type != LightType.Directional && (light.transform.position - position).sqrMagnitude > (far + light.range) * (far + light.range))
                {
                    continue;
                }
                lightCount++;
                if (light.shadows != LightShadows.None) shadowed++;
                typeCounts[light.type] = typeCounts.TryGetValue(light.type, out var n) ? n + 1 : 1;
            }
            Plugin.Logger.LogInfo($"[cctv]   lights in range: {lightCount} ({shadowed} with shadows): " +
                                  string.Join(", ", typeCounts.Select(pair => $"{pair.Key} x{pair.Value}").ToArray()) +
                                  $"; pixelLightCount={QualitySettings.pixelLightCount}, shadowDistance={QualitySettings.shadowDistance:F0}, shadowResolution={QualitySettings.shadowResolution}, cascades={QualitySettings.shadowCascades}");
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
