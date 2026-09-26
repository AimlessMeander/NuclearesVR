using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game can draw its water two ways: a real-time fluid simulation (ZibraAI "Liquid"), or a plain flat water
    /// surface at the right level (the game's "advanced water simulation" option). The simulation crashed the game
    /// at random in VR (a null read inside ZibraLiquidNative_Win.dll, which keeps one set of GPU textures shared
    /// between all cameras) and looks different in each eye and over the lasers and menu, so while VR runs the mod
    /// switches the game to the flat water, as if that option were off. The player's own setting is put back when VR
    /// stops, so playing on the monitor is unchanged.
    /// </summary>
    internal partial class VrManager
    {
        private static readonly MethodInfo ApplySimulatedWater =
            AccessTools.Method(typeof(CConfiguracion.COpciones), "ActivarAguaSimulada", new[] { typeof(bool) });

        private bool _restoreSimulatedWater;
        private float _nextWaterCheck;

        /// <summary>Called every frame the eye cameras are synced; checks about once a second.</summary>
        private void UseFlatWater()
        {
            if (Time.unscaledTime < _nextWaterCheck || !CConfiguracion.COpciones.AguaSimulada)
            {
                return;
            }
            _nextWaterCheck = Time.unscaledTime + 1f;
            _restoreSimulatedWater = true;
            CConfiguracion.COpciones.AguaSimulada = false;
            if (ApplySimulatedWater != null)
            {
                ApplySimulatedWater.Invoke(null, new object[] { false });
                Plugin.Logger.LogInfo("Water: switched to the flat water surface for VR.");
            }
            else
            {
                Plugin.Logger.LogWarning("Water: could not find the game's water switch; the water simulation may still run.");
            }
        }

        private void RestoreSimulatedWaterSetting()
        {
            if (_restoreSimulatedWater)
            {
                _restoreSimulatedWater = false;
                CConfiguracion.COpciones.AguaSimulada = true;
            }
        }
    }
}
