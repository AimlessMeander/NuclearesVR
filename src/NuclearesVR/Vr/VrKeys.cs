using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Keys and axes the controllers "press" on the game's behalf. The game
    /// reads its controls through Unity's legacy Input (Input.GetKey with the
    /// player's mapped keys, Input.GetAxisRaw("Mouse X") for turning), so the
    /// patches below add the controllers' state on top of the real keyboard and
    /// mouse. Edges (key down / key up) are stamped with the frame the game
    /// will read them in, since the controllers are sampled at the end of the
    /// previous frame.
    /// </summary>
    internal static class VrKeys
    {
        private static readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
        private static readonly Dictionary<KeyCode, int> DownFrame = new Dictionary<KeyCode, int>();
        private static readonly Dictionary<KeyCode, int> UpFrame = new Dictionary<KeyCode, int>();

        /// <summary>Extra "Mouse X" the game's mouse look sees, from the right stick.</summary>
        internal static float TurnInput;

        /// <summary>
        /// Added to Input.mousePosition while the trigger is held: how far the hand has moved since
        /// the click, in pixels. Dials, sliders and valves read the mouse moving while dragged.
        /// </summary>
        internal static Vector2 MouseOffset;

        /// <summary>True while a controller is doing the pointing: instant activation applies.</summary>
        internal static bool PointingActive;

        internal static void Apply(HashSet<KeyCode> wanted)
        {
            var visibleFrame = Time.frameCount + 1;
            foreach (var key in wanted)
            {
                if (Held.Add(key))
                {
                    DownFrame[key] = visibleFrame;
                }
            }
            foreach (var key in new List<KeyCode>(Held))
            {
                if (!wanted.Contains(key))
                {
                    Held.Remove(key);
                    UpFrame[key] = visibleFrame;
                }
            }
        }

        internal static void Clear()
        {
            Held.Clear();
            DownFrame.Clear();
            UpFrame.Clear();
            TurnInput = 0f;
            MouseOffset = Vector2.zero;
            PointingActive = false;
        }

        internal static bool IsHeld(KeyCode key) => Held.Contains(key);
        internal static bool WentDown(KeyCode key) => DownFrame.TryGetValue(key, out var f) && f == Time.frameCount;
        internal static bool WentUp(KeyCode key) => UpFrame.TryGetValue(key, out var f) && f == Time.frameCount;
    }

    /// <summary>Harmony patches that feed <see cref="VrKeys"/> into the game's input reads.</summary>
    internal static class InputPatches
    {
        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKey", new[] { typeof(KeyCode) }), nameof(GetKeyPostfix));
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKeyDown", new[] { typeof(KeyCode) }), nameof(GetKeyDownPostfix));
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKeyUp", new[] { typeof(KeyCode) }), nameof(GetKeyUpPostfix));
            Patch(harmony, AccessTools.Method(typeof(Input), "GetAxisRaw", new[] { typeof(string) }), nameof(GetAxisPostfix));
            Patch(harmony, AccessTools.Method(typeof(Input), "GetAxis", new[] { typeof(string) }), nameof(GetAxisPostfix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Interface.CTeclas), "SinPosicionar"), nameof(SinPosicionarPostfix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Input), "mousePosition"), nameof(MousePositionPostfix));
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodBase original, string postfixName)
        {
            try
            {
                if (original == null)
                {
                    Plugin.Logger.LogWarning($"Input patch '{postfixName}': target method not found.");
                    return;
                }
                harmony.Patch(original, postfix: new HarmonyMethod(typeof(InputPatches), postfixName));
                Plugin.Logger.LogInfo($"Input patch applied: {original.DeclaringType?.Name}.{original.Name}");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"Input patch '{postfixName}' failed: {ex}");
            }
        }

        private static void GetKeyPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && VrKeys.IsHeld(key)) __result = true;
        }

        private static void GetKeyDownPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && VrKeys.WentDown(key)) __result = true;
        }

        private static void GetKeyUpPostfix(KeyCode key, ref bool __result)
        {
            if (!__result && VrKeys.WentUp(key)) __result = true;
        }

        private static void GetAxisPostfix(string axisName, ref float __result)
        {
            if (VrKeys.TurnInput != 0f && axisName == "Mouse X")
            {
                __result += VrKeys.TurnInput;
            }
        }

        private static void MousePositionPostfix(ref Vector3 __result)
        {
            if (VrKeys.MouseOffset != Vector2.zero)
            {
                __result.x += VrKeys.MouseOffset.x;
                __result.y += VrKeys.MouseOffset.y;
            }
        }

        // The game's "hold shift while clicking to skip the hand animation".
        private static void SinPosicionarPostfix(ref bool __result)
        {
            if (VrKeys.PointingActive && Plugin.InstantActivation.Value) __result = true;
        }
    }
}
