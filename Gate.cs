using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Events;

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
        public static void Choose(bool on)
        {
            if (InLevel()) return;
            Answered = true;
            session = on;
            Cfg.StartupChoice.Value = on;
            Plugin.Instance.Config.Save();
            ApplyPhysics();
            Plugin.Log.LogInfo($"Startup choice: {(on ? "ON" : "OFF")} for this session");
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

    /// <summary>
    /// Startup prompt: shown once per launch when the main menu first appears, using the game's own two-option popup
    /// (MenuManager.PagePopUpTwoOptions) so it gets the game's cursor and mouse/keyboard/controller input.
    /// Closing it without a choice (back/escape = the game's option 2) means OFF for the session.
    /// </summary>
    internal static class StartupPrompt
    {
        private static bool shown;
        private static float shownAt;
        private static GameObject holder;

        private static bool MenuReady()
        {
            try
            {
                if (!RunManager.instance || Gate.InLevel() || !(SemiFunc.IsMainMenu() || SemiFunc.RunIsLobbyMenu())) return false;
                var mm = MenuManager.instance;
                if (!mm || !mm.currentMenuPage) return false;
                var page = mm.currentMenuPage;
                if (page.currentPageState != MenuPage.PageState.Active) return false;
                if (page.menuPageIndex != MenuPageIndex.Main && page.menuPageIndex != MenuPageIndex.Lobby) return false;
                return !MenuPageTwoOptions.instance; // no other popup open
            }
            catch { return false; }
        }

        private static void T(out string t, out string b, out string on, out string off)
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean:
                    t = "SmoothNet 네트워크 개선 모드";
                    b = "렉을 줄이는 실험 기능이에요. 이번 판에 켤까요?\n(매번 묻지 않으려면 설정에서 AskOnStartup을 끄세요)";
                    on = "켜기"; off = "끄기"; break;
                case SystemLanguage.Japanese:
                    t = "SmoothNet ネットワーク改善MOD";
                    b = "ラグを減らす実験的な機能です。今回のプレイで有効にしますか？\n(毎回聞かれたくない場合は設定の AskOnStartup をオフにしてください)";
                    on = "オンにする"; off = "オフにする"; break;
                default:
                    t = "SmoothNet network improvements";
                    b = "An experimental lag-reduction mod. Turn it on for this session?\n(To stop being asked, set AskOnStartup to false in the config)";
                    on = "Turn on"; off = "Turn off"; break;
            }
        }

        /// <summary>Called every frame from Runner.Update.</summary>
        public static void Tick()
        {
            if (!Gate.NeedsPrompt) return;
            if (shown)
            {
                // The popup vanished without either option firing (e.g. scene change): safe default is OFF.
                if (Time.unscaledTime - shownAt > 0.5f && !MenuPageTwoOptions.instance)
                {
                    Plugin.Log.LogInfo("Startup popup closed without a choice -> OFF for this session");
                    Gate.Choose(false);
                    if (!Gate.Answered) shown = false; // not in a menu; try again later
                }
                return;
            }
            if (!MenuReady()) return;

            if (!holder)
            {
                holder = new GameObject("SmoothNet.StartupPopup");
                holder.hideFlags = HideFlags.HideAndDontSave;
            }
            var pop = holder.GetComponent<MenuButtonPopUp>() ?? holder.AddComponent<MenuButtonPopUp>();
            pop.option1Event = new UnityEvent();
            pop.option2Event = new UnityEvent();
            pop.option1Event.AddListener(() => Gate.Choose(true));
            pop.option2Event.AddListener(() => Gate.Choose(false));
            T(out string t, out string b, out string on, out string off);
            MenuManager.instance.PagePopUpTwoOptions(pop, t, null, new Color(1f, 0.55f, 0f), b, null, on, null, off, null, false);
            shown = true;
            shownAt = Time.unscaledTime;
            Plugin.Log.LogInfo("Startup popup shown");
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
