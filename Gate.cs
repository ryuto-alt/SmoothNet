using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace SmoothNet
{
    /// <summary>
    /// The one switch every gameplay-affecting path consults. On = config Enabled AND this session's startup choice.
    /// Off means vanilla: no prediction/simulation, vanilla rates, no host compensation, no cart sleep fix, no GC patch,
    /// no region change, and the presence flag "e" is dropped so other players treat us as unmodded.
    /// (The auto-updater and the HUD do not depend on it.)
    /// </summary>
    internal static class Gate
    {
        private static bool session = true;
        public static bool Answered;
        private static bool physicsChangedByUs;

        public static bool On => Cfg.Enabled.Value && session;

        /// <summary>Show the startup prompt: asking is on, the mod is enabled in config, nothing answered yet.</summary>
        public static bool NeedsPrompt => Cfg.Enabled.Value && Cfg.AskOnStartup.Value && !Answered;

        public static void Init()
        {
            // Until the player answers, behave vanilla (nothing runs before the main menu anyway).
            session = !Cfg.AskOnStartup.Value;
            ApplyPhysics();
        }

        public static bool InLevel()
        {
            try { return SemiFunc.RunIsLevel() || SemiFunc.RunIsArena() || SemiFunc.RunIsShop() || SemiFunc.RunIsTutorial(); }
            catch { return false; }
        }

        /// <summary>Only in the menu / lobby, never inside a level.</summary>
        public static void Choose(bool on, bool dontAsk)
        {
            if (InLevel()) return;
            Answered = true;
            session = on;
            Cfg.StartupChoice.Value = on;
            if (dontAsk)
            {
                Cfg.AskOnStartup.Value = false;
                Cfg.Enabled.Value = on;
            }
            Plugin.Instance.Config.Save();
            ApplyPhysics();
            Plugin.Log.LogInfo($"Startup choice: {(on ? "ON" : "OFF")} for this session{(dontAsk ? " (won't ask again; Enabled=" + on + ")" : "")}");
        }

        /// <summary>Physics.reuseCollisionCallbacks is the only global setting we touch; restore it when the mod is off.</summary>
        private static void ApplyPhysics()
        {
            bool want = On && Cfg.CollisionGcFix.Value;
            if (want && !Physics.reuseCollisionCallbacks)
            {
                Physics.reuseCollisionCallbacks = true;
                physicsChangedByUs = true;
                Plugin.Log.LogInfo("Physics.reuseCollisionCallbacks -> true");
            }
            else if (!want && physicsChangedByUs)
            {
                Physics.reuseCollisionCallbacks = false;
                physicsChangedByUs = false;
                Plugin.Log.LogInfo("Physics.reuseCollisionCallbacks restored (mod off)");
            }
        }
    }

    /// <summary>Startup prompt: shown once per launch when the main menu first appears (IMGUI).</summary>
    internal static class StartupPrompt
    {
        private static bool sel = true;
        private static bool selInit;
        private static bool dontAsk;
        private static GUIStyle title, body, btn, btnSel, small;
        private static Texture2D bg;

        private static bool MenuReady()
        {
            try { return RunManager.instance && (SemiFunc.IsMainMenu() || SemiFunc.RunIsLobbyMenu()) && !Gate.InLevel(); }
            catch { return false; }
        }

        private static void T(out string t, out string b, out string on, out string off, out string chk, out string hint)
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean:
                    t = "SmoothNet 네트워크 개선 모드"; b = "렉을 줄이는 실험 기능이에요.\n이번 판에 켤까요?"; on = "켜기"; off = "끄기";
                    chk = "다음부터 묻지 않기"; hint = "마우스 클릭, 또는 1 = 켜기 / 2 = 끄기 / 3 = 묻지 않기 체크. (끄고 묻지 않기를 고르면 설정 파일 Enabled=true로 되돌릴 수 있어요)"; break;
                case SystemLanguage.Japanese:
                    t = "SmoothNet ネットワーク改善MOD"; b = "ラグを減らす実験的な機能です。\n今回のプレイで有効にしますか？"; on = "オンにする"; off = "オフにする";
                    chk = "次回から聞かない"; hint = "マウスでクリック、またはキー 1 = オン / 2 = オフ / 3 = 次回から聞かないを切替。(オフで聞かない場合は設定ファイルの Enabled=true で戻せます)"; break;
                default:
                    t = "SmoothNet network improvements"; b = "An experimental lag-reduction mod.\nTurn it on for this session?"; on = "Turn on"; off = "Turn off";
                    chk = "Don't ask again"; hint = "Click, or keys: 1 = on / 2 = off / 3 = toggle \"don't ask\". (Off + don't ask can be undone with Enabled=true in the config)"; break;
            }
        }

        public static void Draw()
        {
            if (!Gate.NeedsPrompt || !MenuReady()) return;
            var ev = Event.current;
            if (ev == null) return;
            if (!selInit) { sel = Cfg.StartupChoice.Value; selInit = true; }
            if (title == null)
            {
                bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.94f));
                bg.Apply();
                title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                body = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                small = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                title.normal.textColor = body.normal.textColor = small.normal.textColor = Color.white;
                btn = new GUIStyle(GUI.skin.button) { fontSize = 18 };
                btnSel = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
                btnSel.normal.textColor = btnSel.hover.textColor = new Color(0.5f, 1f, 0.4f);
            }
            T(out string t, out string b, out string on, out string off, out string chk, out string hint);

            if (ev.type == EventType.KeyDown)
            {
                switch (ev.keyCode)
                {
                    case KeyCode.Alpha1: case KeyCode.Keypad1: Gate.Choose(true, dontAsk); ev.Use(); return;
                    case KeyCode.Alpha2: case KeyCode.Keypad2: Gate.Choose(false, dontAsk); ev.Use(); return;
                    case KeyCode.Alpha3: case KeyCode.Keypad3: dontAsk = !dontAsk; ev.Use(); break;
                }
            }

            float w = Mathf.Min(560f, Screen.width - 40f), h = 300f;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.depth = -1000;
            GUI.DrawTexture(r, bg);
            GUI.Label(new Rect(r.x + 16, r.y + 14, w - 32, 34), t, title);
            GUI.Label(new Rect(r.x + 16, r.y + 56, w - 32, 70), b, body);
            float bw = (w - 64f) * 0.5f;
            if (GUI.Button(new Rect(r.x + 24, r.y + 136, bw, 48), on, sel ? btnSel : btn)) Gate.Choose(true, dontAsk);
            if (GUI.Button(new Rect(r.x + 40 + bw, r.y + 136, bw, 48), off, !sel ? btnSel : btn)) Gate.Choose(false, dontAsk);
            dontAsk = GUI.Toggle(new Rect(r.x + 24, r.y + 196, w - 48, 26), dontAsk, " " + chk);
            GUI.Label(new Rect(r.x + 16, r.y + 232, w - 32, 56), hint, small);
        }
    }

    /// <summary>
    /// AutoBestRegion: when we CREATE a lobby (SteamLobbyManager.HostLobby(_open:false) is called by NetworkConnect right
    /// after it set FixedRegion from the menu selection), host on the lowest-ping Photon region if the selected one is slower
    /// by more than 50 ms. Pings come from the region menu (MenuPageRegions) which pinged every region. Joining never changes it.
    /// </summary>
    internal static class RegionPick
    {
        private static readonly Dictionary<string, int> pings = new Dictionary<string, int>();

        public static void Snapshot()
        {
            try
            {
                var handler = PhotonNetwork.NetworkingClient?.RegionHandler;
                var list = handler?.EnabledRegions;
                if (list == null || list.Count == 0) return;
                pings.Clear();
                foreach (var r in list)
                    if (r != null && !string.IsNullOrEmpty(r.Code) && r.Ping > 0 && r.Ping < 9000) pings[r.Code.ToLowerInvariant()] = r.Ping;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Region snapshot failed: " + e.Message); }
        }

        public static void Apply()
        {
            if (!Gate.On || !Cfg.AutoBestRegion.Value) return;
            if (Cfg.ForceRegion.Value != "Off") return; // explicit choice wins
            Snapshot(); // live data if the handler still has it, else keep the menu snapshot
            if (pings.Count == 0) { Plugin.Log.LogInfo("AutoBestRegion: no region pings available"); return; }

            string best = null; int bestPing = int.MaxValue;
            foreach (var kv in pings) if (kv.Value < bestPing) { best = kv.Key; bestPing = kv.Value; }
            var settings = PhotonNetwork.PhotonServerSettings.AppSettings;
            string selected = (settings.FixedRegion ?? "").ToLowerInvariant();

            bool change;
            int selPing = -1;
            if (selected == "") change = true; // auto: make sure it is really the best one
            else if (pings.TryGetValue(selected, out selPing)) change = selPing > bestPing + 50;
            else return; // unknown region: leave it alone
            if (!change || selected == best) { Plugin.Log.LogInfo($"AutoBestRegion: hosting on '{(selected == "" ? best : selected)}' (best '{best}' {bestPing}ms)"); return; }

            settings.FixedRegion = best;
            Plugin.Log.LogInfo($"AutoBestRegion: '{(selected == "" ? "auto" : selected)}'{(selPing > 0 ? " " + selPing + "ms" : "")} -> '{best}' {bestPing}ms");
            string msg;
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean: msg = $"핑이 더 낮은 {best} 서버로 호스트했어요. ({bestPing}ms)"; break;
                case SystemLanguage.Japanese: msg = $"Pingが低い {best} サーバーでホストしました。({bestPing}ms)"; break;
                default: msg = $"Hosting on the lower-ping '{best}' server ({bestPing}ms)."; break;
            }
            Updater.ShowToast(msg);
        }
    }

    [HarmonyPatch(typeof(SteamLobbyManager), "HostLobby")]
    internal static class HostLobbyRegionPatch
    {
        private static void Prefix(bool _open)
        {
            if (_open) return; // that one is the in-game "open lobby" call, not the room creation
            try { RegionPick.Apply(); }
            catch (Exception e) { Plugin.Log.LogError("AutoBestRegion failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(MenuPageRegions), nameof(MenuPageRegions.PickRegion))]
    internal static class RegionMenuSnapshotPatch
    {
        // Runs before ForceRegionPatch/vanilla: the region page just pinged every region.
        [HarmonyPriority(Priority.First)]
        private static void Prefix() => RegionPick.Snapshot();
    }
}
