using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Adaptive eye resolution. How expensive the headset views are depends heavily on what is in
    /// view (measured on a 4090: about 1.5 ms per eye in one spot and 10 ms per eye in another), and a
    /// 90 Hz headset gives 11 ms for both eyes together. SteamVR reports how long the GPU took for each
    /// frame; when that is over budget the eye textures are recreated a step smaller, and when there
    /// is clear headroom they grow back, up to the RenderScale setting. Old textures are kept for a few
    /// seconds before being freed, because the compositor may still be reading the last submitted one.
    /// </summary>
    internal partial class VrManager
    {
        private int _recommendedWidth, _recommendedHeight;
        private float _headsetHz = 90f;
        private float _currentScale = 1f;
        private int _currentMsaa = 4;

        private readonly List<KeyValuePair<RenderTexture, float>> _retiredTextures = new List<KeyValuePair<RenderTexture, float>>();

        private double _dynGpuSum;
        private int _dynSamples;
        private float _dynWindowStart;
        private float _lastScaleChange, _lastScaleIncrease;
        private float _scaleCeiling = 10f, _scaleCeilingUntil;

        private const float ScaleStep = 0.05f;

        private RenderTexture MakeEyeTexture(float scale, int msaa)
        {
            var width = Mathf.Max(64, Mathf.RoundToInt(_recommendedWidth * scale / 8f) * 8);
            var height = Mathf.Max(64, Mathf.RoundToInt(_recommendedHeight * scale / 8f) * 8);
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.Default) { antiAliasing = msaa };
            texture.Create();
            return texture;
        }

        /// <summary>Replaces both eye textures with ones of a new size / MSAA level. Safe to call between frames.</summary>
        private void ResizeEyeTextures(float scale, int msaa)
        {
            var newLeft = MakeEyeTexture(scale, msaa);
            var newRight = MakeEyeTexture(scale, msaa);
            var release = Time.realtimeSinceStartup + 3f;
            if (_leftTex != null) _retiredTextures.Add(new KeyValuePair<RenderTexture, float>(_leftTex, release));
            if (_rightTex != null) _retiredTextures.Add(new KeyValuePair<RenderTexture, float>(_rightTex, release));
            _leftTex = newLeft;
            _rightTex = newRight;
            if (_leftEyeCamera != null) _leftEyeCamera.targetTexture = _leftTex;
            if (_rightEyeCamera != null) _rightEyeCamera.targetTexture = _rightTex;
            _currentScale = scale;
            _currentMsaa = msaa;
        }

        private void ReleaseRetiredTextures(bool all)
        {
            var now = Time.realtimeSinceStartup;
            for (var i = _retiredTextures.Count - 1; i >= 0; i--)
            {
                if (all || _retiredTextures[i].Value <= now)
                {
                    var texture = _retiredTextures[i].Key;
                    if (texture != null)
                    {
                        texture.Release();
                        Destroy(texture);
                    }
                    _retiredTextures.RemoveAt(i);
                }
            }
        }

        /// <summary>Once per frame while VR runs.</summary>
        private void UpdateDynamicResolution()
        {
            ReleaseRetiredTextures(false);
            if (!Plugin.DynamicResolution.Value || _benchRunning || _mirrorVisible || PlayerLook.Instancia == null)
            {
                _dynSamples = 0;
                _dynGpuSum = 0;
                return;
            }

            var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
            if (OpenVR.Compositor != null && OpenVR.Compositor.GetFrameTiming(ref timing, 0))
            {
                _dynGpuSum += timing.m_flTotalRenderGpuMs;
                _dynSamples++;
            }

            var now = Time.realtimeSinceStartup;
            if (_dynWindowStart == 0f)
            {
                _dynWindowStart = now;
            }
            if (now - _dynWindowStart < 1f || _dynSamples < 10)
            {
                return;
            }
            var average = _dynGpuSum / _dynSamples;
            _dynGpuSum = 0;
            _dynSamples = 0;
            _dynWindowStart = now;

            var budget = 1000f / _headsetHz * 0.92f;
            var maxScale = Plugin.RenderScale.Value;
            var minScale = Mathf.Min(Plugin.MinRenderScale.Value, maxScale);
            var target = _currentScale;

            if (average > budget * 1.05f && now - _lastScaleChange > 1.5f && _currentScale > minScale + 0.001f)
            {
                var step = average > budget * 1.6f ? ScaleStep * 2f : ScaleStep;
                target = Mathf.Max(minScale, _currentScale - step);
                if (now - _lastScaleIncrease < 10f)
                {
                    // It only just grew and was too much: hold it below that for a while.
                    _scaleCeiling = target;
                    _scaleCeilingUntil = now + 45f;
                }
            }
            else if (average < budget * 0.72f && now - _lastScaleChange > 6f && _currentScale < maxScale - 0.001f)
            {
                var ceiling = now < _scaleCeilingUntil ? _scaleCeiling : maxScale;
                target = Mathf.Min(Mathf.Min(maxScale, ceiling), _currentScale + ScaleStep);
                if (target > _currentScale)
                {
                    _lastScaleIncrease = now;
                }
            }

            if (Mathf.Abs(target - _currentScale) > 0.001f)
            {
                Plugin.Logger.LogInfo($"[dynres] render scale {_currentScale:F2} -> {target:F2} (GPU {average:F1} ms, budget {budget:F1} ms)");
                _lastScaleChange = now;
                ResizeEyeTextures(target, _currentMsaa);
            }
        }
    }
}
