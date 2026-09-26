using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearesVR
{
    public enum MonitorViewMode { Headset, Black, Game }

    public enum VrStartMode
    {
        /// <summary>VR only if SteamVR is already running; otherwise play normally in 2D.</summary>
        Auto,
        /// <summary>Always start VR when a game loads (this launches SteamVR if it is not running).</summary>
        Always,
        /// <summary>Never start VR.</summary>
        Never
    }

    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.mjh.nuclearesvr";
        public const string Name = "NuclearesVR";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Logger;
        internal static ConfigEntry<float> PointerPitchDegrees;
        internal static ConfigEntry<float> RecenterHoldSeconds;
        internal static ConfigEntry<float> MenuScreenHeight, MenuScreenDown, MenuScreenDistance;
        internal static ConfigEntry<bool> PointingEnabled;
        internal static ConfigEntry<bool> InstantActivation;
        internal static ConfigEntry<float> TurnSpeed;
        internal static ConfigEntry<float> DragPixelsPerMeter;
        internal static ConfigEntry<bool> WalkTowardsHead;
        internal static ConfigEntry<VrStartMode> StartMode;
        internal static ConfigEntry<bool> GripIsRightClick;
        internal static ConfigEntry<bool> LiquidInVr;
        internal static ConfigEntry<bool> DisableVsyncInVr;
        internal static ConfigEntry<MonitorViewMode> MonitorView;
        internal static ConfigEntry<bool> SecurityCameras;
        internal static ConfigEntry<bool> EyeOcclusionCulling;
        internal static ConfigEntry<float> EyeDefaultLayerDistance;
        internal static ConfigEntry<float> VrShadowDistance;
        internal static ConfigEntry<float> FarSmallObjectDistance;
        internal static ConfigEntry<float> FarSmallObjectSize;
        internal static ConfigEntry<bool> ShareWaterReflection;
        internal static ConfigEntry<int> WaterReflectionEveryNthFrame;
        internal static ConfigEntry<int> EyeMsaa;
        internal static ConfigEntry<float> RenderScale;
        internal static ConfigEntry<KeyCode> KeyA, KeyB, KeyX, KeyXAlso, KeyY;
        internal static ConfigEntry<KeyCode> KeyLeftGrip, KeyRightGrip, KeyLeftStick, KeyRightStick, KeyInventory;

        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;
            PointerPitchDegrees = Config.Bind("Controllers", "PointerPitchDegrees", 0f,
                "Tilts the pointer laser up (+) or down (-) relative to the controller's pointing pose, in degrees.");
            RecenterHoldSeconds = Config.Bind("Controllers", "RecenterHoldSeconds", 2f,
                new ConfigDescription("Hold both triggers for this many seconds to recentre the view (do it while sitting comfortably, looking straight ahead). 0 = off. The End key on the keyboard also recentres.",
                    new AcceptableValueRange<float>(0f, 10f)));
            PointingEnabled = Config.Bind("Controllers", "PointingEnabled", true,
                "Controllers point and click in the game. Turn off to only draw the lasers.");
            InstantActivation = Config.Bind("Controllers", "InstantActivation", true,
                "Clicks skip the hand animation, like holding Shift with the mouse.");
            TurnSpeed = Config.Bind("Controllers", "TurnSpeed", 4f,
                "How fast the right stick turns you (higher is faster). Uses the game's mouse look, so its mouse sensitivity setting also applies.");
            DragPixelsPerMeter = Config.Bind("Controllers", "DragPixelsPerMeter", 4000f,
                "Turning dials and moving sliders: how many 'mouse pixels' one metre of hand movement counts as (higher = a shorter hand movement turns a dial further).");
            WalkTowardsHead = Config.Bind("Controllers", "WalkTowardsHead", true,
                "Pushing the left stick moves you the way your head faces (the body turns to face where you look while you move). Off = the way the body faces.");
            StartMode = Config.Bind("General", "VrMode", VrStartMode.Auto,
                "Auto = start VR only if SteamVR is already running, so playing on the monitor without SteamVR never launches it. " +
                "Always = always start VR when a game loads (launches SteamVR if needed). Never = never start VR.");
            GripIsRightClick = Config.Bind("Controllers", "GripIsRightClick", true,
                "Holding a grip is the right mouse button: hold it on a gauge or component for its detail box, open switch guards, and with the stick forward/back to zoom.");
            MenuScreenHeight = Config.Bind("Controllers", "MenuScreenHeight", 0.8f,
                new ConfigDescription("Height in metres of the floating screen used for the menus, dialogs and inventory. Smaller if its corners are cut off.",
                    new AcceptableValueRange<float>(0.3f, 2.0f)));
            MenuScreenDown = Config.Bind("Controllers", "MenuScreenDown", 0.12f,
                new ConfigDescription("How far below eye level, in metres, the centre of that floating screen sits. Higher = lower screen.",
                    new AcceptableValueRange<float>(-0.5f, 0.8f)));
            MenuScreenDistance = Config.Bind("Controllers", "MenuScreenDistance", 1.5f,
                new ConfigDescription("How far in front of you, in metres, that floating screen is. Its picture stays the same apparent size if you change the height by the same proportion.",
                    new AcceptableValueRange<float>(0.5f, 4.0f)));
            LiquidInVr = Config.Bind("Graphics", "LiquidSimulationInVr", true,
                "The game's real-time water simulation (ZibraAI) is active in the control room, reactor and service areas. It shares one set of GPU textures between all " +
                "cameras, and the game window and the two headset cameras have different sizes, which is suspected of causing random graphics-driver crashes. " +
                "true = the water is drawn in the headset, and hidden from the game's own main camera (the monitor mirror) so that only the two same-size headset cameras use it. " +
                "false = the headset cameras skip the simulation entirely (safest, but the water, e.g. in the core pool, is not visible in the headset).");
            DisableVsyncInVr = Config.Bind("Graphics", "DisableVsyncInVr", true,
                "Turns the game's VSync off while VR runs (restored afterwards). The headset sets the frame rate; with VSync on, the game also waits for the monitor's refresh, " +
                "which can leave the frame rate stuck at a fraction of the headset's (for example 30).");
            MonitorView = Config.Bind("Graphics", "MonitorView", MonitorViewMode.Headset,
                "What the game window on your monitor shows while VR runs. " +
                "Headset = the headset's view (left eye), which costs almost nothing. " +
                "Black = a black window, the cheapest. " +
                "Game = the game's own normal camera, which is like the headset view but drawn a second time (noticeably slower).");
            EyeOcclusionCulling = Config.Bind("Graphics", "EyeOcclusionCulling", true,
                "Occlusion culling for the headset cameras: skips drawing objects hidden behind walls. Measured to cut the frame cost of a busy view roughly in half. " +
                "It was switched off originally because it may once have made walls disappear; if you see walls or scenery vanish, turn this off and tell me.");
            EyeDefaultLayerDistance = Config.Bind("Graphics", "EyeDefaultLayerDistance", 150f,
                new ConfigDescription("Most of the game's objects are on one layer ('Default'); thousands of them in the distance made the headset views very expensive. " +
                                      "This is how far away (metres) objects on that layer are still drawn in the headset. Terrain, sky and other layers keep the game's own draw distance. " +
                                      "0 = no limit (slower, but nothing distant disappears).",
                    new AcceptableValueRange<float>(0f, 1000f)));
            VrShadowDistance = Config.Bind("Graphics", "ShadowDistance", 60f,
                new ConfigDescription("How far away (metres) shadows are drawn while VR runs; the game's own setting is 200. Every eye re-draws all the geometry within this distance into its shadow map, " +
                                      "and the game's merged plant-wide meshes make that very expensive. Shadows near you are unchanged. 0 = keep the game's own setting.",
                    new AcceptableValueRange<float>(0f, 300f)));
            FarSmallObjectDistance = Config.Bind("Graphics", "FarSmallObjectDistance", 100f,
                new ConfigDescription("Small objects (see FarSmallObjectSize) whose nearest point is farther away than this many metres are not drawn in the headset. " +
                                      "The plant has thousands of tiny objects (bolts, fittings, lamps) that are each a separate draw call, drawn once per eye; measured at the worst view, " +
                                      "the 3,000 that were over 100 m away cost about half of the frame time. Big objects such as walls are not affected. 0 = draw everything.",
                    new AcceptableValueRange<float>(0f, 400f)));
            FarSmallObjectSize = Config.Bind("Graphics", "FarSmallObjectSize", 12f,
                new ConfigDescription("What counts as a small object for FarSmallObjectDistance: an object whose bounding box is smaller than this many metres across (corner to corner).",
                    new AcceptableValueRange<float>(1f, 60f)));
            SecurityCameras = Config.Bind("Graphics", "SecurityCameras", false,
                "Whether the game draws the CCTV system's cameras while VR runs. false (default) = the CCTV screens stay black in VR. " +
                "Switching the CCTV on freezes or crashes the game in VR (the graphics driver fails when those extra cameras draw beside the headset views); no reliable fix has been found. " +
                "true = let the game draw them anyway (likely to crash).");
            ShareWaterReflection = Config.Bind("Graphics", "ShareWaterReflection", true,
                "The game's water planes render an extra mirrored copy of the scene for every camera that sees them; with two eyes that made looking at the reactor pool very expensive. " +
                "true = the right eye reuses the left eye's reflection, and the reflection is refreshed only every few frames.");
            WaterReflectionEveryNthFrame = Config.Bind("Graphics", "WaterReflectionEveryNthFrame", 2,
                new ConfigDescription("With ShareWaterReflection on: refresh the water reflection every this many frames (1 = every frame, 2 = every other frame...).", new AcceptableValueRange<int>(1, 8)));
            EyeMsaa = Config.Bind("Graphics", "Msaa", 4,
                new ConfigDescription("Anti-aliasing (MSAA samples) for the headset view: 1 = off, 2, 4 or 8. Smooths jagged edges; costs GPU time. Applied when a game loads.",
                    new AcceptableValueList<int>(1, 2, 4, 8)));
            RenderScale = Config.Bind("Graphics", "RenderScale", 1.0f,
                new ConfigDescription("Renders the headset view at this multiple of the headset's recommended resolution (1.0 = recommended, 1.3 = sharper and less aliased, more GPU load). Applied when a game loads.",
                    new AcceptableValueRange<float>(0.5f, 2.0f)));
            const string keys = "Buttons: the keyboard key the game sees when the controller button is pressed. None = unused. " +
                                "Use the game's own key bindings if you changed them.";
            KeyA = Config.Bind("Buttons", "A", KeyCode.F, keys + " Default: flashlight.");
            KeyB = Config.Bind("Buttons", "B", KeyCode.Tab, keys + " Default: tablet.");
            KeyX = Config.Bind("Buttons", "X", KeyCode.Return, keys + " Default: Enter (next step in the tutorial).");
            KeyXAlso = Config.Bind("Buttons", "XAlso", KeyCode.Space, keys + " A second key pressed together with X. Default: Space (jump).");
            KeyY = Config.Bind("Buttons", "Y", KeyCode.Escape, keys + " Default: menu.");
            KeyLeftGrip = Config.Bind("Buttons", "LeftGrip", KeyCode.None, keys);
            KeyRightGrip = Config.Bind("Buttons", "RightGrip", KeyCode.None, keys);
            KeyLeftStick = Config.Bind("Buttons", "LeftStickClick", KeyCode.LeftShift, keys + " Default: run.");
            KeyRightStick = Config.Bind("Buttons", "RightStickClick", KeyCode.G, keys + " Default: Geiger counter (pressed when you let go, so it does not fire during the two-stick inventory press).");
            KeyInventory = Config.Bind("Buttons", "BothSticksClick", KeyCode.I, keys + " Pressing both stick clicks together. Default: inventory.");
            Logger.LogInfo($"{Name} {Version} loading...");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();
            Vr.InputPatches.Apply(_harmony);
            Vr.VrWaterPatches.Apply(_harmony);
            Vr.VrSecurityPatches.Apply(_harmony);

            // Awake() runs in Nucleares' very first bootstrap scene, before Steam
            // even initializes - and that scene gets torn down and replaced
            // almost immediately as part of normal startup. Anything persistent
            // we create here (even attached to BepInEx's own manager object) is
            // liable to get swept up in that transition. sceneLoaded only fires
            // once a scene transition has fully settled, so we wait for that
            // before creating anything.
            SceneManager.sceneLoaded += OnFirstSceneLoaded;

            Logger.LogInfo($"{Name} {Version} loaded, waiting for first scene load before starting VR...");
        }

        private void OnFirstSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= OnFirstSceneLoaded;
            Logger.LogInfo($"First scene loaded ('{scene.name}'), starting VrManager now.");
            Vr.VrManager.Bootstrap();
        }
    }
}
