using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Frame pacing and a performance log.
    ///
    /// The headset sets the pace: WaitGetPoses blocks until the compositor wants the next frame. The
    /// game window is also presented to the monitor, and if the game's VSync option is on, that present
    /// waits for the monitor's refresh as well. Two waits in a row settle at a fraction of the headset's
    /// rate (a steady 30 is typical), so VSync is turned off while VR runs and put back afterwards.
    /// </summary>
    internal partial class VrManager
    {
        private int _originalVSync = -1;
        private float _perfWindowStart;
        private int _perfFrames;
        private uint _lastMisPresented, _lastDropped, _lastPresents;

        private void ApplyVSyncPolicy()
        {
            if (!Plugin.DisableVsyncInVr.Value)
            {
                return;
            }
            if (_originalVSync < 0)
            {
                _originalVSync = QualitySettings.vSyncCount;
            }
            var current = QualitySettings.vSyncCount;
            if (current != 0)
            {
                QualitySettings.vSyncCount = 0;
                LogThrottled("vsync", $"VSync was {current}: turned off while VR runs (the headset sets the frame rate).");
            }
        }

        private float _originalShadowDistance = -1f;

        /// <summary>Shortens the shadow distance while VR runs (see the setting); the game may reapply its own, so this is checked every frame.</summary>
        private void ApplyShadowPolicy()
        {
            var limit = Plugin.VrShadowDistance.Value;
            if (limit <= 0f)
            {
                return;
            }
            if (_originalShadowDistance < 0f)
            {
                _originalShadowDistance = QualitySettings.shadowDistance;
            }
            if (QualitySettings.shadowDistance > limit + 0.01f)
            {
                var was = QualitySettings.shadowDistance;
                QualitySettings.shadowDistance = limit;
                LogThrottled("shadowdist", $"Shadow distance {was:F0} m -> {limit:F0} m while VR runs.");
            }
        }

        private void RestoreShadowDistance()
        {
            if (_originalShadowDistance >= 0f)
            {
                QualitySettings.shadowDistance = _originalShadowDistance;
                _originalShadowDistance = -1f;
            }
        }

        private void RestoreVSync()
        {
            if (_originalVSync >= 0)
            {
                QualitySettings.vSyncCount = _originalVSync;
                _originalVSync = -1;
            }
        }

        /// <summary>Every 5 seconds: the game's frame rate and SteamVR's own view of how it is doing.</summary>
        private void LogPerformance()
        {
            _perfFrames++;
            var now = Time.realtimeSinceStartup;
            if (_perfWindowStart == 0f)
            {
                _perfWindowStart = now;
                return;
            }
            var elapsed = now - _perfWindowStart;
            if (elapsed < 5f)
            {
                return;
            }

            var fps = _perfFrames / elapsed;
            var text = $"[perf] game {fps:F1} fps ({1000f / Mathf.Max(fps, 0.01f):F1} ms/frame), vsync={QualitySettings.vSyncCount}, far small objects hidden {_farHiddenCount}/{_farCandidates.Length} (detail limit {(_densityLimit < float.MaxValue ? _densityLimit.ToString("F0") : "off")}, boost {_cullBoost:F2}), graphics memory {UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576} MB, render textures {Resources.FindObjectsOfTypeAll<RenderTexture>().Length}";
            try
            {
                var error = ETrackedPropertyError.TrackedProp_Success;
                var hz = _system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
                var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
                if (OpenVR.Compositor.GetFrameTiming(ref timing, 0))
                {
                    text += $" | headset {hz:F0} Hz | SteamVR: gpu {timing.m_flTotalRenderGpuMs:F1} ms (compositor {timing.m_flCompositorRenderGpuMs:F1}), " +
                            $"frame interval {timing.m_flClientFrameIntervalMs:F1} ms, present call {timing.m_flPresentCallCpuMs:F1} ms, " +
                            $"reprojection flags 0x{timing.m_nReprojectionFlags:X}, " +
                            $"presents +{timing.m_nNumFramePresents - _lastPresents}, mispresented +{timing.m_nNumMisPresented - _lastMisPresented}, dropped +{timing.m_nNumDroppedFrames - _lastDropped}";
                    _lastPresents = timing.m_nNumFramePresents;
                    _lastMisPresented = timing.m_nNumMisPresented;
                    _lastDropped = timing.m_nNumDroppedFrames;
                }
                else
                {
                    text += $" | headset {hz:F0} Hz | SteamVR frame timing unavailable";
                }
            }
            catch (Exception ex)
            {
                text += $" | (SteamVR timing failed: {ex.Message})";
            }
            Plugin.Logger.LogInfo(text);
            _perfWindowStart = now;
            _perfFrames = 0;
        }
    }
}
