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
                var pub = UpdateCore.PublicKey();
                UpdateCore.OnLaunch(dir, pub, s => log.LogInfo(s));

                // Same-launch update: fetch + verify + apply within a hard 4 s budget (the plugin's background download stays the fallback).
                if (AutoUpdateEnabled())
                {
                    var lg = log;
                    UpdateCore.TryOnlineUpdate(dir, pub, s => lg.LogInfo(s), OnlineBudgetMs,
                        (url, ms) => UpdateCore.Download(url, ms, 4 * 1024 * 1024), UpdateCore.IsAllowedUrl);
                }
                else log.LogInfo("AutoUpdate is off; skipping the online check");
            }
            catch (Exception e)
            {
                try { (log ?? Logger.CreateLogSource("SmoothNetUpdater")).LogWarning("updater failed: " + e.Message); } catch { }
            }
        }

        private const int OnlineBudgetMs = 4000;

        /// <summary>Reads General.AutoUpdate straight from the plugin's config file (default true when absent).</summary>
        private static bool AutoUpdateEnabled()
        {
            try
            {
                string f = Path.Combine(Paths.ConfigPath, "ryuto.smoothnet.cfg");
                if (!File.Exists(f)) return true;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(f), @"(?im)^\s*AutoUpdate\s*=\s*(true|false)\s*$");
                return !m.Success || !string.Equals(m.Groups[1].Value, "false", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
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
