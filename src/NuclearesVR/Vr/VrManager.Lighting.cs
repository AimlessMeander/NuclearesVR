using System.Collections.Generic;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Optional workaround, off by default (Ctrl+Shift+K turns it on): strip
    /// the shadows from spot lights. Under Deferred rendering, spot lights with
    /// hard shadows don't light anything for the eye cameras; with the shadows
    /// off they do. The eye cameras render in Forward by default now (see
    /// ForwardOnEyes), which doesn't have that problem, so this is only for
    /// comparing. The cost is that spot lights cast no shadows, on the monitor
    /// too, since shadows are a property of the light and not of a camera.
    ///
    /// Only ever touches lights it stripped itself, and puts back exactly the
    /// setting each one had. An earlier version restored by setting every
    /// shadowless spot light to Hard - which turned on hard shadows for lights
    /// the game intentionally has none on, and crashed the game at the menu.
    /// </summary>
    internal partial class VrManager
    {
        internal static bool StripSpotShadows = false;

        private readonly Dictionary<Light, LightShadows> _strippedLights = new Dictionary<Light, LightShadows>();
        private float _nextLightScan;

        private void UpdateLightingWorkarounds()
        {
            if (!StripSpotShadows)
            {
                if (_strippedLights.Count > 0)
                {
                    foreach (var pair in _strippedLights)
                    {
                        if (pair.Key != null)
                        {
                            pair.Key.shadows = pair.Value;
                        }
                    }
                    _strippedLights.Clear();
                }
                return;
            }

            // Re-applied about once a second, since alarm lights and the like
            // only become active later, and the game may recreate lights.
            if (Time.unscaledTime < _nextLightScan)
            {
                return;
            }
            _nextLightScan = Time.unscaledTime + 1f;

            foreach (var light in FindObjectsOfType<Light>())
            {
                if (light.type == LightType.Spot && light.shadows != LightShadows.None)
                {
                    _strippedLights[light] = light.shadows;
                    light.shadows = LightShadows.None;
                }
            }
        }
    }
}
