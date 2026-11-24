using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace SmoothNet.Updating
{
    /// <summary>
    /// BepInEx 5 preloader patcher. Runs before any plugin is loaded, so SmoothNet.dll is not locked yet and a staged
    /// update can replace it. It never blocks the game: every failure is logged and swallowed.
    /// </summary>
    public static class SmoothNetUpdaterPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            ManualLogSource log = null;
            try
            {
                log = Logger.CreateLogSource("SmoothNetUpdater");
                string dir = FindPluginDir();
                if (dir == null) { log.LogInfo("SmoothNet.dll not found under plugins; nothing to do"); return; }
                UpdateCore.OnLaunch(dir, UpdateCore.PublicKey(), s => log.LogInfo(s));
            }
            catch (Exception e)
            {
                try { (log ?? Logger.CreateLogSource("SmoothNetUpdater")).LogWarning("updater failed: " + e.Message); } catch { }
            }
        }

        private static string FindPluginDir()
        {
            string root = Paths.PluginPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            var found = Directory.GetFiles(root, UpdateCore.DllName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).ToList();
            if (found.Count == 0) return null;
            return found.FirstOrDefault(p => p.IndexOf("ryuto-SmoothNet", StringComparison.OrdinalIgnoreCase) >= 0) ?? found[0];
        }
    }
}
