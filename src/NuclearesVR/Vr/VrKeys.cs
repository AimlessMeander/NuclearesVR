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

        /// <summary>
        /// The active controller's ray, and the game camera it replaces. The game finds "the component
        /// I am looking at" by casting from the camera's own transform in its Update; while pointing,
        /// those casts use this ray instead (see the Rayo patches).
        /// </summary>
        internal static bool AimValid;
        internal static Vector3 AimOrigin;
        internal static Vector3 AimDirection;
        internal static Transform AimCamera;

        internal static void SetAim(bool valid, Vector3 origin, Vector3 direction, Transform camera)
        {
            AimValid = valid;
            AimOrigin = origin;
            AimDirection = direction;
            AimCamera = camera;
        }

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

        private static bool _rightHeld;
        private static int _rightDownFrame = -1, _rightUpFrame = -1;

        /// <summary>The right mouse button, as pressed by a controller.</summary>
        internal static void SetRightMouse(bool pressed)
        {
            if (pressed == _rightHeld)
            {
                return;
            }
            _rightHeld = pressed;
            if (pressed) _rightDownFrame = Time.frameCount + 1;
            else _rightUpFrame = Time.frameCount + 1;
        }

        internal static bool RightHeld => _rightHeld;
        internal static bool RightWentDown => _rightDownFrame == Time.frameCount;
        internal static bool RightWentUp => _rightUpFrame == Time.frameCount;

        internal static void Clear()
        {
            _rightHeld = false;
            _rightDownFrame = _rightUpFrame = -1;
            Held.Clear();
            DownFrame.Clear();
            UpFrame.Clear();
            TurnInput = 0f;
            AimValid = false;
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
            Patch(harmony, AccessTools.Method(typeof(Input), "GetMouseButton", new[] { typeof(int) }), nameof(MouseButtonPostfix));
            Patch(harmony, AccessTools.Method(typeof(Input), "GetMouseButtonDown", new[] { typeof(int) }), nameof(MouseButtonDownPostfix));
            Patch(harmony, AccessTools.Method(typeof(Input), "GetMouseButtonUp", new[] { typeof(int) }), nameof(MouseButtonUpPostfix));
            Patch(harmony, AccessTools.Method(typeof(Interface), "Rayo", new[] { typeof(Transform) }), nameof(RayoPrefix), prefix: true);
            Patch(harmony, AccessTools.Method(typeof(Interface), "Rayo", new[] { typeof(Transform), typeof(LayerMask) }), nameof(RayoLayersPrefix), prefix: true);
            Patch(harmony, AccessTools.Method(typeof(Interface), "GetObjetoEnLaMira"), nameof(ObjetoEnLaMiraPrefix), prefix: true);
            Patch(harmony, AccessTools.Method(typeof(Interface), "RayoDesdeCentroPantalla"), nameof(CentroPantallaPrefix), prefix: true);
            Patch(harmony, AccessTools.PropertyGetter(typeof(Interface.CTeclas), "SinPosicionar"), nameof(SinPosicionarPostfix));
            Patch(harmony, AccessTools.PropertyGetter(typeof(Input), "mousePosition"), nameof(MousePositionPostfix));
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodBase original, string postfixName, bool prefix = false)
        {
            try
            {
                if (original == null)
                {
                    Plugin.Logger.LogWarning($"Input patch '{postfixName}': target method not found.");
                    return;
                }
                var method = new HarmonyMethod(typeof(InputPatches), postfixName);
                if (prefix)
                {
                    harmony.Patch(original, prefix: method);
                }
                else
                {
                    harmony.Patch(original, postfix: method);
                }
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

        private static GameObject CastAim(float distance, int mask)
        {
            return Physics.Raycast(VrKeys.AimOrigin, VrKeys.AimDirection, out var hit, distance, mask)
                ? hit.collider.transform.gameObject
                : null;
        }

        private static bool RayoPrefix(Transform pos, ref GameObject __result)
        {
            if (!VrKeys.AimValid || pos != VrKeys.AimCamera) return true;
            __result = CastAim(float.PositiveInfinity, Physics.DefaultRaycastLayers);
            return false;
        }

        private static bool RayoLayersPrefix(Transform pos, LayerMask layers, ref GameObject __result)
        {
            if (!VrKeys.AimValid || pos != VrKeys.AimCamera) return true;
            __result = CastAim(150f, layers);
            return false;
        }

        private static bool ObjetoEnLaMiraPrefix(ref GameObject __result)
        {
            if (!VrKeys.AimValid) return true;
            __result = CastAim(float.PositiveInfinity, Physics.DefaultRaycastLayers);
            return false;
        }

        private static bool CentroPantallaPrefix(ref Collider __result)
        {
            if (!VrKeys.AimValid) return true;
            __result = Physics.Raycast(VrKeys.AimOrigin, VrKeys.AimDirection, out var hit, float.PositiveInfinity, ~(1 << 6))
                ? hit.collider
                : null;
            return false;
        }

        private static void MouseButtonPostfix(int button, ref bool __result)
        {
            if (button == 1 && !__result && VrKeys.RightHeld) __result = true;
        }

        private static void MouseButtonDownPostfix(int button, ref bool __result)
        {
            if (button == 1 && !__result && VrKeys.RightWentDown) __result = true;
        }

        private static void MouseButtonUpPostfix(int button, ref bool __result)
        {
            if (button == 1 && !__result && VrKeys.RightWentUp) __result = true;
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
