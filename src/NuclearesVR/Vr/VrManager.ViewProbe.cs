using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Ctrl+Shift+V: works out which things in the view you are looking at cost the GPU time. Stand
    /// where the frame rate drops and look at the slow view, press it, and keep still (about a minute).
    /// It lists what is being drawn, grouped by kind of renderer and by shader, with vertex counts, and
    /// then hides each group in turn while measuring SteamVR's GPU time, so the group whose absence
    /// makes the cost disappear stands out. Results are [probe] lines in the log.
    /// </summary>
    internal partial class VrManager
    {
        private bool _probeRunning;

        private sealed class RendererGroup
        {
            public string Name;
            public readonly List<Renderer> Members = new List<Renderer>();
            public long Vertices;
            public int SubMeshes;
        }

        private static int VertexCount(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer skinned:
                    return skinned.sharedMesh != null ? skinned.sharedMesh.vertexCount : 0;
                case MeshRenderer _:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : 0;
                default:
                    return 0;
            }
        }

        private static string RendererPath(Transform transform)
        {
            var parts = new List<string>();
            for (var i = 0; transform != null && i < 5; i++, transform = transform.parent)
            {
                parts.Add(transform.name);
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private void StartViewProbe()
        {
            if (_probeRunning || _benchRunning)
            {
                Plugin.Logger.LogInfo("[probe] a benchmark or probe is already running.");
                return;
            }
            StartCoroutine(RunViewProbe());
        }

        private IEnumerator RunViewProbe()
        {
            _probeRunning = true;
            _benchRunning = true; // also keeps dynamic resolution from changing things
            var log = Plugin.Logger;
            log.LogInfo("[probe] starting - keep still, looking at the slow view, for about a minute.");

            // ---- what is being drawn ----
            var head = _leftEyeCamera != null ? _leftEyeCamera.transform.position : Vector3.zero;
            var visible = new List<Renderer>();
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (r != null && r.enabled && r.isVisible && r.gameObject.activeInHierarchy)
                {
                    visible.Add(r);
                }
            }
            log.LogInfo($"[probe] {visible.Count} renderers visible to some camera right now.");

            var groups = new Dictionary<string, RendererGroup>();
            void Add(string key, Renderer r)
            {
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new RendererGroup { Name = key };
                    groups[key] = group;
                }
                group.Members.Add(r);
                group.Vertices += VertexCount(r);
                group.SubMeshes += Mathf.Max(1, r.sharedMaterials.Length);
            }
            foreach (var r in visible)
            {
                Add("type " + r.GetType().Name, r);
                var material = r.sharedMaterial;
                var shader = material != null && material.shader != null ? material.shader.name : "(no material)";
                Add("shader " + shader, r);
                if (material != null && material.renderQueue >= 2500)
                {
                    Add("transparent/late queue (>=2500)", r);
                }
                Add("layer " + r.gameObject.layer + " " + LayerMask.LayerToName(r.gameObject.layer), r);
            }
            foreach (var group in groups.Values.OrderByDescending(g => g.Vertices).Take(18))
            {
                log.LogInfo($"[probe] group '{group.Name}': {group.Members.Count} renderers, {group.SubMeshes} draws, {group.Vertices:N0} vertices");
            }
            foreach (var r in visible.OrderByDescending(VertexCount).Take(12))
            {
                log.LogInfo($"[probe] heaviest: {RendererPath(r.transform)} verts={VertexCount(r):N0} dist={(r.bounds.center - head).magnitude:F0} m " +
                            $"layer={r.gameObject.layer} shader={(r.sharedMaterial != null ? r.sharedMaterial.shader.name : "-")}");
            }

            // ---- other things that cost per frame ----
            var cameras = Camera.allCameras.Where(c => c.enabled && c.targetTexture != null && !c.name.StartsWith("NuclearesVR_")).ToList();
            log.LogInfo($"[probe] other cameras rendering to textures: {cameras.Count}" +
                        (cameras.Count > 0 ? " (" + string.Join(", ", cameras.Select(c => c.name).ToArray()) + ")" : ""));
            var lights = FindObjectsOfType<Light>().Where(l => l.enabled && l.gameObject.activeInHierarchy).ToList();
            log.LogInfo($"[probe] enabled lights: {lights.Count} (" + string.Join(", ", lights.Take(10).Select(l => $"{l.name}:{l.type}:{l.shadows}").ToArray()) + ")");
            var particles = FindObjectsOfType<ParticleSystem>().Where(p => p.isPlaying).ToList();
            log.LogInfo($"[probe] playing particle systems: {particles.Count}" +
                        (particles.Count > 0 ? " (" + string.Join(", ", particles.Take(8).Select(p => RendererPath(p.transform)).ToArray()) + ")" : ""));

            // ---- what happens when each group is hidden ----
            var results = new List<string>();
            yield return ProbeMeasure("baseline (nothing hidden)", null, null, results);

            foreach (var group in groups.Values.Where(g => g.Members.Count >= 3).OrderByDescending(g => g.Vertices).Take(12))
            {
                var members = group.Members;
                var before = new List<bool>();
                yield return ProbeMeasure($"hide {group.Name} ({members.Count})",
                    () =>
                    {
                        foreach (var r in members)
                        {
                            before.Add(r != null && r.enabled);
                            if (r != null) r.enabled = false;
                        }
                    },
                    () =>
                    {
                        for (var i = 0; i < members.Count; i++)
                        {
                            if (members[i] != null) members[i].enabled = before[i];
                        }
                    }, results);
            }

            var disabledLights = new List<Light>();
            yield return ProbeMeasure("all lights off",
                () => { foreach (var l in lights) { if (l != null) { disabledLights.Add(l); l.enabled = false; } } },
                () => { foreach (var l in disabledLights) { if (l != null) l.enabled = true; } disabledLights.Clear(); }, results);

            var stopped = new List<ParticleSystem>();
            yield return ProbeMeasure("particle systems paused",
                () => { foreach (var p in particles) { if (p != null) { stopped.Add(p); p.Pause(); p.GetComponent<Renderer>().enabled = false; } } },
                () => { foreach (var p in stopped) { if (p != null) { p.Play(); p.GetComponent<Renderer>().enabled = true; } } stopped.Clear(); }, results);

            log.LogInfo("[probe] ---- results (SteamVR GPU time per frame; lower is better) ----");
            foreach (var line in results)
            {
                log.LogInfo(line);
            }
            log.LogInfo("[probe] finished.");
            _benchRunning = false;
            _probeRunning = false;
        }

        private IEnumerator ProbeMeasure(string label, Action apply, Action restore, List<string> results)
        {
            apply?.Invoke();
            var warmUpEnd = Time.realtimeSinceStartup + 1.2f;
            while (Time.realtimeSinceStartup < warmUpEnd)
            {
                yield return null;
            }

            double gpuSum = 0;
            var samples = 0;
            var frames = 0;
            var start = Time.realtimeSinceStartup;
            var end = start + 2.5f;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                frames++;
                var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
                if (OpenVR.Compositor != null && OpenVR.Compositor.GetFrameTiming(ref timing, 0))
                {
                    gpuSum += timing.m_flTotalRenderGpuMs;
                    samples++;
                }
            }
            restore?.Invoke();

            var line = samples > 0
                ? $"[probe] {label,-60} gpu avg {gpuSum / samples,5:F1} ms   game {frames / (Time.realtimeSinceStartup - start):F0} fps"
                : $"[probe] {label,-60} no SteamVR timing available";
            results.Add(line);
            Plugin.Logger.LogInfo(line);
        }
    }
}
