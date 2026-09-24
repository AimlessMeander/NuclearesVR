using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// On/off experiments for the in-game monitor text problem (text visible
    /// only from some viewpoints in the headset), each aimed at one
    /// hypothesis. Nothing here is on by default.
    /// </summary>
    internal partial class VrManager
    {
        private readonly Dictionary<Renderer, bool> _hiddenGlass = new Dictionary<Renderer, bool>();
        private bool _textZTestAlways;

        // Ctrl+Shift+G: hide (and restore) glass covers within 25m. If the text
        // becomes stable everywhere, the glass in front of it is the cause.
        private void ToggleGlass()
        {
            if (_hiddenGlass.Count > 0)
            {
                foreach (var pair in _hiddenGlass)
                {
                    if (pair.Key != null)
                    {
                        pair.Key.enabled = pair.Value;
                    }
                }
                Plugin.Logger.LogInfo($"EXPERIMENT: restored {_hiddenGlass.Count} glass renderers.");
                _hiddenGlass.Clear();
                return;
            }

            var origin = _mainCamera != null ? _mainCamera.transform.position : Vector3.zero;
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                var name = r.name.ToLowerInvariant();
                if ((name.Contains("cristal") || name.Contains("glass") || name.Contains("vidrio")) &&
                    Vector3.Distance(r.bounds.center, origin) < 25f)
                {
                    _hiddenGlass[r] = r.enabled;
                    r.enabled = false;
                }
            }
            Plugin.Logger.LogInfo($"EXPERIMENT: hid {_hiddenGlass.Count} glass renderers within 25m: " +
                                  string.Join(", ", _hiddenGlass.Keys.Take(6).Select(r => Path(r.transform)).ToArray()));
        }

        // Ctrl+Shift+Y: 3D TextMeshPro text ignores the depth test (draws over
        // anything in front of it). TextMeshPro's shaders read ZTest from the
        // global "unity_GUIZTestMode" property on the material. If the text
        // becomes stable everywhere, depth (z-fighting, or something in front
        // of it winning) is the cause.
        private void ToggleTextZTest()
        {
            _textZTestAlways = !_textZTestAlways;
            var changed = 0;
            var seen = new HashSet<Material>();
            foreach (var text in FindObjectsOfType<TextMeshPro>(true))
            {
                var material = text.fontSharedMaterial;
                if (material != null && seen.Add(material) && material.HasProperty("unity_GUIZTestMode"))
                {
                    material.SetInt("unity_GUIZTestMode", _textZTestAlways ? 8 : 4);
                    changed++;
                }
            }
            Plugin.Logger.LogInfo($"EXPERIMENT: 3D text depth test {(_textZTestAlways ? "OFF (always draws)" : "back to normal")} on {changed} materials " +
                                  $"({seen.Count} distinct font materials found).");
        }

        private readonly Dictionary<Material, KeyValuePair<Shader, int>> _onTopBackup = new Dictionary<Material, KeyValuePair<Shader, int>>();

        // Ctrl+Shift+Z: force all 3D text to draw on top: the game's unlit text
        // shader (which does have a depth-test setting), depth test off, drawn
        // after all solid geometry. If the monitor text becomes stable from
        // every position, the text is losing a depth fight with the screen
        // surface behind it (it sits about a millimetre in front).
        private int _onTopMode;

        private void ToggleTextOnTop()
        {
            // Cycle: off -> 1 (blunt: unlit, no depth test) -> 2 (gentle: only
            // draw the text later, everything else as the game has it) -> off.
            var restore = _onTopBackup.Count > 0;
            var wasMode = _onTopMode;
            if (restore)
            {
                foreach (var pair in _onTopBackup)
                {
                    if (pair.Key != null)
                    {
                        pair.Key.shader = pair.Value.Key;
                        pair.Key.renderQueue = pair.Value.Value;
                    }
                }
                Plugin.Logger.LogInfo($"EXPERIMENT: text drawing restored on {_onTopBackup.Count} materials.");
                _onTopBackup.Clear();
                _onTopMode = 0;
                if (wasMode == 2)
                {
                    return;
                }
            }
            _onTopMode = wasMode + 1;
            if (_onTopMode == 2)
            {
                foreach (var text in FindObjectsOfType<TextMeshPro>(true))
                {
                    var m = text.fontSharedMaterial;
                    if (m != null && !_onTopBackup.ContainsKey(m))
                    {
                        _onTopBackup[m] = new KeyValuePair<Shader, int>(m.shader, m.renderQueue);
                        m.renderQueue = 2500;
                    }
                }
                Plugin.Logger.LogInfo($"EXPERIMENT 2: 3D text drawn after other solid geometry (queue 2500), everything else unchanged, on {_onTopBackup.Count} materials.");
                return;
            }
            var unlit = Shader.Find(UnlitTextShaderName);
            if (unlit == null)
            {
                Plugin.Logger.LogWarning("EXPERIMENT: unlit text shader not found.");
                return;
            }
            var withZTest = 0;
            foreach (var text in FindObjectsOfType<TextMeshPro>(true))
            {
                var m = text.fontSharedMaterial;
                if (m == null || _onTopBackup.ContainsKey(m))
                {
                    continue;
                }
                _onTopBackup[m] = new KeyValuePair<Shader, int>(m.shader, m.renderQueue);
                if (m.shader.name == LitTextShaderName)
                {
                    m.shader = unlit;
                }
                m.renderQueue = 3000;
                if (m.HasProperty("unity_GUIZTestMode"))
                {
                    m.SetInt("unity_GUIZTestMode", 8);
                    withZTest++;
                }
            }
            Plugin.Logger.LogInfo($"EXPERIMENT: 3D text forced on top (unlit, queue 3000, no depth test) on {_onTopBackup.Count} materials ({withZTest} accepted the depth-test setting).");
        }
    }
}
