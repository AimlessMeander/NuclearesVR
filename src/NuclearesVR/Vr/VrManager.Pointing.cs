using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Points and clicks with the controllers by making the game think a mouse
    /// is doing it. Everything interactive in Nucleares (switches, dials,
    /// tooltips) is Unity's OnMouseEnter/OnMouseDown, which raycasts from the
    /// camera through the mouse position, and the tablet's UI works the same
    /// way. So:
    ///   - the mouse is held at the centre of the game window, so the ray is
    ///     straight along the camera's forward;
    ///   - at the end of every frame the game camera is moved to the active
    ///     controller, and Unity works out what's under the "mouse" at the start
    ///     of the next frame from there; then it's put back before the game's
    ///     scripts run (see Update), so nothing else notices;
    ///   - the trigger sends a genuine left mouse button press to the window.
    /// The active hand is whichever one last pulled its trigger. Sticks and
    /// buttons become virtual key presses (see <see cref="VrKeys"/>).
    /// </summary>
    internal partial class VrManager
    {
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern System.IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern bool GetClientRect(System.IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(System.IntPtr hwnd, ref POINT point);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;

        // ---- head-relative walking ----
        // The head offset is applied on top of the game camera, which follows the player body. To walk
        // where the head faces, the body is turned to the head's heading while the stick is pushed, and
        // the same angle is taken out of the head offset (_yawAdjust) so the view does not change.
        private float _yawAdjust;
        private float _lastHeadYaw;
        private bool _haveHeadYaw;
        private bool _movingLastFrame;
        private static readonly System.Reflection.FieldInfo HorizontalRotationField =
            HarmonyLib.AccessTools.Field(typeof(PlayerLook), "currentHorizontalRotation");

        private Quaternion MapRotation(Quaternion trackRot)
        {
            var delta = Quaternion.Inverse(_zeroRot) * trackRot;
            if (_yawAdjust == 0f)
            {
                return delta;
            }
            var basis = _savedBaseRotation;
            return Quaternion.Inverse(basis) * Quaternion.Euler(0f, -_yawAdjust, 0f) * basis * delta;
        }

        private Vector3 MapPosition(Vector3 trackPos)
        {
            var delta = Quaternion.Inverse(_zeroRot) * (trackPos - _zeroPos);
            return _yawAdjust == 0f ? delta : Quaternion.Euler(0f, -_yawAdjust, 0f) * delta;
        }

        /// <summary>After the head has been applied to the camera: remember which way it faces.</summary>
        private void RememberHeadYaw()
        {
            var forward = _mainCamera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.04f)
            {
                _lastHeadYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                _haveHeadYaw = true;
            }
        }

        /// <summary>Start of the frame, before the game's scripts run.</summary>
        private void AlignBodyToHead()
        {
            if (!_movingLastFrame || !_haveHeadYaw || !Plugin.WalkTowardsHead.Value || !_inputReady || InCrane)
            {
                return;
            }
            var look = PlayerLook.Instancia;
            if (look == null || look.MirarActivo || _mirrorVisible)
            {
                return;
            }
            var body = look.transform;
            var turn = Mathf.DeltaAngle(body.eulerAngles.y, _lastHeadYaw);
            if (Mathf.Abs(turn) < 0.25f)
            {
                return;
            }
            body.Rotate(0f, turn, 0f, Space.World);
            HorizontalRotationField?.SetValue(look, Mathf.DeltaAngle(0f, body.eulerAngles.y));
            _yawAdjust += turn;
        }

        // ---- pointing at the virtual menu screen ----

        /// <summary>
        /// Where a hand's ray meets the virtual screen, as a position on the desktop in pixels
        /// (from the top left), and how far along the ray it is.
        /// </summary>
        private bool TryHitVirtualScreen(HandState hand, out Vector2 pixel, out float distance)
        {
            pixel = Vector2.zero;
            distance = 0f;
            if (_mirrorQuad == null || !_mirrorQuad.activeInHierarchy || !hand.PoseValid)
            {
                return false;
            }
            var quad = _mirrorQuad.transform;
            var normal = quad.forward;
            var direction = hand.WorldRot * Vector3.forward;
            var facing = Vector3.Dot(direction, normal);
            if (Mathf.Abs(facing) < 1e-4f)
            {
                return false;
            }
            distance = Vector3.Dot(quad.position - hand.WorldPos, normal) / facing;
            if (distance <= 0f)
            {
                return false;
            }
            var local = quad.InverseTransformPoint(hand.WorldPos + direction * distance);
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f)
            {
                return false;
            }
            if (!TryGetWindowRect(out var origin, out var size))
            {
                return false;
            }
            pixel = new Vector2(origin.x + (local.x + 0.5f) * size.x, origin.y + (0.5f - local.y) * size.y);
            return true;
        }

        private static bool TryGetWindowRect(out Vector2 origin, out Vector2 size)
        {
            origin = Vector2.zero;
            size = Vector2.zero;
            var window = GetActiveWindow();
            if (window == System.IntPtr.Zero || !GetClientRect(window, out var rect))
            {
                return false;
            }
            var topLeft = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(window, ref topLeft))
            {
                return false;
            }
            origin = new Vector2(topLeft.X, topLeft.Y);
            size = new Vector2(rect.Right - rect.Left, rect.Bottom - rect.Top);
            return size.x > 0f && size.y > 0f;
        }

        private bool _activeHandIsRight = true;
        private bool _previousLeftTrigger, _previousRightTrigger;
        private bool _mouseDown;
        private Vector3 _dragOrigin;
        private const float DragDeadzoneMeters = 0.015f;
        private bool _pointerPoseApplied;
        private readonly HashSet<KeyCode> _wantedKeys = new HashSet<KeyCode>();

        private const float StickPressThreshold = 0.5f;
        private const float TurnDeadzone = 0.2f;

        /// <summary>Pointing is only meaningful in a running game, not on the virtual menu screen.</summary>
        private bool PointingAllowed =>
            _inputReady && Plugin.PointingEnabled.Value && PlayerLook.Instancia != null && !_mirrorVisible &&
            _mainCamera != null && _rotationApplied;

        private HandState ActiveHand => _activeHandIsRight ? RightHand : LeftHand;

        /// <summary>Once per frame in LateUpdate, after the hands have been positioned.</summary>
        private void UpdateControllerActions()
        {
            if (!_inputReady)
            {
                return;
            }

            // The hand that most recently pulled its trigger points.
            if (RightHand.Trigger && !_previousRightTrigger) _activeHandIsRight = true;
            else if (LeftHand.Trigger && !_previousLeftTrigger) _activeHandIsRight = false;
            _previousLeftTrigger = LeftHand.Trigger;
            _previousRightTrigger = RightHand.Trigger;

            var allowed = PointingAllowed;
            VrKeys.PointingActive = allowed;

            var hand = ActiveHand;

            // On the pause menu the controller is the mouse cursor on the virtual screen.
            var onMenu = false;
            if (_inputReady && _mirrorVisible && Application.isFocused &&
                TryHitVirtualScreen(hand, out var menuPixel, out _))
            {
                SetCursorPos(Mathf.RoundToInt(menuPixel.x), Mathf.RoundToInt(menuPixel.y));
                onMenu = true;
            }

            var wantDown = (allowed || onMenu) && hand.PoseValid && hand.Trigger && Application.isFocused;

            // Dragging: while the trigger is held, the game's mouse position moves with the hand, so
            // dials, sliders and valves turn as they do when dragged with a mouse.
            if (wantDown && allowed)
            {
                if (!_mouseDown)
                {
                    _dragOrigin = hand.WorldPos;
                    _dragOut = Vector2.zero;
                }
                var yaw = _leftEyeCamera != null ? _leftEyeCamera.transform.eulerAngles.y : 0f;
                var right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
                var moved = hand.WorldPos - _dragOrigin;
                // The offset only follows the hand once it is more than the dead zone away from the last
                // value, and it trails the hand by that distance. So tremor and small drifts change
                // nothing, and reversing needs a deliberate move back. The game's 3-position switches
                // step one way or the other on ANY change of the horizontal position, so without this
                // they flipped at random.
                _dragOut.x = Backlash(_dragOut.x, Vector3.Dot(moved, right));
                _dragOut.y = Backlash(_dragOut.y, moved.y);
                VrKeys.MouseOffset = _dragOut * Plugin.DragPixelsPerMeter.Value;
                VrKeys.Dragging = true;
            }
            else
            {
                // Keep the last offset for a few frames after letting go, so the game sees the button
                // come up before the position moves. Zeroing it at once looked like a drag back the
                // other way, and flipped 3-position switches the wrong way on release.
                if (_mouseDown)
                {
                    _releaseHoldFrames = 6;
                }
                else if (_releaseHoldFrames > 0)
                {
                    _releaseHoldFrames--;
                }
                if (_releaseHoldFrames <= 0)
                {
                    VrKeys.MouseOffset = Vector2.zero;
                    VrKeys.Dragging = false;
                }
            }

            if (wantDown != _mouseDown)
            {
                mouse_event(wantDown ? MouseLeftDown : MouseLeftUp, 0, 0, 0, System.UIntPtr.Zero);
                _mouseDown = wantDown;
            }

            _wantedKeys.Clear();
            if (MoveStick.y > StickPressThreshold) _wantedKeys.Add(CConfiguracion.COpciones.CMapeo.KeyCode_Adelante);
            if (MoveStick.y < -StickPressThreshold) _wantedKeys.Add(CConfiguracion.COpciones.CMapeo.KeyCode_Atras);
            if (MoveStick.x > StickPressThreshold) _wantedKeys.Add(CConfiguracion.COpciones.CMapeo.KeyCode_Derecha);
            if (MoveStick.x < -StickPressThreshold) _wantedKeys.Add(CConfiguracion.COpciones.CMapeo.KeyCode_Izquierda);
            var inCrane = InCrane;
            if (inCrane != _wasInCrane)
            {
                _wasInCrane = inCrane;
                Plugin.Logger.LogInfo(inCrane ? "[crane] seated - crane controls on" : "[crane] left the seat - normal controls");
            }
            if (inCrane)
            {
                // Sitting in the crane seat the buttons do crane things instead (the game's fixed keys).
                AddMapped(ButtonA, KeyCode.Z);      // open / close the grabber
                AddMapped(ButtonB, KeyCode.F);      // laser sight
                AddMapped(ButtonY, KeyCode.Space);  // next camera
                AddMapped(ButtonX, KeyCode.X);      // leave the crane
                // Right stick forward / back moves the grabber up / down (the game's own up / down keys).
                if (TurnStick.y > StickPressThreshold) _wantedKeys.Add(CConfiguracion.COpciones.CMapeo.KeyCode_Subir);
                if (TurnStick.y < -StickPressThreshold) _wantedKeys.Add(CConfiguracion.COpciones.CMapeo.KeyCode_Bajar);
            }
            else
            {
                AddMapped(ButtonA, Plugin.KeyA.Value);
                AddMapped(ButtonB, Plugin.KeyB.Value);
                AddMapped(ButtonX, Plugin.KeyX.Value);
                AddMapped(ButtonX, Plugin.KeyXAlso.Value);
                AddMapped(ButtonY, Plugin.KeyY.Value);
            }
            AddMapped(LeftHand.Grip, Plugin.KeyLeftGrip.Value);
            AddMapped(RightHand.Grip, Plugin.KeyRightGrip.Value);
            AddMapped(LeftHand.StickClick, Plugin.KeyLeftStick.Value);
            AddMapped(RightHand.StickClick, Plugin.KeyRightStick.Value);
            VrKeys.Apply(_wantedKeys);

            VrKeys.SetAim(allowed && hand.PoseValid, hand.WorldPos, hand.WorldRot,
                          _mainCamera != null ? _mainCamera.transform : null);

            // Holding a grip is the right mouse button (detail box on gauges, switch guards, zoom).
            VrKeys.SetRightMouse(allowed && Plugin.GripIsRightClick.Value && (LeftHand.Grip || RightHand.Grip));

            // Right stick turns you, through the game's own mouse look.
            _movingLastFrame = allowed && (MoveStick.sqrMagnitude > StickPressThreshold * StickPressThreshold);

            var turn = Mathf.Abs(TurnStick.x) > TurnDeadzone ? TurnStick.x : 0f;
            VrKeys.TurnInput = turn * Plugin.TurnSpeed.Value;
        }

        private static readonly System.Reflection.FieldInfo CranePlayerField =
            HarmonyLib.AccessTools.Field(typeof(controlCrane), "_jugador");

        /// <summary>True while the local player is sitting in the crane's control seat.</summary>
        internal static bool InCrane
        {
            get
            {
                try
                {
                    return controlCrane.Instancia != null && CranePlayerField != null &&
                           CranePlayerField.GetValue(controlCrane.Instancia) != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        private bool _wasInCrane;
        private Vector2 _dragOut;
        private int _releaseHoldFrames;

        private static float Backlash(float current, float target)
        {
            if (target - current > DragDeadzoneMeters) return target - DragDeadzoneMeters;
            if (target - current < -DragDeadzoneMeters) return target + DragDeadzoneMeters;
            return current;
        }

        private void AddMapped(bool pressed, KeyCode key)
        {
            if (pressed && key != KeyCode.None)
            {
                _wantedKeys.Add(key);
            }
        }

        /// <summary>
        /// End of frame, after rendering: aim the game camera along the active
        /// controller so the game's mouse handling in the next frame sees it,
        /// and keep the OS cursor at the window centre.
        /// </summary>
        private void ApplyPointerPose()
        {
            if (!PointingAllowed)
            {
                return;
            }
            var hand = ActiveHand;
            if (!hand.PoseValid)
            {
                return;
            }
            _mainCamera.transform.SetPositionAndRotation(hand.WorldPos, hand.WorldRot);
            _pointerPoseApplied = true;
            CenterCursor();
        }

        /// <summary>Undo <see cref="ApplyPointerPose"/> before the game's scripts run.</summary>
        private void RestorePointerPose()
        {
            if (!_pointerPoseApplied)
            {
                return;
            }
            _pointerPoseApplied = false;
            if (_mainCamera != null)
            {
                _mainCamera.transform.localPosition = _baseLocalPosition;
                _mainCamera.transform.localRotation = _savedBaseRotation;
            }
        }

        private static void CenterCursor()
        {
            if (!Application.isFocused)
            {
                return;
            }
            var window = GetActiveWindow();
            if (window == System.IntPtr.Zero || !GetClientRect(window, out var rect))
            {
                return;
            }
            var centre = new POINT { X = (rect.Right - rect.Left) / 2, Y = (rect.Bottom - rect.Top) / 2 };
            if (ClientToScreen(window, ref centre))
            {
                SetCursorPos(centre.X, centre.Y);
            }
        }

        /// <summary>On shutdown: don't leave a mouse button or key held.</summary>
        private void ReleaseControllerActions()
        {
            if (_mouseDown)
            {
                mouse_event(MouseLeftUp, 0, 0, 0, System.UIntPtr.Zero);
                _mouseDown = false;
            }
            VrKeys.Clear();
            _pointerPoseApplied = false;
        }
    }
}
