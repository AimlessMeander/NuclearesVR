using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Ctrl+Shift+B: finds out what is costing GPU time at the spot you are standing in. Stand still,
    /// look at the view that runs slowly, press it, and keep still for about 40 seconds. Each setting
    /// that could reduce the cost is applied in turn (and undone afterwards) while SteamVR's own GPU
    /// timing is averaged, and the results are written to the log as [bench] lines.
    ///
    /// What earlier runs showed: the two headset views cost about 10 ms each in a heavy spot and
    /// everything else in the game 1.5 ms; the cost did not change with resolution, MSAA, the water,
    /// volumetric lights or pixel lights, and vanished when the Default layer (about 5000 visible
    /// objects) was hidden. So the cost is the number of objects drawn, which points at culling and
    /// level of detail rather than pixels.
    /// </summary>
    internal partial class VrManager
    {
        private bool _benchRunning;
        private bool _benchNoLiquid;
        private int _benchEyeMaskRemove;
        private float _benchFar;
        private bool _benchOcclusion;

        private void StartBenchmark()
        {
            if (_benchRunning)
            {
                Plugin.Logger.LogInfo("[bench] already running.");
                return;
            }
            StartCoroutine(RunBenchmark());
        }

        private IEnumerator RunBenchmark()
        {
            _benchRunning = true;
            var log = Plugin.Logger;
            log.LogInfo("[bench] starting - keep still and keep looking at the same view for about 40 seconds.");
            log.LogInfo($"[bench] settings: pixelLightCount={QualitySettings.pixelLightCount} shadows={QualitySettings.shadows} " +
                        $"shadowDistance={QualitySettings.shadowDistance:F0} lodBias={QualitySettings.lodBias:F2} maximumLODLevel={QualitySettings.maximumLODLevel} " +
                        $"eyeFar={_eyeFar:F0} eyeOcclusionCulling={Plugin.EyeOcclusionCulling.Value} scale={_currentScale:F2} msaa={_currentMsaa}");

            var results = new List<string>();
            yield return Measure("baseline", null, null, results);

            var shadows = QualitySettings.shadows;
            yield return Measure("shadows off",
                () => QualitySettings.shadows = ShadowQuality.Disable,
                () => QualitySettings.shadows = shadows, results);

            yield return Measure("occlusion culling on",
                () => _benchOcclusion = true, () => _benchOcclusion = false, results);

            yield return Measure("draw distance 100 m",
                () => _benchFar = 100f, () => _benchFar = 0f, results);
            yield return Measure("draw distance 40 m",
                () => _benchFar = 40f, () => _benchFar = 0f, results);

            var lodBias = QualitySettings.lodBias;
            yield return Measure("LOD bias 0.5",
                () => QualitySettings.lodBias = 0.5f, () => QualitySettings.lodBias = lodBias, results);
            yield return Measure("LOD bias 0.25",
                () => QualitySettings.lodBias = 0.25f, () => QualitySettings.lodBias = lodBias, results);

            yield return Measure("occlusion + 100 m + LOD 0.5",
                () => { _benchOcclusion = true; _benchFar = 100f; QualitySettings.lodBias = 0.5f; },
                () => { _benchOcclusion = false; _benchFar = 0f; QualitySettings.lodBias = lodBias; }, results);

            yield return Measure("no headset views rendered",
                () =>
                {
                    if (_leftEyeCamera != null) _leftEyeCamera.enabled = false;
                    if (_rightEyeCamera != null) _rightEyeCamera.enabled = false;
                },
                () =>
                {
                    if (_leftEyeCamera != null) _leftEyeCamera.enabled = true;
                    if (_rightEyeCamera != null) _rightEyeCamera.enabled = true;
                }, results);

            log.LogInfo("[bench] ---- results (SteamVR GPU time per frame; lower is better) ----");
            foreach (var line in results)
            {
                log.LogInfo(line);
            }
            log.LogInfo("[bench] finished.");
            _benchRunning = false;
        }

        private IEnumerator Measure(string label, Action apply, Action restore, List<string> results)
        {
            apply?.Invoke();
            var warmUpEnd = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < warmUpEnd)
            {
                yield return null;
            }

            double gpuSum = 0;
            float gpuMin = float.MaxValue, gpuMax = 0;
            var samples = 0;
            var frames = 0;
            var start = Time.realtimeSinceStartup;
            var end = start + 3f;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                frames++;
                var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
                if (OpenVR.Compositor != null && OpenVR.Compositor.GetFrameTiming(ref timing, 0))
                {
                    gpuSum += timing.m_flTotalRenderGpuMs;
                    gpuMin = Mathf.Min(gpuMin, timing.m_flTotalRenderGpuMs);
                    gpuMax = Mathf.Max(gpuMax, timing.m_flTotalRenderGpuMs);
                    samples++;
                }
            }
            restore?.Invoke();

            var fps = frames / (Time.realtimeSinceStartup - start);
            var line = samples > 0
                ? $"[bench] {label,-30} gpu avg {gpuSum / samples,5:F1} ms (min {gpuMin:F1}, max {gpuMax:F1})   game {fps:F0} fps"
                : $"[bench] {label,-30} no SteamVR timing available   game {fps:F0} fps";
            results.Add(line);
            Plugin.Logger.LogInfo(line);
        }
    }
}
