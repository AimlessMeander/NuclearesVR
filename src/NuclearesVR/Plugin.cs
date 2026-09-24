using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearesVR
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.mjh.nuclearesvr";
        public const string Name = "NuclearesVR";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Logger;
        internal static ConfigEntry<float> PointerPitchDegrees;
        internal static ConfigEntry<bool> PointingEnabled;
        internal static ConfigEntry<bool> InstantActivation;
        internal static ConfigEntry<float> TurnSpeed;
        internal static ConfigEntry<float> DragPixelsPerMeter;
        internal static ConfigEntry<bool> WalkTowardsHead;
        internal static ConfigEntry<KeyCode> KeyA, KeyB, KeyX, KeyY;
        internal static ConfigEntry<KeyCode> KeyLeftGrip, KeyRightGrip, KeyLeftStick, KeyRightStick;

        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;
            PointerPitchDegrees = Config.Bind("Controllers", "PointerPitchDegrees", 0f,
                "Tilts the pointer laser up (+) or down (-) relative to the controller's pointing pose, in degrees.");
            PointingEnabled = Config.Bind("Controllers", "PointingEnabled", true,
                "Controllers point and click in the game. Turn off to only draw the lasers.");
            InstantActivation = Config.Bind("Controllers", "InstantActivation", true,
                "Clicks skip the hand animation, like holding Shift with the mouse.");
            TurnSpeed = Config.Bind("Controllers", "TurnSpeed", 6f,
                "How fast the right stick turns you (higher is faster). Uses the game's mouse look, so its mouse sensitivity setting also applies.");
            DragPixelsPerMeter = Config.Bind("Controllers", "DragPixelsPerMeter", 3000f,
                "Turning dials and moving sliders: how many 'mouse pixels' one metre of hand movement counts as (higher = a shorter hand movement turns a dial further).");
            WalkTowardsHead = Config.Bind("Controllers", "WalkTowardsHead", true,
                "Pushing the left stick moves you the way your head faces (the body turns to face where you look while you move). Off = the way the body faces.");
            const string keys = "Buttons: the keyboard key the game sees when the controller button is pressed. None = unused. " +
                                "Use the game's own key bindings if you changed them.";
            KeyA = Config.Bind("Buttons", "A", KeyCode.F, keys + " Default: flashlight.");
            KeyB = Config.Bind("Buttons", "B", KeyCode.Tab, keys + " Default: tablet.");
            KeyX = Config.Bind("Buttons", "X", KeyCode.None, keys);
            KeyY = Config.Bind("Buttons", "Y", KeyCode.Escape, keys + " Default: menu.");
            KeyLeftGrip = Config.Bind("Buttons", "LeftGrip", KeyCode.None, keys);
            KeyRightGrip = Config.Bind("Buttons", "RightGrip", KeyCode.None, keys);
            KeyLeftStick = Config.Bind("Buttons", "LeftStickClick", KeyCode.LeftShift, keys + " Default: run.");
            KeyRightStick = Config.Bind("Buttons", "RightStickClick", KeyCode.None, keys);
            Logger.LogInfo($"{Name} {Version} loading...");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();
            Vr.InputPatches.Apply(_harmony);

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
