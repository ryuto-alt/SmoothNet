using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Networking;
using SmoothNet.Updating;

namespace SmoothNet
{
    /// <summary>
    /// Background auto-update: downloads + verifies the newest signed release into BepInEx\plugins\...\.update\.
    /// The preloader patcher (SmoothNetUpdater.dll) swaps it in on the next game launch.
    /// </summary>
    internal static class Updater
    {
        private const float StartDelay = 8f;
        private const int MaxBytes = 4 * 1024 * 1024;
        private const float ToastSeconds = 12f;

        /// <summary>Version staged and waiting for a restart (null if none).</summary>
        public static string StagedVersion;

        private static string toast;
        private static float toastShownAt = -1f;
        private static GUIStyle toastStyle;
        private static Texture2D toastBg;

        private static string PluginDir
        {
            get
            {
                try { return Path.GetDirectoryName(Plugin.Instance.Info.Location); }
                catch { return null; }
            }
        }

        /// <summary>Rollback bookkeeping: tells the patcher this version loaded fine.</summary>
        public static void MarkBooted()
        {
            try
            {
                string dir = PluginDir;
                if (dir != null) UpdateCore.WriteBootMarker(dir, Plugin.Version);
            }
            catch (Exception e) { Plugin.Log.LogWarning("boot marker failed: " + e.Message); }
        }

        public static IEnumerator Routine()
        {
            if (!Cfg.AutoUpdate.Value) yield break;
            yield return new WaitForSecondsRealtime(StartDelay);

            string dir = PluginDir;
            if (dir == null) yield break;
            try { StagedVersion = UpdateCore.PendingVersion(dir); } catch { }

            byte[] jsonBytes = null;
            yield return Get(UpdateCore.LatestUrl, b => jsonBytes = b);
            if (jsonBytes == null) yield break;

            LatestInfo info; string err;
            if (!UpdateCore.TryParseLatest(System.Text.Encoding.UTF8.GetString(jsonBytes), out info, out err))
            { Plugin.Log.LogWarning("Update check: " + err); yield break; }

            yield return PluginRoutine(dir, info);
            yield return PatcherRoutine(dir, info);
        }

