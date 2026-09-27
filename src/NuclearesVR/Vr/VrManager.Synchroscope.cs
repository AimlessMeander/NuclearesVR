using System;
using System.Reflection;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The synchroscope's moving light uses an emission far brighter than 1 (about 4), which the game's glow effect turns
    /// into a bright, whitish spot. The headset image has no brightness above 1 and no glow, so that light came out the
    /// same red as the ring. While VR runs its material is swapped for a bright peach-white that reads the same in a
    /// plain image; the original colours are put back when VR stops.
    /// </summary>
    internal partial class VrManager
    {
        private Material _syncPointerMaterial;
        private Color _syncPointerColor, _syncPointerEmission;
        private float _nextSynchroscopeLook;

        private void FixSynchroscopeLight()
        {
            if (_syncPointerMaterial != null || Time.unscaledTime < _nextSynchroscopeLook)
            {
                return;
            }
            _nextSynchroscopeLook = Time.unscaledTime + 20f;
            var scope = FindObjectOfType<Synchronoscope>();
            if (scope == null)
            {
                return;
            }
            try
            {
                var field = typeof(Synchronoscope).GetField("MatLuzNoSync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var material = field?.GetValue(scope) as Material;
                if (material == null || !material.HasProperty("_EmissionColor"))
                {
                    return;
                }
                _syncPointerMaterial = material;
                _syncPointerColor = material.GetColor("_Color");
                _syncPointerEmission = material.GetColor("_EmissionColor");
                material.SetColor("_Color", new Color(1f, 0.6f, 0.5f, 1f));
                material.SetColor("_EmissionColor", new Color(1f, 0.88f, 0.72f, 1f));
                Plugin.Logger.LogInfo("Synchroscope: moving light made bright for the headset.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogInfo($"Synchroscope light fix failed: {ex.Message}");
            }
        }

        private void RestoreSynchroscopeLight()
        {
            if (_syncPointerMaterial != null)
            {
                _syncPointerMaterial.SetColor("_Color", _syncPointerColor);
                _syncPointerMaterial.SetColor("_EmissionColor", _syncPointerEmission);
                _syncPointerMaterial = null;
            }
        }
    }
}
