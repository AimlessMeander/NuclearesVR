using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The in-game monitors' text is 3D TextMeshPro using the "Distance Field
    /// (Surface)" shader - TextMeshPro's *lit* variant, which the game picks
    /// on purpose (Ficheros.GenerarFuenteTMPPro_AfectadaPorIluminacion) so text
    /// is shaded by scene lighting. Under the game's own Deferred pipeline that
    /// looks right; with the eye cameras in Forward the shading, reflections
    /// and light probes it depends on come out differently, and because
    /// reflection and specular terms depend on the view direction, the text
    /// showed only from certain positions, popping on and off and sweeping
    /// across the screen as the head moved. Text on a display should just be
    /// visible, so switch those materials to the game's own unlit variant
    /// ("TextMeshPro/Distance Field", which the game also loads, so it's in
    /// the build) while in a game, and remember what they were so Ctrl+Shift+T
    /// can put them back. Off by default: it applied fine but made no
    /// difference to the monitors, so lit text was not the cause. It.s the material that.s changed, so it applies to
    /// the monitor view as well.
    /// </summary>
    internal partial class VrManager
    {
        internal static bool UnlitMonitorText = false;

        private const string LitTextShaderName = "TextMeshPro/Distance Field (Surface)";
        private const string UnlitTextShaderName = "TextMeshPro/Distance Field";

        private readonly Dictionary<Material, Shader> _originalTextShaders = new Dictionary<Material, Shader>();
        private Shader _unlitTextShader;
        private bool _searchedForUnlitShader;
        private float _nextTextScan;

        private void UpdateMonitorTextShaders()
        {
            if (!UnlitMonitorText)
            {
                if (_originalTextShaders.Count > 0)
                {
                    foreach (var pair in _originalTextShaders)
                    {
                        if (pair.Key != null && pair.Value != null)
                        {
                            pair.Key.shader = pair.Value;
                        }
                    }
                    Plugin.Logger.LogInfo($"Restored the lit text shader on {_originalTextShaders.Count} materials.");
                    _originalTextShaders.Clear();
                }
                return;
            }

            // Only in a game (the menu's text isn't affected), and only every
            // few seconds - new text can appear as rooms load.
            if (PlayerLook.Instancia == null || Time.unscaledTime < _nextTextScan)
            {
                return;
            }
            _nextTextScan = Time.unscaledTime + 3f;

            if (!_searchedForUnlitShader)
            {
                _searchedForUnlitShader = true;
                _unlitTextShader = Shader.Find(UnlitTextShaderName);
                if (_unlitTextShader == null)
                {
                    Plugin.Logger.LogWarning($"Shader '{UnlitTextShaderName}' not found - monitor text left as the game has it.");
                }
            }
            if (_unlitTextShader == null)
            {
                return;
            }

            var switched = 0;
            foreach (var text in FindObjectsOfType<TextMeshPro>(true))
            {
                var material = text.fontSharedMaterial;
                if (material != null && material.shader != null && material.shader.name == LitTextShaderName)
                {
                    _originalTextShaders[material] = material.shader;
                    material.shader = _unlitTextShader;
                    switched++;
                }
            }
            if (switched > 0)
            {
                Plugin.Logger.LogInfo($"Switched {switched} lit text material(s) to '{UnlitTextShaderName}'.");
            }
        }
    }
}
