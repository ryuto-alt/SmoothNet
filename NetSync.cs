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

        public const int VanillaSerializationRate = 25;
        public const int VanillaSendRate = 25;
        public static bool AllModded;
        public static int HostFps = -1;

        private static float nextTick;
        private static float nextFpsPublish;
        private static float fpsAccum;
        private static int fpsFrames;
        public static float LocalFps;

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
            if (Cfg.Enabled.Value) flags += "e";
            if (Cfg.Enabled.Value && Cfg.PredictCart.Value) flags += "c";
            if (Cfg.Enabled.Value && Cfg.PredictCart.Value && Cfg.LocalCartSim.Value) flags += "s";
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
            nextTick = Time.unscaledTime + 0.5f;

            var me = PhotonNetwork.LocalPlayer;
            string presence = LocalPresence();
            if (Raw(me) != presence)
                me.SetCustomProperties(new Hashtable { { PlayerKey, presence } });

            bool all = true;
            foreach (var p in PhotonNetwork.PlayerList)
                if (!HasMod(p)) { all = false; break; }
            AllModded = all;

            var room = PhotonNetwork.CurrentRoom;
            if (PhotonNetwork.IsMasterClient && room != null)
            {
                int desired = (Cfg.Enabled.Value && Cfg.HighRateSync.Value && AllModded)
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
            if (!Cfg.Enabled.Value || room == null) return VanillaSerializationRate;
            // Only trust the key while the current host runs SmoothNet (stale after host migration otherwise).
            if (!PhotonNetwork.IsMasterClient && !HasMod(PhotonNetwork.MasterClient)) return VanillaSerializationRate;
            if (room.CustomProperties.TryGetValue(RoomRateKey, out object o) && o is int r && r >= 10 && r <= 60)
                return r;
            return VanillaSerializationRate;
        }

        public static void ApplyRates()
        {
            int ser = TargetSerializationRate();
            int send = Cfg.Enabled.Value ? Mathf.Max(Cfg.SendRate.Value, ser) : VanillaSendRate;
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
            if (!Cfg.Enabled.Value) return;
            string f = Cfg.ForceRegion.Value;
            if (string.IsNullOrEmpty(f) || f == "Off") return;
            Plugin.Log.LogInfo($"ForceRegion: '{_region}' -> '{f}'");
            _region = f;
        }
    }
}
