using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SmoothNet
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ryuto.smoothnet";
        public const string Name = "SmoothNet";
        public const string Version = "1.3.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Cfg.Bind(Config);
            Updater.MarkBooted();

            var harmony = new Harmony(Guid);
            foreach (var t in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                // Patch each class separately so one broken patch (after a game update) doesn't take the rest down.
                try { harmony.CreateClassProcessor(t).Patch(); }
                catch (System.Exception e) { Log.LogError($"Patch {t.Name} failed (game update?): {e.Message}"); }
            }

            Gate.Init(); // also applies Physics.reuseCollisionCallbacks only while the mod is active
            Log.LogInfo($"Physics: fixedDeltaTime={Time.fixedDeltaTime} maxDeltaTime={Time.maximumDeltaTime} solverIter={Physics.defaultSolverIterations}");

            var go = new GameObject("SmoothNet.Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}
