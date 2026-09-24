using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// On-demand dump of everything relevant to "why doesn't this light show
    /// up in the headset": rendering settings, every enabled camera, and every
    /// active light near the player with the properties that decide whether
    /// and how it affects a given camera. Trigger with Ctrl+Shift+L while the
    /// thing in question is on (torch lit, standing near an active alarm).
    /// </summary>
    internal partial class VrManager
    {
        private void CheckDiagnosticsKey()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.L))
            {
                DumpLightingState();
            }
            if (Input.GetKeyDown(KeyCode.R))
            {
                ForwardOnEyes = !ForwardOnEyes;
                Plugin.Logger.LogInfo($"Eye cameras now render in {(ForwardOnEyes ? "Forward" : "the main camera's path (Deferred)")}.");
            }
            if (Input.GetKeyDown(KeyCode.T))
            {
                UnlitMonitorText = !UnlitMonitorText;
                Plugin.Logger.LogInfo($"Monitor text is now {(UnlitMonitorText ? "unlit" : "lit, as the game has it")}.");
            }
            if (Input.GetKeyDown(KeyCode.M))
            {
                DumpMaterialUnderView();
            }
            if (Input.GetKeyDown(KeyCode.K))
            {
                StripSpotShadows = !StripSpotShadows;
                Plugin.Logger.LogInfo($"Spot light shadows are now {(StripSpotShadows ? "stripped" : "left as the game sets them")}.");
            }
        }

        // Off by default. Forward rendering on the eye cameras fixes the torch
        // and alarm lights too, but makes the in-game monitors go dark except
        // where the torch shines on them. Ctrl+Shift+R flips it, to compare.
        internal static bool ForwardOnEyes = true;

        /// <summary>
        /// Ctrl+Shift+M: describes the renderer straight ahead of where you're
        /// looking - its shaders, keywords, emission settings and which pass
        /// types exist - to work out why a surface (an in-game monitor) looks
        /// different under Forward than Deferred.
        /// </summary>
        private void DumpMaterialUnderView()
        {
            try
            {
                if (_mainCamera == null)
                {
                    return;
                }
                var ray = new Ray(_mainCamera.transform.position, _mainCamera.transform.forward);
                // Renderers whose bounds the view ray passes through, nearest
                // first - not colliders: a raycast landed on an invisible
                // player-limit collider (TopeJugador/Tope) instead of the
                // monitor. Bounds are coarse, so thin things behind a big
                // wall can show up too; the nearest few are what to read.
                var candidates = new List<KeyValuePair<float, Renderer>>();
                foreach (var candidate in FindObjectsOfType<Renderer>())
                {
                    if (!candidate.enabled || !candidate.gameObject.activeInHierarchy || candidate.gameObject.layer == _mirrorLayer)
                    {
                        continue;
                    }
                    if (candidate.bounds.IntersectRay(ray, out var distance) && distance < 15f)
                    {
                        candidates.Add(new KeyValuePair<float, Renderer>(distance, candidate));
                    }
                }
                candidates.Sort((a, b) => a.Key.CompareTo(b.Key));
                Plugin.Logger.LogInfo($"=== Material dump: {candidates.Count} renderers along the view ray; nearest 8 ===");
                foreach (var candidate in candidates.Take(8))
                {
                    Plugin.Logger.LogInfo($"  {candidate.Key:F2}m {Path(candidate.Value.transform)}");
                }
                var renderers = candidates.Take(4).Select(c => c.Value).ToArray();
                foreach (var r in renderers)
                {
                    Plugin.Logger.LogInfo($"renderer {Path(r.transform)} type={r.GetType().Name} layer={r.gameObject.layer} " +
                                          $"receiveShadows={r.receiveShadows} lightmapIndex={r.lightmapIndex} probes={r.lightProbeUsage}");
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null)
                        {
                            continue;
                        }
                        var instance = r.materials.FirstOrDefault(x => x != null && x.name.StartsWith(m.name));
                        var keywords = string.Join(" ", (instance ?? m).shaderKeywords);
                        Plugin.Logger.LogInfo($"  material '{m.name}' shader='{m.shader.name}' queue={m.renderQueue} passes={m.passCount} " +
                                              $"FORWARD={m.FindPass("FORWARD")} FORWARD_DELTA={m.FindPass("FORWARD_DELTA")} " +
                                              $"DEFERRED={m.FindPass("DEFERRED")} META={m.FindPass("META")} " +
                                              $"giFlags={m.globalIlluminationFlags} keywords=[{keywords}]");
                        foreach (var prop in new[] { "_Color", "_EmissionColor", "_Metallic", "_Glossiness", "_Cutoff" })
                        {
                            if (m.HasProperty(prop))
                            {
                                Plugin.Logger.LogInfo($"    {prop} = {((instance ?? m).GetColor(prop) is var c && prop.EndsWith("Color") ? c.ToString() : (instance ?? m).GetFloat(prop).ToString())}");
                            }
                        }
                        foreach (var prop in new[] { "_MainTex", "_EmissionMap", "_BumpMap", "_MetallicGlossMap" })
                        {
                            if (m.HasProperty(prop))
                            {
                                var t = (instance ?? m).GetTexture(prop);
                                Plugin.Logger.LogInfo($"    {prop} = {(t != null ? t.name + " " + t.GetType().Name : "none")}");
                            }
                        }
                    }
                }
                // The screen's text is likely a UI canvas, not a Renderer, so
                // also describe the whole monitor object: everything under the
                // nearest ancestor whose name starts with "Monitor".
                Transform monitorRoot = null;
                foreach (var candidate in candidates)
                {
                    for (var t = candidate.Value.transform; t != null; t = t.parent)
                    {
                        if (t.name.StartsWith("Monitor"))
                        {
                            monitorRoot = t;
                            break;
                        }
                    }
                    if (monitorRoot != null)
                    {
                        break;
                    }
                }
                if (monitorRoot != null)
                {
                    DumpMonitorStructure(monitorRoot);
                }
                Plugin.Logger.LogInfo("=== end material dump ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Material dump failed: {ex}");
            }
        }

        private static string MaterialSummary(Material m)
        {
            if (m == null)
            {
                return "none";
            }
            var zwrite = m.HasProperty("_ZWrite") ? $" ZWrite={m.GetFloat("_ZWrite")}" : "";
            var blend = m.HasProperty("_SrcBlend") ? $" blend={m.GetFloat("_SrcBlend")}/{m.GetFloat("_DstBlend")}" : "";
            var mode = m.HasProperty("_Mode") ? $" _Mode={m.GetFloat("_Mode")}" : "";
            return $"'{m.name}' shader='{m.shader.name}' queue={m.renderQueue} renderType={m.GetTag("RenderType", false, "-")}{mode}{zwrite}{blend} " +
                   $"keywords=[{string.Join(" ", m.shaderKeywords)}]";
        }

        private void DumpMonitorStructure(Transform root)
        {
            var log = Plugin.Logger;
            log.LogInfo($"--- structure of '{Path(root)}' (active={root.gameObject.activeInHierarchy}) ---");

            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true).Take(8))
            {
                log.LogInfo($"  canvas {Path(canvas.transform)} enabled={canvas.enabled} active={canvas.gameObject.activeInHierarchy} " +
                            $"mode={canvas.renderMode} sortingOrder={canvas.sortingOrder} sortingLayer={canvas.sortingLayerName} " +
                            $"overrideSorting={canvas.overrideSorting} worldCamera={(canvas.worldCamera != null ? canvas.worldCamera.name : "none")} " +
                            $"layer={canvas.gameObject.layer}");
            }
            foreach (var group in root.GetComponentsInChildren<CanvasGroup>(true).Take(8))
            {
                log.LogInfo($"  canvasGroup {Path(group.transform)} alpha={group.alpha} interactable={group.interactable}");
            }
            var graphics = root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
            log.LogInfo($"  {graphics.Length} UI graphics under it; first 10:");
            foreach (var g in graphics.Take(10))
            {
                log.LogInfo($"    {g.GetType().Name} {Path(g.transform)} enabled={g.enabled} active={g.gameObject.activeInHierarchy} " +
                            $"color={g.color} material={MaterialSummary(g.materialForRendering)}");
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true).Take(12))
            {
                log.LogInfo($"  renderer {r.GetType().Name} {Path(r.transform)} enabled={r.enabled} active={r.gameObject.activeInHierarchy} " +
                            $"sortingOrder={r.sortingOrder} bounds={r.bounds.center}/{r.bounds.size}");
                log.LogInfo($"    material {MaterialSummary(r.sharedMaterial)}");
            }
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var i = 0; t != null && i < 6; i++, t = t.parent)
            {
                parts.Add(t.name);
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private void DumpLightingState()
        {
            try
            {
                var log = Plugin.Logger;
                log.LogInfo("=== Lighting dump ===");
                log.LogInfo($"pixelLightCount={QualitySettings.pixelLightCount} shadows={QualitySettings.shadows} " +
                            $"shadowDistance={QualitySettings.shadowDistance} colorSpace={QualitySettings.activeColorSpace} " +
                            $"lodBias={QualitySettings.lodBias} realtimeReflectionProbes={QualitySettings.realtimeReflectionProbes}");

                foreach (var c in FindObjectsOfType<Camera>().Where(x => x.enabled && x.gameObject.activeInHierarchy))
                {
                    log.LogInfo($"camera '{c.name}' path={c.actualRenderingPath} hdr={c.allowHDR} msaa={c.allowMSAA} " +
                                $"depth={c.depth} cullingMask={c.cullingMask} clear={c.clearFlags} " +
                                $"target={(c.targetTexture != null ? c.targetTexture.name : "screen")} " +
                                $"near={c.nearClipPlane} far={c.farClipPlane} pos={c.transform.position}");
                }

                var origin = _mainCamera != null ? _mainCamera.transform.position : Vector3.zero;
                var lights = FindObjectsOfType<Light>()
                    .Where(l => l.enabled && l.gameObject.activeInHierarchy && l.intensity > 0f)
                    .Select(l => new { Light = l, Distance = Vector3.Distance(l.transform.position, origin) })
                    .Where(x => x.Distance < 30f || x.Light.type == LightType.Directional)
                    .OrderBy(x => x.Distance)
                    .Take(40)
                    .ToList();
                log.LogInfo($"{lights.Count} active lights within 30m (nearest first):");
                foreach (var entry in lights)
                {
                    var l = entry.Light;
                    var components = string.Join(",", l.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray());
                    log.LogInfo($"  {Path(l.transform)} dist={entry.Distance:F1} type={l.type} color={l.color} " +
                                $"intensity={l.intensity} range={l.range} spot={l.spotAngle} layer={l.gameObject.layer} " +
                                $"cullingMask={l.cullingMask} shadows={l.shadows} renderMode={l.renderMode} " +
                                $"bounce={l.bounceIntensity} flare={(l.flare != null ? l.flare.name : "none")} " +
                                $"components=[{components}]");
                }
                log.LogInfo("=== end lighting dump ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Lighting dump failed: {ex}");
            }
        }
    }
}
