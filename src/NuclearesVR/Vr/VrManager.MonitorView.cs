using System.Collections.Generic;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's own camera renders the whole scene again for the monitor, at full window size with
    /// its full set of effects (ambient occlusion, screen-space reflections, post-processing), on top of
    /// the two headset views. In VR nobody looks at that picture, yet its GPU cost counts against
    /// every frame, and SteamVR's frame timings showed the frame time swinging between about 5 and
    /// 38 ms depending on where the player stood.
    ///
    /// While VR runs, the game camera is told to draw nothing and its heavy effects are switched off.
    /// It draws nothing by giving every layer a tiny culling distance - NOT by emptying its culling
    /// mask: Unity also uses that mask to decide which objects receive mouse events (OnMouseDown)
    /// and UI clicks, so an empty mask made nothing clickable. It is still where the game's mouse
    /// handling aims from, so pointing, clicking and the tablet are unaffected. It renders normally while the virtual
    /// menu screen is showing (that screen is a capture of the monitor). The monitor mirror is
    /// therefore a black window while playing in VR (set LightweightMonitorView = false to keep it).
    /// </summary>
    internal partial class VrManager
    {
        private int _gameMask;
        private int _ourMainMask = -2;
        private bool _haveGameMask;
        private bool _mainCullingSkipped;
        private float[] _originalCullDistances;
        private static readonly float[] SkipCullDistances = MakeSkipCullDistances();

        private static float[] MakeSkipCullDistances()
        {
            var distances = new float[32];
            for (var i = 0; i < distances.Length; i++)
            {
                distances[i] = 0.02f;
            }
            return distances;
        }

        private void SetMainCullingSkipped(bool skip)
        {
            if (skip == _mainCullingSkipped || _mainCamera == null)
            {
                return;
            }
            if (skip)
            {
                _originalCullDistances = _mainCamera.layerCullDistances;
                _mainCamera.layerCullDistances = SkipCullDistances;
            }
            else if (_originalCullDistances != null)
            {
                _mainCamera.layerCullDistances = _originalCullDistances;
            }
            _mainCullingSkipped = skip;
        }

        private readonly Dictionary<Behaviour, bool> _switchedOffEffects = new Dictionary<Behaviour, bool>();

        private static readonly string[] HeavyEffectTypes = { "ShinySSRR", "PostProcessLayer", "HBAO", "Beautify", "WetStuff" };

        /// <summary>The culling mask the game itself set for its camera, remembered because we change the camera's copy.</summary>
        private void UpdateGameMask()
        {
            var seen = _mainCamera.cullingMask;
            if (!_haveGameMask || seen != _ourMainMask)
            {
                _gameMask = seen;
                _haveGameMask = true;
            }
        }

        /// <summary>After the eye cameras have copied the game's mask: set what the game's own camera draws.</summary>
        private void ApplyMainMask()
        {
            var skip = Plugin.MonitorView.Value != MonitorViewMode.Game && !_mirrorVisible;
            var mask = _gameMask;
            _mainCamera.cullingMask = mask;
            _ourMainMask = mask;
            SetMainCullingSkipped(skip);
            SetHeavyEffects(!skip);
        }

        private void RestoreMainMask()
        {
            if (_mainCamera != null && _haveGameMask)
            {
                _mainCamera.cullingMask = _gameMask;
            }
            SetMainCullingSkipped(false);
            _mainCullingSkipped = false; // also when the camera itself is already gone
            SetHeavyEffects(true);
            _haveGameMask = false;
            _ourMainMask = -2;
        }

        private void SetHeavyEffects(bool enabled)
        {
            if (enabled)
            {
                foreach (var pair in _switchedOffEffects)
                {
                    if (pair.Key != null && pair.Value)
                    {
                        pair.Key.enabled = true;
                    }
                }
                _switchedOffEffects.Clear();
                return;
            }

            if (_mainCamera == null)
            {
                return;
            }
            foreach (var behaviour in _mainCamera.GetComponents<Behaviour>())
            {
                if (behaviour == null || System.Array.IndexOf(HeavyEffectTypes, behaviour.GetType().Name) < 0)
                {
                    continue;
                }
                if (!_switchedOffEffects.ContainsKey(behaviour))
                {
                    _switchedOffEffects[behaviour] = behaviour.enabled; // remember whether the game had it on
                }
                if (behaviour.enabled)
                {
                    behaviour.enabled = false; // the game may switch these back on, so this is enforced every frame
                }
            }
        }
    }
}
