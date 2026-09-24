using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Motion controller input through SteamVR Input. The game is described to
    /// SteamVR by an action manifest (what the mod wants: hand poses, triggers,
    /// sticks, buttons) plus default bindings for Quest/Touch controllers.
    /// Anyone can remap buttons in SteamVR's own controller-binding screen, and
    /// other controllers (e.g. Steam Frame) get their bindings there too.
    /// This file reads the actions each frame and places one laser pointer per
    /// hand; feeding the results into the game's own input comes next.
    /// </summary>
    internal partial class VrManager
    {
        internal struct HandState
        {
            public bool PoseValid;
            public Vector3 TrackPos;      // raw tracking-space pose (Unity coordinates)
            public Quaternion TrackRot;
            public Vector3 WorldPos;      // in the game world, after the same mapping the head gets
            public Quaternion WorldRot;
            public bool Trigger;
            public bool Grip;
            public bool StickClick;
        }

        internal HandState LeftHand;
        internal HandState RightHand;
        internal Vector2 MoveStick;       // left stick
        internal Vector2 TurnStick;       // right stick
        internal bool ButtonA, ButtonB, ButtonX, ButtonY;

        private bool _inputReady;
        private ulong _actionSet;
        private ulong _hPoseL, _hPoseR, _hTrigL, _hTrigR, _hGripL, _hGripR, _hMove, _hTurn;
        private ulong _hA, _hB, _hX, _hY, _hStickClickL, _hStickClickR;
        private readonly Dictionary<string, bool> _previousDigital = new Dictionary<string, bool>();
        private static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
        private static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
        private static readonly uint PoseSize = (uint)Marshal.SizeOf(typeof(InputPoseActionData_t));
        private const ETrackingUniverseOrigin TrackingSpace = ETrackingUniverseOrigin.TrackingUniverseStanding;

        private void InitVrInput()
        {
            try
            {
                OpenVR.Compositor.SetTrackingSpace(TrackingSpace);

                // The manifest and default bindings are copied next to the DLL by the build.
                var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var manifestPath = System.IO.Path.Combine(dir, "nuclearesvr_actions.json");
                if (!File.Exists(manifestPath))
                {
                    Plugin.Logger.LogWarning($"SteamVR Input: {manifestPath} is missing - controllers won't work.");
                    return;
                }

                var input = OpenVR.Input;
                if (input == null)
                {
                    Plugin.Logger.LogWarning("SteamVR Input is not available - controllers won't work.");
                    return;
                }
                var error = input.SetActionManifestPath(manifestPath);
                Plugin.Logger.LogInfo($"SteamVR Input: manifest '{manifestPath}' -> {error}");
                if (error != EVRInputError.None)
                {
                    return;
                }

                var ok = Handle("/actions/main", ref _actionSet);
                ok &= Handle("/actions/main/in/PoseLeft", ref _hPoseL);
                ok &= Handle("/actions/main/in/PoseRight", ref _hPoseR);
                ok &= Handle("/actions/main/in/TriggerLeft", ref _hTrigL);
                ok &= Handle("/actions/main/in/TriggerRight", ref _hTrigR);
                ok &= Handle("/actions/main/in/GripLeft", ref _hGripL);
                ok &= Handle("/actions/main/in/GripRight", ref _hGripR);
                ok &= Handle("/actions/main/in/Move", ref _hMove);
                ok &= Handle("/actions/main/in/Turn", ref _hTurn);
                ok &= Handle("/actions/main/in/ButtonA", ref _hA);
                ok &= Handle("/actions/main/in/ButtonB", ref _hB);
                ok &= Handle("/actions/main/in/ButtonX", ref _hX);
                ok &= Handle("/actions/main/in/ButtonY", ref _hY);
                ok &= Handle("/actions/main/in/StickClickLeft", ref _hStickClickL);
                ok &= Handle("/actions/main/in/StickClickRight", ref _hStickClickR);
                _inputReady = ok;
                Plugin.Logger.LogInfo(ok
                    ? "SteamVR Input ready: controllers will be read every frame."
                    : "SteamVR Input: some actions were not found - see the errors above.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"SteamVR Input setup failed: {ex}");
                _inputReady = false;
            }
        }

        private static bool Handle(string name, ref ulong handle)
        {
            var error = name == "/actions/main"
                ? OpenVR.Input.GetActionSetHandle(name, ref handle)
                : OpenVR.Input.GetActionHandle(name, ref handle);
            if (error != EVRInputError.None)
            {
                Plugin.Logger.LogWarning($"SteamVR Input: handle for {name} failed: {error}");
                return false;
            }
            return true;
        }

        private bool Digital(string name, ulong handle)
        {
            var data = new InputDigitalActionData_t();
            var error = OpenVR.Input.GetDigitalActionData(handle, ref data, DigitalSize, OpenVR.k_ulInvalidInputValueHandle);
            var state = error == EVRInputError.None && data.bActive && data.bState;
            _previousDigital.TryGetValue(name, out var previous);
            if (state != previous)
            {
                _previousDigital[name] = state;
                Plugin.Logger.LogInfo($"[input] {name} {(state ? "DOWN" : "up")}");
            }
            return state;
        }

        private Vector2 Analog(ulong handle)
        {
            var data = new InputAnalogActionData_t();
            var error = OpenVR.Input.GetAnalogActionData(handle, ref data, AnalogSize, OpenVR.k_ulInvalidInputValueHandle);
            return error == EVRInputError.None && data.bActive ? new Vector2(data.x, data.y) : Vector2.zero;
        }

        private bool ReadPose(ulong handle, out Vector3 position, out Quaternion rotation)
        {
            var data = new InputPoseActionData_t();
            var error = OpenVR.Input.GetPoseActionDataForNextFrame(handle, TrackingSpace, ref data, PoseSize, OpenVR.k_ulInvalidInputValueHandle);
            if (error == EVRInputError.None && data.bActive && data.pose.bPoseIsValid)
            {
                data.pose.mDeviceToAbsoluteTracking.ToUnity(out position, out rotation);
                return true;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>Once per frame, right after WaitGetPoses.</summary>
        private void UpdateVrInput()
        {
            if (!_inputReady)
            {
                return;
            }
            var sets = new[]
            {
                new VRActiveActionSet_t { ulActionSet = _actionSet, ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle, nPriority = 0 }
            };
            var error = OpenVR.Input.UpdateActionState(sets, (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t)));
            if (error != EVRInputError.None)
            {
                LogThrottled("input-update-error", $"SteamVR Input UpdateActionState: {error}");
                return;
            }

            LeftHand.PoseValid = ReadPose(_hPoseL, out LeftHand.TrackPos, out LeftHand.TrackRot);
            RightHand.PoseValid = ReadPose(_hPoseR, out RightHand.TrackPos, out RightHand.TrackRot);
            LeftHand.Trigger = Digital("LeftTrigger", _hTrigL);
            RightHand.Trigger = Digital("RightTrigger", _hTrigR);
            LeftHand.Grip = Digital("LeftGrip", _hGripL);
            RightHand.Grip = Digital("RightGrip", _hGripR);
            LeftHand.StickClick = Digital("LeftStickClick", _hStickClickL);
            RightHand.StickClick = Digital("RightStickClick", _hStickClickR);
            ButtonA = Digital("A", _hA);
            ButtonB = Digital("B", _hB);
            ButtonX = Digital("X", _hX);
            ButtonY = Digital("Y", _hY);
            MoveStick = Analog(_hMove);
            TurnStick = Analog(_hTurn);

            LogThrottled("input-state", $"[input] leftPose={LeftHand.PoseValid} rightPose={RightHand.PoseValid} " +
                                        $"move={MoveStick} turn={TurnStick}");
        }

        // ---- hand placement and laser pointers ----

        private GameObject _leftLaser, _rightLaser;
        private Material _laserIdle, _laserActive;

        private static GameObject MakeLaser(int layer, Material material)
        {
            var root = new GameObject("NuclearesVR_Laser");
            root.layer = layer;
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            beam.name = "Beam";
            beam.transform.SetParent(root.transform, false);
            beam.layer = layer;
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "Dot";
            dot.transform.SetParent(root.transform, false);
            dot.layer = layer;
            foreach (var part in new[] { beam, dot })
            {
                var collider = part.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider); // must never interfere with the game's raycasts
                }
                var renderer = part.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return root;
        }

        /// <summary>
        /// After head tracking has been applied to the camera. Hands go through
        /// the same mapping the head gets (the tracking-space pose relative to
        /// the recenter pose, on top of the game camera's own base pose), so
        /// they line up with the eyes. Also draws the lasers.
        /// </summary>
        private void PositionHands()
        {
            if (!_inputReady || _mainCamera == null)
            {
                return;
            }
            if (_laserIdle == null)
            {
                _laserIdle = MakeUnlitColor(new Color(0.35f, 0.85f, 1f));
                _laserActive = MakeUnlitColor(new Color(0.3f, 1f, 0.35f));
            }
            if (_leftLaser == null) _leftLaser = MakeLaser(_mirrorLayer, _laserIdle);
            if (_rightLaser == null) _rightLaser = MakeLaser(_mirrorLayer, _laserIdle);

            var parent = _mainCamera.transform.parent;
            var pitch = Quaternion.Euler(Plugin.PointerPitchDegrees.Value, 0f, 0f);
            MapHand(ref LeftHand, parent, pitch);
            MapHand(ref RightHand, parent, pitch);
            ShowLaser(_leftLaser, LeftHand);
            ShowLaser(_rightLaser, RightHand);
        }

        private void MapHand(ref HandState hand, Transform parent, Quaternion pitch)
        {
            if (!hand.PoseValid)
            {
                return;
            }
            var localPos = _baseLocalPosition + MapPosition(hand.TrackPos);
            var localRot = _savedBaseRotation * MapRotation(hand.TrackRot) * pitch;
            hand.WorldPos = parent != null ? parent.TransformPoint(localPos) : localPos;
            hand.WorldRot = parent != null ? parent.rotation * localRot : localRot;
        }

        private const float LaserMaxLength = 4f;

        private void ShowLaser(GameObject laser, HandState hand)
        {
            if (laser == null)
            {
                return;
            }
            if (laser.activeSelf != hand.PoseValid)
            {
                laser.SetActive(hand.PoseValid);
            }
            if (!hand.PoseValid)
            {
                return;
            }

            var forward = hand.WorldRot * Vector3.forward;
            var length = LaserMaxLength;
            if (Physics.Raycast(hand.WorldPos, forward, out var hit, LaserMaxLength, ~0, QueryTriggerInteraction.Ignore) && hit.distance > 0.05f)
            {
                length = hit.distance;
            }
            if (_mirrorVisible && TryHitVirtualScreen(hand, out _, out var screenDistance) && screenDistance < length)
            {
                length = screenDistance;
            }
            laser.transform.SetPositionAndRotation(hand.WorldPos, hand.WorldRot);

            var beam = laser.transform.Find("Beam");
            var dot = laser.transform.Find("Dot");
            beam.localPosition = new Vector3(0f, 0f, length * 0.5f);
            beam.localScale = new Vector3(0.004f, 0.004f, length);
            dot.localPosition = new Vector3(0f, 0f, length);
            dot.localScale = Vector3.one * 0.02f;

            var material = hand.Trigger ? _laserActive : _laserIdle;
            beam.GetComponent<MeshRenderer>().sharedMaterial = material;
            dot.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private void HideHands()
        {
            if (_leftLaser != null) _leftLaser.SetActive(false);
            if (_rightLaser != null) _rightLaser.SetActive(false);
            _inputReady = false;
        }
    }
}
