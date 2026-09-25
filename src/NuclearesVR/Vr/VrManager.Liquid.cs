using System;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's water simulation (ZibraAI "Liquid") renders once per camera, from a callback on every
    /// camera that is about to render, and keeps ONE shared set of GPU textures for all of them, resized
    /// to suit each camera as it comes up. With the game's main camera (window size, e.g. 3840x2160)
    /// and the two eye cameras (2064x2208) all rendering every frame, those shared textures were being
    /// destroyed and recreated over and over, which is the likely cause of the random graphics-driver
    /// crashes (most often in the control room, where the simulation is active).
    ///
    /// The simulation skips a camera whose culling mask does not include the layer the simulation's
    /// GameObject is on. So while VR runs, that layer is removed from the game's main camera (which only
    /// feeds the monitor and the screen capture) and kept for the eye cameras: the simulation then only
    /// ever sees the two eye cameras, which are the same size. The water still shows in the headset; it
    /// no longer shows in the monitor mirror.
    /// </summary>
    internal partial class VrManager
    {
        private Type _liquidType;
        private bool _searchedForLiquidType;
        private int _liquidLayerMask;
        private float _nextLiquidScan;
        private bool _mainMaskStripped;
        private int _strippedBits;

        // Layers that must never be hidden from the main camera (Default, UI): if the simulation were
        // on one of these, hiding it would blank the monitor view and the menus.
        private const int NeverStripLayers = (1 << 0) | (1 << 5);

        private bool LiquidStripSafe => (_liquidLayerMask & NeverStripLayers) == 0;

        /// <summary>The eye cameras are ordinary cameras (the simulation draws for them) unless the setting is off or stripping is not safe.</summary>
        private bool LiquidVisibleInEyes => Plugin.LiquidInVr.Value && LiquidStripSafe;

        private void UpdateLiquidLayers()
        {
            if (Time.unscaledTime < _nextLiquidScan)
            {
                return;
            }
            _nextLiquidScan = Time.unscaledTime + 1f;

            if (!_searchedForLiquidType)
            {
                _searchedForLiquidType = true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _liquidType = assembly.GetType("com.zibra.liquid.Solver.ZibraLiquid", throwOnError: false);
                    if (_liquidType != null)
                    {
                        break;
                    }
                }
                if (_liquidType == null)
                {
                    Plugin.Logger.LogInfo("Water simulation type not found - nothing to work around.");
                }
            }
            if (_liquidType == null)
            {
                return;
            }

            var mask = 0;
            foreach (var instance in FindObjectsOfType(_liquidType))
            {
                if (instance is Component component)
                {
                    mask |= 1 << component.gameObject.layer;
                }
            }
            if (mask != _liquidLayerMask)
            {
                _liquidLayerMask = mask;
                Plugin.Logger.LogInfo($"Water simulation objects are on layer mask {mask} " +
                                      (LiquidStripSafe ? "(hidden from the game's main camera while VR runs)." : "(Default/UI layer - cannot be hidden safely; headset cameras will skip the simulation instead)."));
            }
        }

        /// <summary>Called every frame after the eye cameras have copied the main camera's culling mask.</summary>
        private void ApplyLiquidMask()
        {
            if (!LiquidVisibleInEyes || _liquidLayerMask == 0 || _mainCamera == null)
            {
                RestoreMainMask();
                return;
            }
            _mainCamera.cullingMask &= ~_liquidLayerMask;
            _mainMaskStripped = true;
            _strippedBits = _liquidLayerMask;
        }

        private void RestoreMainMask()
        {
            if (_mainMaskStripped && _mainCamera != null)
            {
                _mainCamera.cullingMask |= _strippedBits;
            }
            _mainMaskStripped = false;
        }
    }
}
