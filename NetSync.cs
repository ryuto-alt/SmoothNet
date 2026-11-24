using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace SmoothNet
{
    /// <summary>
    /// Mod presence handshake, host-driven rate sync, latency estimates.
    /// Uses Photon custom properties only (vanilla ignores unknown keys), so the
    /// wire format of the game itself is never changed.
    /// </summary>
    internal static class NetSync
    {
        public const string PlayerKey = "SmoothNet";
        public const string RoomRateKey = "SmoothNet.sr";
        public const string RoomFpsKey = "SmoothNet.fps";
        public const string RttKey = "SmoothNet.rtt";

        public const int VanillaSerializationRate = 25;
        public const int VanillaSendRate = 25;
        public static bool AllModded;
        public static int HostFps = -1;

        private static float nextTick;
        private static float nextFpsPublish;
        private static float fpsAccum;
        private static int fpsFrames;
        public static float LocalFps;

        // --- RTT publishing + adaptive rates ---
        // Host: HighRateSync only while every player's RTT is low and stable; falls back to vanilla for >= 30 s when someone's RTT climbs.
        // Everyone: SendRate falls back to vanilla for 30 s when our own RTT climbs over its baseline or our outgoing queue grows.
        private const float HoldSeconds = 30f;
        private const float BadSustainSeconds = 1f;      // host: drop after ~2 ticks of trouble
        private const float SendBadSustainSeconds = 2f;  // own SendRate fallback
        private const float RaiseStableSeconds = 10f;
        private const float RaiseMaxRttMs = 100f;
        private const float DropRttMs = 120f;
        private const float DropRiseMs = 40f;
        private const float SendDropRiseMs = 60f;
        private const float MinFpsForAdapt = 30f;        // Photon RTT is inflated by slow frames; don't judge the network then
        private const int QueueLimit = 40;

        internal class RttTracker
        {
            public float Baseline = -1f;
            public int Samples;
            public float BadSince = -1f;

            public void Add(float rtt, float now, float hardMs, float riseMs, bool ignore = false)
            {
                if (ignore) { BadSince = -1f; return; } // slow frames inflate Photon's RTT: neither learn from nor judge it
                Samples++;
                bool risen = Baseline >= 0f && rtt - Baseline > riseMs;
                if (Baseline < 0f || rtt < Baseline) Baseline = rtt;
                else if (!risen) Baseline += (rtt - Baseline) * 0.005f; // slow upward drift, frozen while in trouble
                bool bad = Samples >= 20 && (rtt > hardMs || risen);    // ~10 s warm-up
                if (bad) { if (BadSince < 0f) BadSince = now; } else BadSince = -1f;
            }

            public bool BadFor(float now, float seconds) => BadSince >= 0f && now - BadSince >= seconds;
        }

        private static readonly Dictionary<int, RttTracker> trackers = new Dictionary<int, RttTracker>();
        private static readonly RttTracker myTracker = new RttTracker();
        private static float nextRttPublish;
        private static int lastPublishedRtt = -1;

        public static bool HighRateActive;
        private static float highRateHoldUntil;
        private static float highRateStableSince = -1f;
        public static float HighRateHoldRemaining => Mathf.Max(0f, highRateHoldUntil - Time.unscaledTime);

        public static float SendFallbackUntil;
        private static bool sendFallbackLogged;
        private static int queueBadCount;
        public static bool SendFallbackActive => Time.unscaledTime < SendFallbackUntil;

        // traffic stats (local peer)
        public static float OutKBps, InKBps, OutPktPs;
        public static int QueueNow, QueueMaxShown;
        private static int queueMaxWindow;
        private static float lastTrafficSample;
        private static long prevOutBytes, prevInBytes, prevOutPackets;

        public static void ResetAdaptive()
        {
            trackers.Clear();
            myTracker.Baseline = -1f; myTracker.Samples = 0; myTracker.BadSince = -1f;
            HighRateActive = false;
            highRateHoldUntil = 0f;
            highRateStableSince = -1f;
            SendFallbackUntil = 0f;
            sendFallbackLogged = false;
            queueBadCount = 0;
            lastPublishedRtt = -1;
            lastTrafficSample = 0f;
        }

        /// <summary>RTT (ms) another player published via SmoothNet, or -1.</summary>
        public static int ReportedRtt(Player p)
        {
            if (p?.CustomProperties != null && p.CustomProperties.TryGetValue(RttKey, out object v) && v is int i && i > 0) return i;
            return -1;
        }

        private static int RttOf(Player p)
        {
            if (p.IsLocal) return Mathf.Max(0, PhotonNetwork.GetPing());
            int r = ReportedRtt(p);
            if (r > 0) return r;
            if (GameDirector.instance != null)
                foreach (var a in GameDirector.instance.PlayerList)
                    if (a && a.photonView && a.photonView.Owner == p) return a.playerPing > 0 ? a.playerPing : -1; // vanilla value, refreshed only every ~6 s
            return -1;
        }

        private static void PublishRtt(float now)
        {
            if (!Gate.On || now < nextRttPublish) return;
            nextRttPublish = now + 2f;
            int ping = Mathf.Max(1, PhotonNetwork.GetPing());
            if (ping == lastPublishedRtt) return;
            lastPublishedRtt = ping;
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { RttKey, ping } });
        }

        private static void SampleTraffic(float now)
        {
            var peer = PhotonNetwork.NetworkingClient?.LoadBalancingPeer;
            if (peer == null) return;
            if (Gate.On && !peer.TrafficStatsEnabled) peer.TrafficStatsEnabled = true;
            int q = peer.QueuedOutgoingCommands;
            QueueNow = q;
            if (q > queueMaxWindow) queueMaxWindow = q;
            if (now - lastTrafficSample < 1f) return;
            float dt = now - lastTrafficSample;
            long ob = peer.TrafficStatsOutgoing.TotalPacketBytes, ib = peer.TrafficStatsIncoming.TotalPacketBytes, op = peer.TrafficStatsOutgoing.TotalPacketCount;
            if (lastTrafficSample > 0f && dt < 5f && ob >= prevOutBytes && ib >= prevInBytes)
            {
                OutKBps = (ob - prevOutBytes) / 1024f / dt;
                InKBps = (ib - prevInBytes) / 1024f / dt;
                OutPktPs = (op - prevOutPackets) / dt;
            }
            prevOutBytes = ob; prevInBytes = ib; prevOutPackets = op;
            lastTrafficSample = now;
            QueueMaxShown = queueMaxWindow;
            queueMaxWindow = 0;
        }

        public static string Region()
        {
            string region = PhotonNetwork.CloudRegion ?? "?";
            int slash = region.IndexOf('/');
            return slash >= 0 ? region.Substring(0, slash) : region;
        }

        private static float farRegionSince = -1f;
        private static bool farRegionToastShown;

        private static void CheckRegion(float now, int myPing)
        {
            if (farRegionToastShown) return;
            string region = Region();
            bool near = region == "jp" || region == "kr" || region == "?" || region == "";
            if (!PhotonNetwork.IsMasterClient || near || myPing <= 120) { farRegionSince = -1f; return; }
            if (farRegionSince < 0f) farRegionSince = now;
            if (now - farRegionSince < 10f) return;
            farRegionToastShown = true;
            string msg;
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean: msg = $"지금 서버 지역이 {region}이라 핑이 높아요. 메뉴에서 지역을 kr 또는 jp로 바꾸거나, 설정의 ForceRegion을 kr로 하세요."; break;
                case SystemLanguage.Japanese: msg = $"今のサーバー地域は {region} で、Pingが高くなっています。メニューで地域を kr か jp に変えるか、設定の ForceRegion を kr/jp にしてください。"; break;
                default: msg = $"Your Photon region is {region} and your ping is high. Pick region kr or jp in the menu, or set ForceRegion to kr/jp in the config."; break;
            }
            Plugin.Log.LogInfo($"High ping ({myPing}ms) on region '{region}' while hosting: showing region hint");
            Updater.ShowToast(msg);
        }

        private static void AdaptRates(float now, bool host)
        {
            int myPing = Mathf.Max(0, PhotonNetwork.GetPing());
            CheckRegion(now, myPing);
            bool lowFps = LocalFps > 0f && LocalFps < MinFpsForAdapt;
            myTracker.Add(myPing, now, 1e9f, SendDropRiseMs, lowFps);
            queueBadCount = QueueNow > QueueLimit ? queueBadCount + 1 : 0;

            // SendRate fallback (every player, host included)
            if (sendFallbackLogged && now >= SendFallbackUntil)
            {
                sendFallbackLogged = false;
                Plugin.Log.LogInfo($"SendRate restored to {Cfg.SendRate.Value}");
            }
            if (Gate.On && Cfg.SendRate.Value > VanillaSendRate && now >= SendFallbackUntil)
            {
                string why = null;
                if (myTracker.BadFor(now, SendBadSustainSeconds)) why = $"own RTT {myPing}ms (baseline {myTracker.Baseline:0}ms)";
                else if (queueBadCount >= 4) why = $"outgoing queue {QueueNow} commands";
                if (why != null)
                {
                    SendFallbackUntil = now + HoldSeconds;
                    sendFallbackLogged = true;
                    queueBadCount = 0;
                    Plugin.Log.LogInfo($"SendRate -> {VanillaSendRate} for {HoldSeconds:0}s: {why}");
                }
            }

            if (!host) { HighRateActive = false; highRateStableSince = -1f; return; }

            bool allKnown = true, allLow = true, anyBad = false;
            string badWho = null;
            foreach (var p in PhotonNetwork.PlayerList)
            {
                int r = RttOf(p);
                if (r < 0) { allKnown = false; continue; }
                if (!trackers.TryGetValue(p.ActorNumber, out var tr)) trackers[p.ActorNumber] = tr = new RttTracker();
                tr.Add(r, now, DropRttMs, DropRiseMs, p.IsLocal && lowFps);
                if (r >= RaiseMaxRttMs) allLow = false;
                if (tr.BadFor(now, BadSustainSeconds)) { anyBad = true; badWho = $"{p.NickName} RTT {r}ms (baseline {tr.Baseline:0}ms)"; }
            }

            bool wanted = Gate.On && Cfg.HighRateSync.Value && AllModded;
            if (!wanted)
            {
                if (HighRateActive)
                {
                    HighRateActive = false;
                    Plugin.Log.LogInfo($"HighRateSync off: {(Cfg.HighRateSync.Value ? "not every player has SmoothNet" : "disabled in config")}");
                }
                highRateStableSince = -1f;
                return;
            }
            if (queueBadCount >= 2 && HighRateActive) { anyBad = true; badWho = $"outgoing queue {QueueNow} commands"; }
            if (anyBad) highRateHoldUntil = Mathf.Max(highRateHoldUntil, now + HoldSeconds);
            if (HighRateActive)
            {
                if (anyBad)
                {
                    HighRateActive = false;
                    highRateStableSince = -1f;
                    Plugin.Log.LogInfo($"HighRateSync -> vanilla {VanillaSerializationRate} for >= {HoldSeconds:0}s: {badWho}");
                }
                return;
            }
            if (allKnown && allLow && !lowFps && LocalFps >= 45f && now >= highRateHoldUntil)
            {
                if (highRateStableSince < 0f) highRateStableSince = now;
                if (now - highRateStableSince >= RaiseStableSeconds)
                {
                    HighRateActive = true;
                    Plugin.Log.LogInfo($"HighRateSync -> {Cfg.HighRateValue.Value} (all RTT < {RaiseMaxRttMs:0}ms and stable)");
                }
            }
            else highRateStableSince = -1f;
        }

        // per-avatar receive timing (age of the last OnPhotonSerializeView in seconds)
        public struct RecvInfo { public float Age; public float LocalTime; }
        public static readonly Dictionary<PlayerAvatar, RecvInfo> AvatarRecv = new Dictionary<PlayerAvatar, RecvInfo>();

        private static PlayerAvatar hostAvatar;
        private static float hostAvatarLookup;

        // Presence value: "<version>;<flags>" where flags: e = mod enabled, c = cart prediction on, s = local cart simulation (1.1.0+; older versions ignore unknown flags).
        private static string Raw(Player p)
        {
            if (p?.CustomProperties == null) return null;
            return p.CustomProperties.TryGetValue(PlayerKey, out object v) ? v as string : null;
        }

        private static string Flags(Player p)
        {
            string r = Raw(p);
            int i = r?.IndexOf(';') ?? -1;
            return i >= 0 ? r.Substring(i + 1) : "";
        }

        public static string LocalPresence()
        {
            string flags = "";
            if (Gate.On) flags += "e";
            if (Gate.On && Cfg.PredictCart.Value) flags += "c";
            if (Gate.On && Cfg.PredictCart.Value && Cfg.LocalCartSim.Value) flags += "s";
            return Plugin.Version + ";" + flags;
        }

        /// <summary>Has SmoothNet installed AND enabled (so it follows the host's rate).</summary>
        public static bool HasMod(Player p) => Raw(p) != null && Flags(p).Contains("e");

        /// <summary>Predicts carts locally (host must not compensate them too).</summary>
        public static bool PredictsCart(Player p) => Raw(p) != null && Flags(p).Contains("c");

        /// <summary>Simulates the cart "now" with fresh input (1.1.0+): the host should steer toward its extrapolated present.</summary>
        public static bool SimsCart(Player p) => Raw(p) != null && Flags(p).Contains("s");

        public static string ModVersion(Player p)
        {
            string r = Raw(p);
            if (r == null) return null;
            int i = r.IndexOf(';');
            string ver = i >= 0 ? r.Substring(0, i) : r;
            return HasMod(p) ? ver : ver + " (off)";
        }

        /// <summary>My round trip time to the Photon game server, seconds.</summary>
        public static float MyRtt => Mathf.Max(0, PhotonNetwork.GetPing()) * 0.001f;

        /// <summary>Host's round trip time to the Photon game server (as reported by the host's avatar), seconds.</summary>
        public static float HostRtt
        {
            get
            {
                if (PhotonNetwork.IsMasterClient) return MyRtt;
                if ((!hostAvatar || hostAvatar.photonView == null || hostAvatar.photonView.Owner != PhotonNetwork.MasterClient)
                    && Time.unscaledTime > hostAvatarLookup)
                {
                    hostAvatarLookup = Time.unscaledTime + 1f;
                    hostAvatar = null;
                    if (GameDirector.instance != null)
                        foreach (var a in GameDirector.instance.PlayerList)
                            if (a && a.photonView && a.photonView.Owner == PhotonNetwork.MasterClient) { hostAvatar = a; break; }
                }
                return hostAvatar ? Mathf.Max(0, hostAvatar.playerPing) * 0.001f : MyRtt;
            }
        }

        /// <summary>One-way latency estimate client -> server -> host, seconds.</summary>
        public static float UplinkToHost => (MyRtt + HostRtt) * 0.5f;

        public static void Tick()
        {
            // local fps
            fpsAccum += Time.unscaledDeltaTime; fpsFrames++;
            if (fpsAccum >= 0.5f) { LocalFps = fpsFrames / fpsAccum; fpsAccum = 0; fpsFrames = 0; }

            if (!PhotonNetwork.InRoom) { AllModded = false; HostFps = -1; return; }
            if (Time.unscaledTime < nextTick) return;
            float now = Time.unscaledTime;
            nextTick = now + 0.5f;
            SampleTraffic(now);
            PublishRtt(now);

            var me = PhotonNetwork.LocalPlayer;
            string presence = LocalPresence();
            if (Raw(me) != presence)
                me.SetCustomProperties(new Hashtable { { PlayerKey, presence } });

            bool all = true;
            foreach (var p in PhotonNetwork.PlayerList)
                if (!HasMod(p)) { all = false; break; }
            AllModded = all;
            AdaptRates(now, PhotonNetwork.IsMasterClient);

            var room = PhotonNetwork.CurrentRoom;
            if (PhotonNetwork.IsMasterClient && room != null)
            {
                int desired = (Gate.On && Cfg.HighRateSync.Value && AllModded && HighRateActive)
                    ? Cfg.HighRateValue.Value : VanillaSerializationRate;
                int current = room.CustomProperties.TryGetValue(RoomRateKey, out object o) && o is int i ? i : -1;
                if (current != desired)
                    room.SetCustomProperties(new Hashtable { { RoomRateKey, desired } });

                if (Time.unscaledTime >= nextFpsPublish)
                {
                    nextFpsPublish = Time.unscaledTime + 2f;
                    room.SetCustomProperties(new Hashtable { { RoomFpsKey, Mathf.RoundToInt(LocalFps) } });
                }
            }

            HostFps = room != null && room.CustomProperties.TryGetValue(RoomFpsKey, out object f) && f is int fi ? fi : -1;
            ApplyRates();
        }

        public static int TargetSerializationRate()
        {
            var room = PhotonNetwork.CurrentRoom;
            if (!Gate.On || room == null) return VanillaSerializationRate;
            // Only trust the key while the current host runs SmoothNet (stale after host migration otherwise).
            if (!PhotonNetwork.IsMasterClient && !HasMod(PhotonNetwork.MasterClient)) return VanillaSerializationRate;
            if (room.CustomProperties.TryGetValue(RoomRateKey, out object o) && o is int r && r >= 10 && r <= 60)
                return r;
            return VanillaSerializationRate;
        }

        public static void ApplyRates()
        {
            int ser = TargetSerializationRate();
            int send = Gate.On ? Mathf.Max(SendFallbackActive ? VanillaSendRate : Cfg.SendRate.Value, ser) : VanillaSendRate;
            send = Mathf.Max(send, ser);
            // PUN requires SendRate >= SerializationRate, so order matters.
            if (PhotonNetwork.SendRate < ser) PhotonNetwork.SendRate = send;
            if (PhotonNetwork.SerializationRate != ser) PhotonNetwork.SerializationRate = ser;
            if (PhotonNetwork.SendRate != send) PhotonNetwork.SendRate = send;
        }

        public static bool TryGetAvatarAge(PlayerAvatar a, out float age)
        {
            if (a != null && AvatarRecv.TryGetValue(a, out var r))
            {
                age = r.Age + (Time.time - r.LocalTime);
                return true;
            }
            age = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkManager), "Start")]
    internal static class NetworkManagerStartPatch
    {
        // Vanilla NetworkManager.Start always assigns 25/25 in multiplayer (NetworkManager.cs:80-81);
        // VanillaSerializationRate/VanillaSendRate hold those constants.
        private static void Postfix()
        {
            NetSync.ResetAdaptive();
            Plugin.Log.LogInfo($"Photon region: {NetSync.Region()} (ping {PhotonNetwork.GetPing()}ms)");
            NetSync.ApplyRates();
            NetSync.AvatarRecv.Clear();
            Plugin.Log.LogInfo($"Rates applied: Send={PhotonNetwork.SendRate} Ser={PhotonNetwork.SerializationRate} (vanilla {NetSync.VanillaSendRate}/{NetSync.VanillaSerializationRate})");
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.OnPhotonSerializeView))]
    internal static class AvatarSerializePatch
    {
        private static void Postfix(PlayerAvatar __instance, PhotonStream stream, PhotonMessageInfo info)
        {
            if (stream.IsWriting) return;
            int diff = unchecked(PhotonNetwork.ServerTimestamp - info.SentServerTimestamp);
            float age = Mathf.Clamp(diff * 0.001f, 0f, 1f);
            NetSync.AvatarRecv[__instance] = new NetSync.RecvInfo { Age = age, LocalTime = Time.time };
        }
    }

    [HarmonyPatch(typeof(MenuPageRegions), nameof(MenuPageRegions.PickRegion))]
    internal static class ForceRegionPatch
    {
        private static void Prefix(ref string _region)
        {
            if (!Gate.On) return;
            string f = Cfg.ForceRegion.Value;
            if (string.IsNullOrEmpty(f) || f == "Off") return;
            Plugin.Log.LogInfo($"ForceRegion: '{_region}' -> '{f}'");
            _region = f;
        }
    }
}
