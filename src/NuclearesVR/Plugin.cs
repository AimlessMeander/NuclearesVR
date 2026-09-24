using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
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

        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;
            PointerPitchDegrees = Config.Bind("Controllers", "PointerPitchDegrees", 0f,
                "Tilts the pointer laser up (+) or down (-) relative to the controller's pointing pose, in degrees.");
            Logger.LogInfo($"{Name} {Version} loading...");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

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
