using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Phase 1: 6DoF head tracking only. Adds two extra "eye" cameras as children
    /// of the game's existing PlayerCamera and drives that camera's local
    /// position/rotation from the HMD pose, on top of whatever the game's own
    /// mouse-look already computed that frame (see design note in README.md -
    /// this is additive, not a replacement, so mouse/gamepad turning and the
    /// game's own "look at this gauge" focus mode keep working unmodified).
    /// Fails gracefully (logs and stays inactive) if no headset/runtime is found,
    /// so the game is unaffected when played without a headset connected.
    /// </summary>
    internal class VrManager : MonoBehaviour
    {
        /// <summary>
        /// Call only after a scene has actually finished loading (see
        /// Plugin.OnFirstSceneLoaded) - Nucleares' very first bootstrap scene
        /// gets torn down and replaced almost immediately as part of normal
        /// startup, and creating a persistent object during that transition
        /// (even attached to BepInEx's own manager object) got swept up and
        /// destroyed before Unity ever ticked it once. Once past that point,
        /// creating our own dedicated persistent object is safe and simpler
        /// than depending on BepInEx's manager object's own lifecycle.
        /// </summary>
        public static void Bootstrap()
        {
            var go = new GameObject("NuclearesVR_Manager");
            DontDestroyOnLoad(go);
            go.AddComponent<VrManager>();
        }

        private bool _active;
        private CVRSystem _system;
        private readonly TrackedDevicePose_t[] _renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        private static readonly TrackedDevicePose_t[] EmptyPoseArray = new TrackedDevicePose_t[0];

        private Camera _mainCamera;
        private Camera _leftEyeCamera;
        private Camera _rightEyeCamera;
        private RenderTexture _leftTex;
        private RenderTexture _rightTex;
        private Vector3 _baseLocalPosition;

        private Vector3 _zeroPos;
        private Quaternion _zeroRot = Quaternion.identity;
        private bool _haveZeroPose;

        // PlayerLook only rewrites PlayerCamera.transform.localRotation when the
        // physical mouse actually moved that frame (see HandleNormalMode's
        // `if (Mathf.Abs(mouseY) > 0.001f ...)` guard) - when it's stationary
        // (the normal case while wearing a headset), the transform just keeps
        // whatever we last wrote to it. So we can't use the transform as our
        // "base" without reading back our own previous output and integrating
        // our head delta into a runaway spin - instead we read the game's
        // actual authoritative pitch state directly via reflection each frame.
        private static readonly FieldInfo CurrentVerticalRotationField =
            typeof(PlayerLook).GetField("currentVerticalRotation", BindingFlags.NonPublic | BindingFlags.Instance);

        private float _lastThrottledLogTime;

        private void LogThrottled(string message)
        {
            if (Time.unscaledTime - _lastThrottledLogTime < 2f)
            {
                return;
            }
            _lastThrottledLogTime = Time.unscaledTime;
            Plugin.Logger.LogInfo(message);
        }

        private void Awake()
        {
            Plugin.Logger.LogInfo($"VrManager.Awake on GameObject '{gameObject.name}' (instance {GetInstanceID()})");
            TryInitOpenVr();
        }

        private void OnEnable()
        {
            Plugin.Logger.LogInfo("VrManager.OnEnable");
        }

        private void OnDisable()
        {
            Plugin.Logger.LogInfo("VrManager.OnDisable - Update will stop running until re-enabled.");
        }

        private void OnDestroy()
        {
            Plugin.Logger.LogInfo("VrManager.OnDestroy");
            if (_active)
            {
                OpenVR.Shutdown();
            }
        }

        private void TryInitOpenVr()
        {
            try
            {
                if (!OpenVR.IsHmdPresent())
                {
                    Plugin.Logger.LogInfo("No HMD detected. NuclearesVR staying inactive (flatscreen play unaffected).");
                    return;
                }

                var error = EVRInitError.None;
                _system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Scene);
                if (error != EVRInitError.None)
                {
                    Plugin.Logger.LogWarning($"OpenVR.Init failed: {OpenVR.GetStringForHmdError(error)}. Staying inactive.");
                    return;
                }

                uint w = 0, h = 0;
                _system.GetRecommendedRenderTargetSize(ref w, ref h);
                Plugin.Logger.LogInfo($"OpenVR initialized. Recommended per-eye render size: {w}x{h}");

                _leftTex = new RenderTexture((int)w, (int)h, 24, RenderTextureFormat.Default) { antiAliasing = 1 };
                _rightTex = new RenderTexture((int)w, (int)h, 24, RenderTextureFormat.Default) { antiAliasing = 1 };
                _leftTex.Create();
                _rightTex.Create();
                Plugin.Logger.LogInfo($"Eye render textures created: left.IsCreated={_leftTex.IsCreated()}, right.IsCreated={_rightTex.IsCreated()}");

                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                {
                    Plugin.Logger.LogWarning($"Graphics API is {SystemInfo.graphicsDeviceType}, expected Direct3D11. " +
                                              "Frame submission may not work - if the headset shows a black/frozen view, " +
                                              "this is why.");
                }

                StartCoroutine(SubmitLoop());
                _active = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"OpenVR init threw, staying inactive: {ex}");
                _active = false;
            }
        }

        private bool _loggedFirstUpdate;

        private void Update()
        {
            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                Plugin.Logger.LogInfo($"VrManager.Update is running (first tick). active={_active}");
            }

            if (!_active)
            {
                return;
            }

            try
            {
                var playerLook = PlayerLook.Instancia;
                var cam = playerLook != null ? playerLook.GetCamera() : null;
                if (cam != null && cam != _mainCamera)
                {
                    BuildEyeCameras(cam);
                }

                LogThrottled($"[heartbeat] active={_active} playerLookFound={playerLook != null} " +
                             $"cameraFound={cam != null} eyeCamerasBuilt={_leftEyeCamera != null} " +
                             $"camName={(cam != null ? cam.name : "-")} camInstance={(cam != null ? cam.GetInstanceID().ToString() : "-")} " +
                             $"camWorldPos={(cam != null ? cam.transform.position.ToString() : "-")}");

                if (Input.GetKeyDown(KeyCode.End))
                {
                    Recenter();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"NuclearesVR Update error: {ex}");
            }
        }

        private void BuildEyeCameras(Camera main)
        {
            try
            {
                DestroyEyeCameras();

                _mainCamera = main;
                _baseLocalPosition = main.transform.localPosition;

                _leftEyeCamera = CreateEyeCamera(main, "NuclearesVR_LeftEye", EVREye.Eye_Left, _leftTex);
                _rightEyeCamera = CreateEyeCamera(main, "NuclearesVR_RightEye", EVREye.Eye_Right, _rightTex);

                _haveZeroPose = false; // force a recenter on the next pose update
                Plugin.Logger.LogInfo($"VR eye cameras attached to '{main.name}' (instance {main.GetInstanceID()}), " +
                                       $"worldPos={main.transform.position}, localPos={main.transform.localPosition}, " +
                                       $"parent='{(main.transform.parent != null ? main.transform.parent.name : "none")}', " +
                                       $"cullingMask={main.cullingMask}, near={main.nearClipPlane}, far={main.farClipPlane}");

                // If the game uses camera stacking (a base camera plus one or
                // more overlay cameras contributing extra objects to the final
                // image), an object could be invisible in our eye cameras
                // simply because we only cloned the base camera - list every
                // camera in the scene so we can check for that.
                var allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                foreach (var c in allCameras)
                {
                    Plugin.Logger.LogInfo($"  scene camera: '{c.name}' enabled={c.enabled} depth={c.depth} " +
                                           $"cullingMask={c.cullingMask} clearFlags={c.clearFlags} " +
                                           $"targetTexture={(c.targetTexture != null ? c.targetTexture.name : "none")}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Failed to build VR eye cameras: {ex}");
            }
        }

        private Camera CreateEyeCamera(Camera main, string name, EVREye eye, RenderTexture target)
        {
            var go = new GameObject(name);
            go.transform.SetParent(main.transform, worldPositionStays: false);

            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.targetTexture = target;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.rect = new Rect(0, 0, 1, 1);

            // CopyFrom only copies Camera settings, not other components on the
            // same GameObject - if the main camera has a per-camera Skybox
            // component override (common for custom time-of-day skies, rather
            // than relying on the single scene-wide RenderSettings.skybox),
            // that override doesn't carry over, and this fresh GameObject falls
            // back to the (possibly unset) global skybox, rendering black.
            if (main.TryGetComponent<Skybox>(out var mainSkybox) && mainSkybox.material != null)
            {
                var eyeSkybox = go.AddComponent<Skybox>();
                eyeSkybox.material = mainSkybox.material;
            }

            // Unity's occlusion culling extracts frustum planes from the
            // projection matrix, and gets confused by the asymmetric/off-center
            // frustum a VR eye camera needs (unlike a normal symmetric FOV
            // camera) - it can end up culling geometry that's actually visible,
            // which looks like walls disappearing or objects flickering in and
            // out as the head moves. Bypassing it entirely trades some
            // performance for correct rendering.
            cam.useOcclusionCulling = false;

            // Deliberately NOT using GL.GetGPUProjectionMatrix here - it was
            // tried (on both the raw OpenVR matrix and a Unity-Frustum-built
            // one) specifically to fix a vertical flip, and both times it also
            // corrupted rendering (see-through geometry, misaligned objects),
            // while the flip itself flipped back and forth depending on the
            // input rather than actually getting fixed. That function's
            // presence correlated with the corruption regardless of the input
            // matrix, most likely from interacting badly with Direct3D11's
            // reversed depth buffer, which Unity uses by default. The raw
            // matrix here renders correctly - the vertical flip is instead
            // corrected at submission time via VRTextureBounds_t (see
            // SubmitLoop), which doesn't touch rendering at all.
            var projection = _system.GetProjectionMatrix(eye, main.nearClipPlane, main.farClipPlane);
            cam.projectionMatrix = projection.ToMatrix4x4();

            var eyeToHead = _system.GetEyeToHeadTransform(eye);
            eyeToHead.ToUnity(out var eyePos, out var eyeRot);
            cam.transform.localPosition = eyePos;
            cam.transform.localRotation = eyeRot;

            return cam;
        }

        private void DestroyEyeCameras()
        {
            if (_leftEyeCamera != null) Destroy(_leftEyeCamera.gameObject);
            if (_rightEyeCamera != null) Destroy(_rightEyeCamera.gameObject);
            _leftEyeCamera = null;
            _rightEyeCamera = null;
        }

        private void Recenter()
        {
            if (!TryGetHmdPose(out var pos, out var rot))
            {
                return;
            }
            _zeroPos = pos;
            _zeroRot = rot;
            _haveZeroPose = true;
            Plugin.Logger.LogInfo("VR view recentered.");
        }

        private bool TryGetHmdPose(out Vector3 position, out Quaternion rotation)
        {
            var hmdPose = _renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!hmdPose.bPoseIsValid)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            hmdPose.mDeviceToAbsoluteTracking.ToUnity(out position, out rotation);
            return true;
        }

        private void LateUpdate()
        {
            if (!_active || _mainCamera == null)
            {
                return;
            }

            try
            {
                var playerLook = PlayerLook.Instancia;
                if (playerLook == null)
                {
                    return;
                }
                if (playerLook.MirarActivo)
                {
                    // The game's own "look at this gauge" coroutine is driving
                    // localRotation directly right now - don't fight it.
                    return;
                }

                OpenVR.Compositor.WaitGetPoses(_renderPoses, EmptyPoseArray);

                if (!TryGetHmdPose(out var pos, out var rot))
                {
                    return;
                }

                if (!_haveZeroPose)
                {
                    _zeroPos = pos;
                    _zeroRot = rot;
                    _haveZeroPose = true;
                }

                var deltaRot = Quaternion.Inverse(_zeroRot) * rot;
                var deltaPos = Quaternion.Inverse(_zeroRot) * (pos - _zeroPos);

                // Defensive: a bad pose or a math error here sends the camera
                // somewhere nonsensical (black screen, camera inside geometry)
                // with no visible warning otherwise. 5m is far beyond any real
                // room-scale movement, and a non-unit-norm quaternion means the
                // rotation math produced garbage.
                const float maxDelta = 5f;
                var qNorm = Mathf.Sqrt(deltaRot.x * deltaRot.x + deltaRot.y * deltaRot.y +
                                        deltaRot.z * deltaRot.z + deltaRot.w * deltaRot.w);
                if (deltaPos.magnitude > maxDelta || Mathf.Abs(qNorm - 1f) > 0.01f)
                {
                    LogThrottled($"Rejecting bad VR pose delta (pos={deltaPos}, |q|={qNorm:F3}) - " +
                                 "leaving camera untouched this frame.");
                    return;
                }

                LogThrottled($"HMD pos={pos} rot={rot.eulerAngles} deltaPos={deltaPos} deltaRotEuler={deltaRot.eulerAngles}");

                // Rotation: compose our head-tracking delta onto the game's
                // *authoritative* pitch state (read via reflection), not onto
                // whatever the transform currently holds - PlayerLook only
                // rewrites the transform when the mouse actually moved that
                // frame, so reading the transform back here would integrate our
                // own delta into a runaway spin whenever the mouse is idle
                // (which is always, in VR).
                float basePitch = 0f;
                if (CurrentVerticalRotationField != null)
                {
                    basePitch = (float)CurrentVerticalRotationField.GetValue(playerLook);
                }
                else
                {
                    LogThrottled("currentVerticalRotation field not found via reflection - " +
                                 "falling back to 0 pitch base (game update may have renamed it).");
                }
                var baseLocalRotation = Quaternion.Euler(basePitch, 0f, 0f);
                _mainCamera.transform.localRotation = baseLocalRotation * deltaRot;

                // Position: the game never touches localPosition per-frame, so we
                // track our own cached base instead of accumulating.
                _mainCamera.transform.localPosition = _baseLocalPosition + deltaPos;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"NuclearesVR LateUpdate error: {ex}");
            }
        }

        private IEnumerator SubmitLoop()
        {
            // vMin/vMax swapped (rather than 0/1) to flip the image vertically
            // at submission time - see the comment in CreateEyeCamera for why
            // this is done here instead of via the projection matrix.
            var bounds = new VRTextureBounds_t { uMin = 0, vMin = 1, uMax = 1, vMax = 0 };
            while (true)
            {
                yield return new WaitForEndOfFrame();

                if (!_active || _leftEyeCamera == null || _rightEyeCamera == null)
                {
                    continue;
                }

                try
                {
                    var leftTexT = new Texture_t
                    {
                        handle = _leftTex.GetNativeTexturePtr(),
                        eType = ETextureType.DirectX,
                        eColorSpace = EColorSpace.Auto
                    };
                    var rightTexT = new Texture_t
                    {
                        handle = _rightTex.GetNativeTexturePtr(),
                        eType = ETextureType.DirectX,
                        eColorSpace = EColorSpace.Auto
                    };

                    var leftErr = OpenVR.Compositor.Submit(EVREye.Eye_Left, ref leftTexT, ref bounds, EVRSubmitFlags.Submit_Default);
                    var rightErr = OpenVR.Compositor.Submit(EVREye.Eye_Right, ref rightTexT, ref bounds, EVRSubmitFlags.Submit_Default);
                    if (leftErr != EVRCompositorError.None || rightErr != EVRCompositorError.None)
                    {
                        LogThrottled($"Compositor.Submit returned an error: left={leftErr}, right={rightErr}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"VR frame submit error: {ex}");
                }
            }
        }
    }
}
