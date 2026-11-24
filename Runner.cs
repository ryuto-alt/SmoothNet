using System;
using System.Text;
using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SmoothNet
{
    /// <summary>Per-frame driver + network HUD (IMGUI, English so every player can read it).</summary>
    internal class Runner : MonoBehaviour
    {
        private bool show;
        private string text = "";
        private float nextText;
        private GUIStyle style;
        private Texture2D bg;

        private int awake = -1, totalObjs = -1;
        private float nextPhysCount;

        private void Start()
        {
            show = Cfg.HudOnStart.Value;
            StartCoroutine(Updater.Routine());
        }

        private void Update()
        {
            try
            {
                NetSync.Tick();
                StartupPrompt.Tick();
                Updater.Tick();
                string keyName = Cfg.HudKey.Value;
                if (keyName != "None")
                {
                    bool pressed = false;
                    var kb = Keyboard.current;
                    if (kb != null && Enum.TryParse(keyName, out Key key) && kb[key].wasPressedThisFrame) pressed = true;
                    if (!pressed && legacyInputOk && Enum.TryParse(keyName, out KeyCode kc))
                    {
                        try { pressed = Input.GetKeyDown(kc); }
                        catch (InvalidOperationException) { legacyInputOk = false; }
                    }
                    if (pressed) Toggle("input");
                }

                if (show && PhotonNetwork.IsMasterClient && Time.unscaledTime > nextPhysCount)
                {
                    nextPhysCount = Time.unscaledTime + 1f;
                    CountAwake();
                }
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private void LateUpdate()
        {
            try { History.Record(); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private void CountAwake()
        {
            var rd = RoundDirector.instance;
            if (!rd || rd.physGrabObjects == null) { awake = totalObjs = -1; return; }
            int a = 0, t = 0;
            foreach (var o in rd.physGrabObjects)
            {
                if (!o || !o.rb) continue;
                t++;
                if (!o.rb.isKinematic && !o.rb.IsSleeping()) a++;
            }
            awake = a; totalObjs = t;
        }

        private bool legacyInputOk = true;
        private float lastToggleTime = -10f;

        private void Toggle(string source)
        {
            if (Time.unscaledTime - lastToggleTime < 0.2f) return; // several input paths may fire for one press
            lastToggleTime = Time.unscaledTime;
            show = !show;
            Plugin.Log.LogInfo($"HUD {(show ? "shown" : "hidden")} ({source})");
        }

        private void OnGUI()
        {
            var ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && Cfg.HudKey.Value != "None"
                && Enum.TryParse(Cfg.HudKey.Value, out KeyCode kc) && ev.keyCode == kc)
            {
                // IMGUI events come straight from window messages, independent of the Input System setup.
                Toggle("gui");
            }
            Updater.DrawToast();
            if (!show) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14, wordWrap = false };
                style.normal.textColor = Color.white;
                bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.65f));
                bg.Apply();
            }
            if (Time.unscaledTime > nextText)
            {
                nextText = Time.unscaledTime + 0.25f;
                try { text = BuildText(); } catch (Exception e) { text = "SmoothNet HUD error: " + e.Message; }
            }
            var content = new GUIContent(text);
            Vector2 size = style.CalcSize(content);
            var rect = new Rect(12, 12, size.x + 20, size.y + 16);
            GUI.DrawTexture(rect, bg);
            GUI.Label(new Rect(rect.x + 10, rect.y + 8, size.x, size.y), content, style);
        }

        private static string Ms(float seconds) => Mathf.RoundToInt(seconds * 1000f) + "ms";
        private static string Col(string s, string c) => $"<color={c}>{s}</color>";
        private static string PingCol(int ms) => ms <= 60 ? "#7CFC00" : ms <= 120 ? "#FFD700" : "#FF6347";

        private string BuildText()
        {
            var sb = new StringBuilder();
            sb.Append($"<b>SmoothNet {Plugin.Version}</b>  <size=11>[{Cfg.HudKey.Value}]</size>");
            if (!Gate.On) sb.Append("  " + Col(!Cfg.Enabled.Value ? "DISABLED" : Gate.Answered ? "OFF (this session)" : "OFF (waiting for startup choice)", "#FF6347"));
            else sb.Append("  " + Col("ON", "#7CFC00"));
            sb.Append('\n');

            if (Updater.StagedVersion != null)
                sb.Append(Col($"Update {Updater.StagedVersion} ready - restart the game to apply\n", "#7CFC00"));

            if (!PhotonNetwork.InRoom)
            {
                sb.Append("Not in a multiplayer room.\nFPS: " + Mathf.RoundToInt(NetSync.LocalFps));
                return sb.ToString();
            }

            string region = NetSync.Region();
            var proto = PhotonNetwork.NetworkingClient.LoadBalancingPeer.UsedProtocol;
            int myPing = PhotonNetwork.GetPing();
            bool host = PhotonNetwork.IsMasterClient;

            sb.Append($"Region <b>{region}</b>   Protocol {(proto == ConnectionProtocol.Udp ? "UDP" : Col(proto.ToString(), "#FF6347"))}   Ping {Col(myPing + "ms", PingCol(myPing))}\n");
            sb.Append($"Role <b>{(host ? "HOST" : "Client")}</b>   FPS {Mathf.RoundToInt(NetSync.LocalFps)}");
            if (!host) sb.Append($"   Host FPS {(NetSync.HostFps >= 0 ? Col(NetSync.HostFps.ToString(), NetSync.HostFps >= 50 ? "#7CFC00" : NetSync.HostFps >= 30 ? "#FFD700" : "#FF6347") : "?")}");
            sb.Append('\n');

            bool high = PhotonNetwork.SerializationRate > NetSync.VanillaSerializationRate;
            sb.Append($"Rates send {PhotonNetwork.SendRate}/s  sync {PhotonNetwork.SerializationRate}/s  {(high ? Col("[HighRate ON]", "#7CFC00") : "")}");
            if (NetSync.SendFallbackActive) sb.Append("  " + Col($"[SendRate fallback {Mathf.CeilToInt(NetSync.SendFallbackUntil - Time.unscaledTime)}s]", "#FFD700"));
            if (host && Cfg.HighRateSync.Value && !NetSync.HighRateActive && NetSync.AllModded)
                sb.Append("  " + Col(NetSync.HighRateHoldRemaining > 0f ? $"[HighRate held {Mathf.CeilToInt(NetSync.HighRateHoldRemaining)}s]" : "[HighRate waiting for stable RTT]", "#FFD700"));
            sb.Append('\n');
            sb.Append($"Traffic out {NetSync.OutKBps:0.0} KB/s ({NetSync.OutPktPs:0} pkt/s)  in {NetSync.InKBps:0.0} KB/s  queue {NetSync.QueueNow} (max {NetSync.QueueMaxShown})\n");

            if (!host)
            {
                string mode = Predictor.CurrentMode == Predictor.Mode.None ? "idle" : Predictor.CurrentMode.ToString();
                sb.Append($"Prediction <b>{mode}</b>");
                if (Predictor.CurrentMode == Predictor.Mode.CartSim)
                    sb.Append($"  sim vs host: {CartSim.LastPosError:0.00}m {Mathf.Abs(CartSim.LastYawError):0}deg");
                else if (Predictor.CurrentMode == Predictor.Mode.HeldSim)
                    sb.Append($"  sim vs host: {HeldSim.LastPosError:0.00}m {HeldSim.LastRotError:0}deg");
                else if (Predictor.CurrentMode != Predictor.Mode.None)
                    sb.Append($"  ahead {Ms(Predictor.LastLag)}  err {Predictor.LastError:0.00}m" +
                              (Predictor.CurrentMode == Predictor.Mode.Held ? $"  follow {Predictor.LastFollow:0.0}" : ""));
                sb.Append($"   est. to host ~{Ms(NetSync.UplinkToHost)}\n");
                if (HeldSim.Current == HeldSim.Kind.Vanilla) sb.Append("Held door/hinge: vanilla follow (no prediction)\n");
            }
            else
            {
                sb.Append($"Physics awake {(awake >= 0 ? awake + "/" + totalObjs : "-")}\n");
            }

            if (SnapStats.Count > 0)
                sb.Append(Col($"Snaps {SnapStats.Count}  last: {SnapStats.LastName} [{SnapStats.LastKind}] {SnapStats.LastError:0.0}m {Mathf.RoundToInt(Time.unscaledTime - SnapStats.LastTime)}s ago\n", "#FFD700"));
            else sb.Append("Snaps 0\n");

            sb.Append("<size=12>Players  (ping / to-host RTT / mod)</size>\n");
            float hostRtt = NetSync.HostRtt;
            int missing = 0, differentVer = 0;
            if (GameDirector.instance != null)
            {
                foreach (var a in GameDirector.instance.PlayerList)
                {
                    if (!a || !a.photonView) continue;
                    var owner = a.photonView.Owner;
                    bool isHost = owner != null && owner.IsMasterClient;
                    int reported = (!a.isLocal && owner != null) ? NetSync.ReportedRtt(owner) : -1;
                    int ping = a.isLocal ? myPing : (reported > 0 ? reported : a.playerPing);
                    string toHost = isHost ? "host" : Ms(ping * 0.001f + hostRtt);
                    string ver = NetSync.ModVersion(owner);
                    if (ver == null) missing++;
                    else if (ver != Plugin.Version) differentVer++;
                    string name = string.IsNullOrEmpty(a.playerName) ? "?" : a.playerName;
                    if (name.Length > 16) name = name.Substring(0, 16);
                    sb.Append($"  {name,-16} {Col(ping + "ms", PingCol(ping)),-6}  {toHost,-6}  {(ver != null ? Col("v" + ver, "#7CFC00") : Col("no mod", "#FFD700"))}\n");
                }
            }

            // warnings
            if (proto != ConnectionProtocol.Udp)
                sb.Append(Col("! Fell back to TCP/WebSocket - reconnect for UDP\n", "#FF6347"));
            if (myPing > 150)
                sb.Append(Col("! High ping to server - host should pick region jp or kr\n", "#FF6347"));
            if (!host && NetSync.HostFps >= 0 && NetSync.HostFps < 30)
                sb.Append(Col("! Host FPS is low - everyone's physics will stutter\n", "#FF6347"));
            if (missing > 0)
                sb.Append(Col($"! {missing} player(s) without SmoothNet: client-side smoothing off for them\n", "#FFD700"));
            if (differentVer > 0)
                sb.Append(Col($"! {differentVer} player(s) run a different SmoothNet version (yours: v{Plugin.Version})\n", "#FFD700"));
            if (host && Cfg.HighRateSync.Value && !NetSync.AllModded)
                sb.Append(Col("HighRateSync waiting: not everyone has SmoothNet\n", "#FFD700"));

            return sb.ToString().TrimEnd('\n');
        }
    }
}
