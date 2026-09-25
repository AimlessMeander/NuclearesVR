using System;
using System.Collections;
using System.Collections.Generic;
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
    [DefaultExecutionOrder(-32000)]
    internal partial class VrManager : MonoBehaviour
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

        /// <summary>True while VR is running (the game is in a headset session).</summary>
        internal static bool VrRunning { get; private set; }
        private CVRSystem _system;
        private readonly TrackedDevicePose_t[] _renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        private static readonly TrackedDevicePose_t[] EmptyPoseArray = new TrackedDevicePose_t[0];

        private Camera _mainCamera;
        private Camera _leftEyeCamera;
        private Camera _rightEyeCamera;
        private RenderTexture _leftTex;
        private RenderTexture _rightTex;
        private Vector3 _baseLocalPosition;

        // "Virtual monitor" shown before/outside gameplay (main menu, ESC pause
        // menu) - Nucleares' menus are built on a UI canvas that's structurally
        // invisible to any Camera (see README), so instead of trying to render
        // the UI directly, this captures whatever's actually on the monitor
        // each frame (via ScreenCapture, which grabs the real composited
        // output - 3D scene *and* UI overlay together) onto a flat quad
        // positioned in front of the headset view.
        private GameObject _mirrorQuad;
        private RenderTexture _mirrorTex;
        private bool _mirrorVisible;

        private Vector3 _zeroPos;
        private Quaternion _zeroRot = Quaternion.identity;
        private bool _haveZeroPose;

        // How our head-tracking rotation coexists with the game's own camera
        // code: each frame, at the very start (Update, ordered before every
        // other script - see DefaultExecutionOrder on the class), we put the
        // camera's localRotation back to exactly what it was *before* we
        // added our head offset last frame. The game's scripts then run
        // against that clean base - mouse-look, and the Mirar focus-on-object
        // coroutine (used by the tablet) which slerps from whatever rotation
        // it finds - and only afterwards, in LateUpdate, do we re-apply our
        // offset on top. Never reading back our own contaminated output is
        // what avoids both the earlier runaway-spin bug (PlayerLook leaves the
        // transform untouched while the mouse is idle) and the tug-of-war a
        // slerp toward its target would otherwise have with our offset.
        private Quaternion _savedBaseRotation = Quaternion.identity;
        private bool _rotationApplied;

        // Keyed per call site (not a single shared timestamp) - a single
        // shared gate meant whichever LogThrottled call happened to run first
        // each window (in practice, always the [heartbeat] one from Update())
        // silently starved out every other channel, including [submitloop]
        // below, which as a result never once appeared in a log despite the
        // code running fine.
        private readonly Dictionary<string, float> _lastThrottledLogTimes = new Dictionary<string, float>();

        private void LogThrottled(string channel, string message)
        {
            if (_lastThrottledLogTimes.TryGetValue(channel, out var last) && Time.unscaledTime - last < 2f)
            {
                return;
            }
            _lastThrottledLogTimes[channel] = Time.unscaledTime;
            Plugin.Logger.LogInfo(message);
        }

        private void Awake()
        {
            Plugin.Logger.LogInfo($"VrManager.Awake on GameObject '{gameObject.name}' (instance {GetInstanceID()})");
            // OpenVR is deliberately not started here. Starting it makes the
            // headset switch to this game's view straight away, and the game's
            // start menu doesn't work well in VR; until a game is loaded the
            // game stays an ordinary desktop window (Steam Link / Big Picture
            // shows it in 2D). See the start in Update.
        }

        private bool _initTried;
        private bool _skippedVr;

        /// <summary>
        /// Starting OpenVR launches SteamVR if it is not running, which nobody wants when playing on the
        /// monitor. In Auto mode VR only starts if SteamVR's server is already running (as it is when
        /// the game is started from inside SteamVR, e.g. over Steam Link).
        /// </summary>
        private static bool VrWanted(out string reason)
        {
            switch (Plugin.StartMode.Value)
            {
                case VrStartMode.Never:
                    reason = "VrMode is Never.";
                    return false;
                case VrStartMode.Always:
                    reason = "";
                    return true;
                default:
                    try
                    {
                        if (System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0)
                        {
                            reason = "";
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"Could not check whether SteamVR is running ({ex.Message}); assuming yes.");
                        reason = "";
                        return true;
                    }
                    reason = "SteamVR is not running (set VrMode to Always in the config to launch it automatically).";
                    return false;
            }
        }
        private bool _shuttingDown;
        private float _noPlayerSince = -1f;
        private const float LeaveGameGraceSeconds = 1.5f;

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

        /// <summary>
        /// Leaves VR cleanly (used when the player exits to the start menu) so
        /// SteamVR lets go of the headset and the menu is a normal 2D window,
        /// and so a later game load can start VR again. Order matters: stop
        /// submitting frames first, shut OpenVR down, and only then free the
        /// textures the compositor was reading - freeing them while it might
        /// still use them crashes inside the graphics driver.
        /// </summary>
        private void ShutdownVr()
        {
            Plugin.Logger.LogInfo("Back at the start menu - shutting VR down.");
            _active = false;
            VrRunning = false;
            RemoveMonitorMirror();
            _shuttingDown = true;
            StopAllCoroutines();
            _mirrorVisible = false;
            if (_mirrorQuad != null) _mirrorQuad.SetActive(false);
            if (_cursorArrow != null) _cursorArrow.SetActive(false);
            if (_worldMarker != null) _worldMarker.SetActive(false);
            HideHands();
            HideInfoPanel();
            StopFarSmallObjects();
            RestoreVSync();
            RestoreShadowDistance();
            ReleaseControllerActions();
            DestroyEyeCameras();
            _mainCamera = null;
            _rotationApplied = false;
            StartCoroutine(FinishShutdown());
        }

        private System.Collections.IEnumerator FinishShutdown()
        {
            // Let the camera destruction and any in-flight frame finish.
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
            try
            {
                OpenVR.Shutdown();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"OpenVR.Shutdown threw: {ex}");
            }
            _system = null;
            yield return null;
            yield return null;
            ReleaseRetiredTextures(all: true);
            if (_leftTex != null) { _leftTex.Release(); Destroy(_leftTex); _leftTex = null; }
            if (_rightTex != null) { _rightTex.Release(); Destroy(_rightTex); _rightTex = null; }
            _initTried = false;
            _shuttingDown = false;
            Plugin.Logger.LogInfo("VR shut down. It will start again when a game is loaded.");
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

                _recommendedWidth = (int)w;
                _recommendedHeight = (int)h;
                var displayError = ETrackedPropertyError.TrackedProp_Success;
                var hz = _system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref displayError);
                _headsetHz = hz > 20f ? hz : 90f;
                _currentScale = Plugin.RenderScale.Value;
                _currentMsaa = Plugin.EyeMsaa.Value;
                _dynWindowStart = 0f;
                _leftTex = MakeEyeTexture(_currentScale, _currentMsaa);
                _rightTex = MakeEyeTexture(_currentScale, _currentMsaa);
                Plugin.Logger.LogInfo($"Eye textures {_leftTex.width}x{_leftTex.height} (render scale {_currentScale:F2}), MSAA {_currentMsaa}x, headset {_headsetHz:F0} Hz, " +
                                      $"dynamic resolution {(Plugin.DynamicResolution.Value ? "on" : "off")}.");
                Plugin.Logger.LogInfo($"Eye render textures created: left.IsCreated={_leftTex.IsCreated()}, right.IsCreated={_rightTex.IsCreated()}");

                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
                {
                    Plugin.Logger.LogWarning($"Graphics API is {SystemInfo.graphicsDeviceType}, expected Direct3D11. " +
                                              "Frame submission may not work - if the headset shows a black/frozen view, " +
                                              "this is why.");
                }

                SetUpMirrorScreen();
                InitVrInput();
                StartCoroutine(SubmitLoop());
                _noPlayerSince = -1f;
                _active = true;
                VrRunning = true;
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
                // Start VR the first time a game is actually loaded (the
                // player exists), not at the start menu. One attempt only.
                if (!_initTried && !_shuttingDown && PlayerLook.Instancia != null)
                {
                    _initTried = true;
                    if (VrWanted(out var reason))
                    {
                        Plugin.Logger.LogInfo("A game is loaded - starting VR now.");
                        TryInitOpenVr();
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"A game is loaded - not starting VR: {reason}");
                        _skippedVr = true;
                    }
                }
                else if (_skippedVr && PlayerLook.Instancia == null)
                {
                    // Back at the menu: decide again at the next game load.
                    _initTried = false;
                    _skippedVr = false;
                }
                if (!_active)
                {
                    return;
                }
            }

            RestorePointerPose();

            if (_rotationApplied)
            {
                if (_mainCamera != null)
                {
                    _mainCamera.transform.localRotation = _savedBaseRotation;
                }
                _rotationApplied = false;
            }

            AlignBodyToHead();

            // Back at the start menu (no player for a moment): hand the headset
            // back so the menu is a normal 2D window again. The grace period
            // covers the brief gaps while a game loads.
            if (PlayerLook.Instancia == null)
            {
                if (_noPlayerSince < 0f)
                {
                    _noPlayerSince = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _noPlayerSince > LeaveGameGraceSeconds)
                {
                    ShutdownVr();
                    return;
                }
            }
            else
            {
                _noPlayerSince = -1f;
            }

            try
            {
                // Before a game is loaded (main menu, loading screens) there's
                // no PlayerLook yet, so fall back to whatever camera the menu
                // itself uses - this is a plain static view (no head-tracking;
                // see the MirarActivo-style early-out in LateUpdate, which
                // still requires PlayerLook), but it means the headset shows
                // the menu instead of nothing at all. Once a game loads,
                // PlayerLook's camera takes over automatically since it'll
                // differ from whatever we're currently attached to.
                //
                // Camera.main requires the "MainCamera" tag, which the menu
                // camera turned out not to have (confirmed via logging -
                // Camera.main returned null for the whole menu period even
                // though the menu clearly renders via *some* camera) - so we
                // search directly for whatever's actually enabled instead.
                var playerLook = PlayerLook.Instancia;
                var cam = playerLook != null ? playerLook.GetCamera() : FindActiveGameCamera();
                if (cam != null && cam != _mainCamera)
                {
                    BuildEyeCameras(cam);
                }

                LogThrottled("heartbeat", $"[heartbeat] active={_active} playerLookFound={playerLook != null} " +
                             $"cameraFound={cam != null} eyeCamerasBuilt={_leftEyeCamera != null} " +
                             $"camName={(cam != null ? cam.name : "-")} camInstance={(cam != null ? cam.GetInstanceID().ToString() : "-")} " +
                             $"camWorldPos={(cam != null ? cam.transform.position.ToString() : "-")}");

                // Virtual monitor: shown whenever there's no gameplay camera yet
                // (main menu / loading) or the game's own pause menu is up -
                // both cases are built on a UI canvas our eye cameras can't
                // capture directly (see SetUpMirrorScreen's comment).
                // The quad and cursor arrow are children of whichever camera
                // we're attached to, so the game destroying that camera (menu
                // to game, back to menu) takes them with it - rebuild if so.
                if (_mirrorTex != null && _mirrorQuad == null)
                {
                    SetUpMirrorScreen();
                    _mirrorVisible = false;
                    if (_mainCamera != null)
                    {
                        ParentMirrorObjects(_mainCamera.transform);
                    }
                }

                var wantMirror = _mirrorQuad != null && (playerLook == null || CHistoria.Pausada || MenuOrDialogOpen());
                if (wantMirror != _mirrorVisible)
                {
                    _mirrorVisible = wantMirror;
                    _mirrorQuad.SetActive(wantMirror);
                }

                CheckDiagnosticsKey();
                UpdateLightingWorkarounds();
                UpdateMonitorTextShaders();

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

        /// <summary>
        /// A menu or confirmation dialog that does not pause the game (the "put on the suit?" question,
        /// pop-up help) is on the screen overlay, so it gets the virtual screen and a cursor as well.
        /// </summary>
        private static bool MenuOrDialogOpen()
        {
            try
            {
                return Interface.IsHayMenuEnPantalla;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Finds whatever camera is actually rendering the game right now, for
        /// use before PlayerLook exists (main menu, loading screens). Excludes
        /// our own eye cameras and the depth=80 viewmodel/overlay cameras seen
        /// in the scene camera dump (CameraMochila, CameraArmaEnMano) - those
        /// render on top of a base camera, not a full standalone view, so
        /// they're not something we want to build a VR view from directly.
        /// Prefers the highest camera.depth among what's left, matching Unity's
        /// own rendering order (higher depth draws last/on top).
        /// </summary>
        private Camera FindActiveGameCamera()
        {
            Camera best = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (!c.enabled || c.gameObject == null)
                {
                    continue;
                }
                if (c == _leftEyeCamera || c == _rightEyeCamera)
                {
                    continue;
                }
                if (c.depth >= 80f)
                {
                    continue;
                }
                if (best == null || c.depth > best.depth)
                {
                    best = c;
                }
            }
            return best;
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

                ParentMirrorObjects(main.transform);

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
            // Inactive until the camera effects below are copied in, so their
            // Awake/OnEnable see the copied settings (see CopyCameraEffects).
            go.SetActive(false);
            go.transform.SetParent(main.transform, worldPositionStays: false);

            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.targetTexture = target;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.rect = new Rect(0, 0, 1, 1);

            // clearFlags/cullingMask/Skybox are kept in sync with main every
            // frame in SyncEyeCameraSettings (LateUpdate) rather than copied
            // once here - see that method's comment for why.

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

            CopyCameraEffects(main, cam);
            go.SetActive(true);

            return cam;
        }

        private void DestroyEyeCameras()
        {
            RemoveMonitorMirror();
            RestoreMainMask();
            _eyeNear = _eyeFar = -1f;
            _appliedLayerDistance = -1f;
            if (_leftEyeCamera != null) Destroy(_leftEyeCamera.gameObject);
            if (_rightEyeCamera != null) Destroy(_rightEyeCamera.gameObject);
            _effectPairs.Clear();
            _leftEyeCamera = null;
            _rightEyeCamera = null;
        }

        private const float MirrorScreenDistance = 2f;
        private const float MirrorScreenHeight = 1.24f;

        /// <summary>
        /// Nucleares' menus (main menu, ESC pause menu) are built on a UI
        /// canvas that's structurally invisible to any Camera - Unity's
        /// Screen Space - Overlay canvases draw straight to the final screen
        /// output, bypassing the whole camera/rendering pipeline entirely, so
        /// no amount of eye-camera configuration can capture them (confirmed
        /// by decompiling the menu's own source - MenuInicioAnimacionCamara's
        /// CamMenuInicial only ever renders the 3D decorative backdrop behind
        /// the buttons, never the buttons themselves).
        ///
        /// Rather than reconfigure those canvases at runtime (risky - could
        /// break the actual on-screen menu for flatscreen play), this instead
        /// captures whatever's *already* on the monitor each frame - the real
        /// composited output, 3D scene and UI overlay together - via
        /// ScreenCapture, and displays it on a plain flat quad positioned in
        /// front of the headset view. Works for any UI state, not just menus.
        /// </summary>
        // Assigned in SetUpMirrorScreen. The mirror quad lives on this layer,
        // which is included in our eye cameras' cullingMask (see
        // SyncOneEyeCamera) but deliberately NOT in the real main camera's -
        // otherwise the main camera (which renders to your actual monitor)
        // would render the quad too, and since we capture the monitor's own
        // output onto that same quad's texture, that created a feedback loop:
        // each captured frame already contained the previous frame's quad,
        // compounding into a doubled/scrambled image every frame.
        private int _mirrorLayer = -1;
        private Material _mirrorMaterial;

        // CaptureScreenshotIntoRenderTexture expects a texture the same size as
        // the screen - an earlier half-size version only ended up covering a
        // corner of the screen (the pause menu, centered on the real screen,
        // landed clipped at the edge of the capture). Recreated if the window
        // is ever resized.
        private void ResizeMirrorTextureIfNeeded()
        {
            if (_mirrorTex == null || (_mirrorTex.width == Screen.width && _mirrorTex.height == Screen.height))
            {
                return;
            }
            var w = Mathf.Max(1, Screen.width);
            var h = Mathf.Max(1, Screen.height);
            _mirrorTex.Release();
            _mirrorTex.width = w;
            _mirrorTex.height = h;
            _mirrorTex.Create();
            if (_mirrorQuad != null)
            {
                _mirrorQuad.transform.localScale = new Vector3(MirrorScreenHeight * w / h, MirrorScreenHeight, 1f);
            }
            Plugin.Logger.LogInfo($"Mirror screen resized to {w}x{h}.");
        }

        /// <summary>
        /// Picks a Unity layer (0-31) that has no name assigned in this
        /// project, on the assumption that an unnamed layer is unused - not a
        /// hard guarantee, but the best signal available without access to
        /// the project's actual layer configuration. Searches from 31 down
        /// since high layer numbers are conventionally left free for exactly
        /// this kind of runtime/tooling use, and logs a warning if even that
        /// is already named (in use), since then we're picking blind.
        /// </summary>
        private int PickUnusedLayer()
        {
            for (var i = 31; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    return i;
                }
            }
            Plugin.Logger.LogWarning("Every layer 8-31 is named/in use - falling back to layer 31 anyway; " +
                                      "the mirror screen may become visible on the flatscreen monitor too.");
            return 31;
        }

        private void SetUpMirrorScreen()
        {
            try
            {
                _mirrorLayer = PickUnusedLayer();

                var mirrorWidth = Mathf.Max(1, Screen.width);
                var mirrorHeight = Mathf.Max(1, Screen.height);
                if (_mirrorTex == null)
                {
                    _mirrorTex = new RenderTexture(mirrorWidth, mirrorHeight, 0, RenderTextureFormat.Default);
                    _mirrorTex.Create();
                }

                if (_mirrorQuad != null)
                {
                    Destroy(_mirrorQuad);
                }
                if (_mirrorMaterial != null)
                {
                    Destroy(_mirrorMaterial);
                }
                _mirrorQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _mirrorQuad.name = "NuclearesVR_MirrorScreen";
                _mirrorQuad.layer = _mirrorLayer;
                var collider = _mirrorQuad.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider); // don't want this floating quad interfering with the game's own raycasts
                }

                var aspect = (float)mirrorWidth / mirrorHeight;
                _mirrorQuad.transform.localScale = new Vector3(MirrorScreenHeight * aspect, MirrorScreenHeight, 1f);

                var renderer = _mirrorQuad.GetComponent<MeshRenderer>();
                _mirrorMaterial = new Material(Shader.Find("Unlit/Texture")) { mainTexture = _mirrorTex };
                // ScreenCapture's output is stored vertically flipped on Direct3D;
                // flip it back via the UV transform rather than touching the pixels.
                _mirrorMaterial.mainTextureScale = new Vector2(1f, -1f);
                _mirrorMaterial.mainTextureOffset = new Vector2(0f, 1f);
                renderer.material = _mirrorMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _mirrorQuad.SetActive(false);
                SetUpPointerObjects();
                Plugin.Logger.LogInfo($"Mirror screen set up at {mirrorWidth}x{mirrorHeight} on layer {_mirrorLayer}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Failed to set up mirror screen: {ex}");
            }
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
            _yawAdjust = 0f;
            Plugin.Logger.LogInfo("VR view recentered.");
        }

        /// <summary>
        /// CreateEyeCamera's CopyFrom(main) is a one-time snapshot, but the game
        /// changes some of the main camera's settings at runtime - e.g.
        /// clearFlags likely toggles between SolidColor and Skybox depending on
        /// whether the player is indoors or outdoors (gestionExterior.cs sets
        /// RenderSettings.skybox on period/weather changes), and cullingMask may
        /// similarly change which layers are visible in different areas. If our
        /// eye cameras were built while indoors, they'd be frozen at whatever
        /// clearFlags/cullingMask applied then, missing the sky (black instead
        /// of Skybox) and any objects on layers added to main's cullingMask
        /// later (a plausible explanation for a train + track only missing in
        /// the eye cameras, not the main camera). Re-syncing every frame is
        /// cheap and avoids needing to know exactly when/why the game changes
        /// these.
        /// </summary>
        private void SyncEyeCameraSettings()
        {
            if (_leftEyeCamera == null || _rightEyeCamera == null)
            {
                return;
            }
            SyncEyeClipPlanes();
            SyncEyeLayerDistances();
            UpdateLiquidLayers();
            UpdateGameMask();
            SyncOneEyeCamera(_leftEyeCamera);
            SyncOneEyeCamera(_rightEyeCamera);
            ApplyMainMask();
        }

        private float _eyeNear = -1f, _eyeFar = -1f;
        private float _appliedLayerDistance = -1f;

        /// <summary>
        /// Limits how far objects on the Default layer are drawn in the headset (see the setting): the
        /// benchmark showed the cost of a heavy view is almost entirely thousands of Default-layer
        /// objects in the distance. Terrain, sky and the other layers keep the camera's own far plane.
        /// </summary>
        private void SyncEyeLayerDistances()
        {
            var distance = _benchFar > 0f ? _benchFar : Plugin.EyeDefaultLayerDistance.Value;
            if (Mathf.Approximately(distance, _appliedLayerDistance))
            {
                return;
            }
            _appliedLayerDistance = distance;
            var distances = new float[32]; // 0 = use the camera's far plane
            distances[0] = distance;
            _leftEyeCamera.layerCullDistances = distances;
            _rightEyeCamera.layerCullDistances = distances;
            Plugin.Logger.LogInfo($"Headset cameras: objects on the Default layer are drawn up to {(distance > 0f ? distance.ToString("F0") + " m" : "the full distance")}.");
        }

        /// <summary>
        /// The game changes its camera's draw distance as the player moves between areas (a short one
        /// indoors keeps rendering cheap). The eye cameras' projection is built by hand, so it has to be
        /// rebuilt when that changes - it used to be fixed at whatever the distance was when the eye
        /// cameras were created (3000 m), so they drew far more of the world than the game intends.
        /// Setting near/far discards a custom projection, hence the matrix is assigned afterwards.
        /// </summary>
        private void SyncEyeClipPlanes()
        {
            var near = _mainCamera.nearClipPlane;
            var far = _mainCamera.farClipPlane;
            if (Mathf.Approximately(near, _eyeNear) && Mathf.Approximately(far, _eyeFar))
            {
                return;
            }
            Plugin.Logger.LogInfo($"Eye cameras' clip planes follow the game camera: near {near:F2}, far {far:F0} (was {_eyeNear:F2}/{_eyeFar:F0}).");
            _eyeNear = near;
            _eyeFar = far;
            ApplyEyeProjection(_leftEyeCamera, EVREye.Eye_Left);
            ApplyEyeProjection(_rightEyeCamera, EVREye.Eye_Right);
        }

        private void ApplyEyeProjection(Camera camera, EVREye eye)
        {
            camera.nearClipPlane = _eyeNear;
            camera.farClipPlane = _eyeFar;
            camera.projectionMatrix = _system.GetProjectionMatrix(eye, _eyeNear, _eyeFar).ToMatrix4x4();
        }

        private void SyncOneEyeCamera(Camera eye)
        {
            eye.clearFlags = _mainCamera.clearFlags;
            eye.backgroundColor = _mainCamera.backgroundColor;
            eye.cullingMask = (_gameMask | (LiquidVisibleInEyes ? _liquidLayerMask : 0)) & ~_benchEyeMaskRemove;
            eye.renderingPath = ForwardOnEyes ? RenderingPath.Forward : _mainCamera.renderingPath;
            eye.allowMSAA = true; // the main camera may have it off (Deferred cannot use it); the eyes are Forward
            eye.useOcclusionCulling = Plugin.EyeOcclusionCulling.Value || _benchOcclusion;

            // The game's water simulation (ZibraAI) hooks every rendering camera except those of type VR,
            // and builds native GPU resources for each one. See VrManager.Liquid.cs.
            var wantedType = LiquidVisibleInEyes ? CameraType.Game : CameraType.VR;
            if (eye.cameraType != wantedType)
            {
                eye.cameraType = wantedType;
                Plugin.Logger.LogInfo($"Eye camera '{eye.name}' type set to {wantedType}" +
                                      (wantedType == CameraType.VR ? " (the water simulation skips it)." : "."));
            }
            SyncEffectsEnabled(eye);
            if (_mirrorLayer >= 0)
            {
                eye.cullingMask |= 1 << _mirrorLayer; // see the mirror quad even though main doesn't
            }

            if (_mainCamera.TryGetComponent<Skybox>(out var mainSkybox) && mainSkybox.material != null)
            {
                if (!eye.TryGetComponent<Skybox>(out var eyeSkybox))
                {
                    eyeSkybox = eye.gameObject.AddComponent<Skybox>();
                }
                if (eyeSkybox.material != mainSkybox.material)
                {
                    eyeSkybox.material = mainSkybox.material;
                }
            }
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
                SyncEyeCameraSettings();

                // WaitGetPoses must run every frame we also Submit, no matter
                // what else is going on - it's how SteamVR's compositor tracks
                // that the app is alive and paces its frames. It used to sit
                // below the early returns that follow, which meant: (1) at
                // the main menu (no PlayerLook yet) it was never called, so
                // SteamVR never granted us scene focus - Submit returned
                // DoNotHaveFocus and the headset never showed the menu; and
                // (2) while the tablet (or any other MirarActivo focus mode)
                // was open it was skipped while Submit kept firing, giving
                // AlreadySubmitted errors and SteamVR's "waiting" screen.
                OpenVR.Compositor.WaitGetPoses(_renderPoses, EmptyPoseArray);

                UpdateVrInput();
                ApplyVSyncPolicy();
                ApplyShadowPolicy();
                UpdateFarSmallObjects();
                LogPerformance();
                UpdateDynamicResolution();

                var playerLook = PlayerLook.Instancia;
                if (playerLook == null)
                {
                    UpdatePointerVisuals();
                    return;
                }
                if (!TryGetHmdPose(out var pos, out var rot))
                {
                    return;
                }

                if (!_haveZeroPose)
                {
                    _zeroPos = pos;
                    _zeroRot = rot;
                    _haveZeroPose = true;
                    _yawAdjust = 0f;
                }

                // The game's own camera rotation for this frame (before our head offset) is
                // the base the head mapping is expressed against.
                _savedBaseRotation = _mainCamera.transform.localRotation;
                var deltaRot = MapRotation(rot);
                var deltaPos = MapPosition(pos);

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
                    LogThrottled("pose-reject", $"Rejecting bad VR pose delta (pos={deltaPos}, |q|={qNorm:F3}) - " +
                                 "leaving camera untouched this frame.");
                    return;
                }

                LogThrottled("pose", $"HMD pos={pos} rot={rot.eulerAngles} deltaPos={deltaPos} deltaRotEuler={deltaRot.eulerAngles}");

                // Rotation: the transform currently holds the game's own result
                // for this frame, on top of the clean base we restored in
                // Update (see the comment on _savedBaseRotation) - remember it
                // exactly, then add our head offset.
                _savedBaseRotation = _mainCamera.transform.localRotation;
                _mainCamera.transform.localRotation = _savedBaseRotation * deltaRot;
                _rotationApplied = true;

                // Position: the game never touches localPosition per-frame, so we
                // track our own cached base instead of accumulating.
                _mainCamera.transform.localPosition = _baseLocalPosition + deltaPos;

                RememberHeadYaw();
                PositionHands();
                UpdateControllerActions();
                UpdateInfoPanel();
                UpdateMonitorMirror();

                // After head tracking, so the click ray below uses the same
                // camera pose the game will use for the actual click.
                UpdatePointerVisuals();
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

                ApplyPointerPose();

                if ((_mirrorVisible || _infoVisible) && _mirrorTex != null)
                {
                    try
                    {
                        ResizeMirrorTextureIfNeeded();
                        ScreenCapture.CaptureScreenshotIntoRenderTexture(_mirrorTex);
                    }
                    catch (Exception ex)
                    {
                        LogThrottled("mirror-capture-error", $"Mirror screen capture error: {ex}");
                    }
                }

                LogThrottled("submitloop", $"[submitloop] active={_active} " +
                             $"leftCam={(_leftEyeCamera != null ? $"enabled={_leftEyeCamera.enabled},activeInHierarchy={_leftEyeCamera.gameObject.activeInHierarchy}" : "null")} " +
                             $"rightCam={(_rightEyeCamera != null ? $"enabled={_rightEyeCamera.enabled},activeInHierarchy={_rightEyeCamera.gameObject.activeInHierarchy}" : "null")} " +
                             $"timeScale={Time.timeScale}");

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
                        LogThrottled("submit-error", $"Compositor.Submit returned an error: left={leftErr}, right={rightErr}");
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