        private static IEnumerator PluginRoutine(string dir, LatestInfo info)
        {
            string err;
            int? cmp = UpdateCore.CompareVersions(info.Version, Plugin.Version);
            if (cmp != 1) { Plugin.Log.LogInfo($"Update check: up to date ({Plugin.Version})"); yield break; }
            if (info.Version == StagedVersion) { QueueToast(info.Version); yield break; }
            if (UpdateCore.IsRejected(dir, info.Version)) { Plugin.Log.LogInfo($"Update {info.Version} was rolled back before; skipping"); yield break; }

            byte[] dll = null, sig = null;
            yield return Get(info.Dll, b => dll = b);
            if (dll == null) yield break;
            yield return Get(info.Sig, b => sig = b);
            if (sig == null) yield break;

            if (!UpdateCore.VerifyUpdate(dll, sig, info.Version, info.Sha256, Plugin.Version, UpdateCore.PublicKey(), out err))
            { Plugin.Log.LogWarning("Update " + info.Version + " rejected: " + err); yield break; }

            try
            {
                UpdateCore.Stage(dir, dll, sig, info);
                StagedVersion = info.Version;
                Plugin.Log.LogInfo($"Update {info.Version} downloaded and verified; it will be applied on the next game launch");
                QueueToast(info.Version);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Staging update failed: " + e.Message); }
        }

        // ---------- patcher self-update ----------

        private static string FindPatcherFile()
        {
            try
            {
                string root = Paths.PatcherPluginPath;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                var all = Directory.GetFiles(root, UpdateCore.PatcherDllName, SearchOption.AllDirectories);
                return all.FirstOrDefault(f => f.IndexOf("ryuto-SmoothNet", StringComparison.OrdinalIgnoreCase) >= 0) ?? all.FirstOrDefault();
            }
            catch { return null; }
        }

        private static IEnumerator PatcherRoutine(string dir, LatestInfo info)
        {
            if (info.PatcherVersion == null) yield break;
            string target = FindPatcherFile();
            if (target == null) yield break; // no patcher installed: nothing to upgrade
            string curVer = null;
            try { var an = System.Reflection.AssemblyName.GetAssemblyName(target); curVer = an.Version.Major + "." + an.Version.Minor + "." + an.Version.Build; }
            catch { yield break; }
            if (UpdateCore.CompareVersions(info.PatcherVersion, curVer) != 1) yield break;
            string sv, ss, st;
            if (UpdateCore.TryGetStagedPatcher(dir, out sv, out ss, out st) && sv == info.PatcherVersion) yield break;

            byte[] dll = null, sig = null;
            yield return Get(info.PatcherDll, b => dll = b);
            if (dll == null) yield break;
            yield return Get(info.PatcherSig, b => sig = b);
            if (sig == null) yield break;
            string err;
            if (!UpdateCore.VerifyPatcher(dll, sig, info.PatcherVersion, info.PatcherSha256, curVer, UpdateCore.PublicKey(), out err))
            { Plugin.Log.LogWarning("Patcher " + info.PatcherVersion + " rejected: " + err); yield break; }
            try
            {
                UpdateCore.StagePatcher(dir, dll, sig, info, target);
                Plugin.Log.LogInfo($"Patcher {info.PatcherVersion} downloaded and verified; it will be installed when the game exits");
                HookQuit();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Staging patcher failed: " + e.Message); }
        }

        private static bool quitHooked, helperSpawned;

        private static void HookQuit()
        {
            if (quitHooked) return;
            quitHooked = true;
            Application.quitting += () =>
            {
                try { SpawnHelper(false); } catch { }
            };
        }

        private static void SpawnHelper(bool relaunch)
        {
            if (helperSpawned) return;
            string dir = PluginDir;
            if (dir == null) return;
            if (UpdateCore.SpawnHelper(dir, System.Diagnostics.Process.GetCurrentProcess().Id, relaunch, s => Plugin.Log.LogInfo(s)))
                helperSpawned = true;
        }

        // ---------- restart popup (fallback when the patcher could not apply the update during startup) ----------

        private static bool restartAsked;

        /// <summary>Called every frame from Runner.Update.</summary>
        public static void Tick()
        {
            if (restartAsked || StagedVersion == null || Gate.NeedsPrompt) return;
            if (!NativePopup.CanShow(false)) return;
            restartAsked = true;
            string v = StagedVersion, t, b, on, off;
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean:
                    t = "SmoothNet 업데이트"; b = $"SmoothNet {v} 업데이트를 받았어요. 지금 다시 시작해서 적용할까요?"; on = "다시 시작"; off = "나중에"; break;
                case SystemLanguage.Japanese:
                    t = "SmoothNet アップデート"; b = $"SmoothNet {v} のアップデートを受け取りました。今すぐ再起動して適用しますか？"; on = "再起動する"; off = "あとで"; break;
                default:
                    t = "SmoothNet update"; b = $"SmoothNet {v} has been downloaded. Restart the game now to apply it?"; on = "Restart"; off = "Later"; break;
            }
            NativePopup.Show(t, b, on, off, Restart, () => Plugin.Log.LogInfo("Restart popup: later"));
            Plugin.Log.LogInfo("Restart popup shown for " + v);
        }

        private static void Restart()
        {
            Plugin.Log.LogInfo("Restart popup: restarting the game to apply the update");
            SpawnHelper(true);
            if (helperSpawned) Application.Quit();
            else Plugin.Log.LogWarning("Restart helper could not start; please restart manually");
        }

        private static IEnumerator Get(string url, Action<byte[]> done)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 15;
                req.SetRequestHeader("User-Agent", "SmoothNet-Updater/" + Plugin.Version);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogInfo($"Update check: request failed ({req.error})");
                    done(null);
                    yield break;
                }
                var data = req.downloadHandler.data;
                if (data == null || data.Length == 0 || data.Length > MaxBytes) { done(null); yield break; }
                done(data);
            }
        }

        // ---------- toast ----------

        private static void QueueToast(string v)
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean: toast = $"SmoothNet {v} 업데이트를 받았어요. 게임을 다시 시작하면 적용돼요."; break;
                case SystemLanguage.Japanese: toast = $"SmoothNet {v} のアップデートを受け取りました。ゲームを再起動すると適用されます。"; break;
                default: toast = $"SmoothNet {v} update downloaded. Restart the game to apply it."; break;
            }
            toastShownAt = -1f;
        }

        /// <summary>Queue an arbitrary one-off message on the same toast (shown in menus/lobby, ~12 s).</summary>
        public static void ShowToast(string text)
        {
            toast = toast == null ? text : toast + "\n" + text;
            toastShownAt = -1f;
        }

        private static bool InLevel()
        {
            try { return SemiFunc.RunIsLevel() || SemiFunc.RunIsArena(); }
            catch { return false; }
        }

        /// <summary>Called from Runner.OnGUI. Shown for ~12 s once the player is in a menu / lobby (never inside a level).</summary>
        public static void DrawToast()
        {
            if (toast == null || Event.current == null || Event.current.type != EventType.Repaint) return;
            if (InLevel()) return;
            if (toastShownAt < 0f) toastShownAt = Time.unscaledTime;
            if (Time.unscaledTime - toastShownAt > ToastSeconds) { toast = null; return; }

            if (toastStyle == null)
            {
                toastStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                toastStyle.normal.textColor = Color.white;
                toastBg = new Texture2D(1, 1);
                toastBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
                toastBg.Apply();
            }
            float w = Mathf.Min(Screen.width - 40f, 640f);
            var content = new GUIContent(toast);
            float h = toastStyle.CalcHeight(content, w - 24f) + 20f;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height - h - 60f, w, h);
            GUI.DrawTexture(rect, toastBg);
            GUI.Label(new Rect(rect.x + 12f, rect.y + 10f, w - 24f, h - 20f), content, toastStyle);
        }
    }
}
