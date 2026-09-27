using System;
using System.Reflection;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>One-off look at how the synchroscope's lights are set up (for the "pointer light does not stand out in VR" report).</summary>
    internal partial class VrManager
    {
        private bool _loggedSynchroscope;
        private float _nextSynchroscopeLook;

        private void LogSynchroscopeOnce()
        {
            if (_loggedSynchroscope || Time.unscaledTime < _nextSynchroscopeLook)
            {
                return;
            }
            _nextSynchroscopeLook = Time.unscaledTime + 20f;
            var scope = FindObjectOfType<Synchronoscope>();
            if (scope == null)
            {
                return;
            }
            _loggedSynchroscope = true;
            try
            {
                foreach (var name in new[] { "MatLuzApagada", "MatLuzEncendida", "MatLuzNoSync", "MatLuzSync" })
                {
                    var field = typeof(Synchronoscope).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var material = field?.GetValue(scope) as Material;
                    if (material == null)
                    {
                        Plugin.Logger.LogInfo($"[synchroscope] {name}: missing");
                        continue;
                    }
                    var color = material.HasProperty("_Color") ? material.GetColor("_Color").ToString() : "-";
                    var emission = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor").ToString() : "-";
                    Plugin.Logger.LogInfo($"[synchroscope] {name}: shader={material.shader.name} color={color} emission={emission} " +
                                          $"emissionKeyword={material.IsKeywordEnabled("_EMISSION")} queue={material.renderQueue}");
                }
                var eye = _leftEyeCamera;
                Plugin.Logger.LogInfo($"[synchroscope] eye camera: allowHDR={eye.allowHDR} rendering={eye.actualRenderingPath}; main camera: allowHDR={_mainCamera.allowHDR}; " +
                                      $"eye texture format={(_leftTex != null ? _leftTex.format.ToString() : "?")}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogInfo($"[synchroscope] look failed: {ex.Message}");
            }
        }
    }
}
