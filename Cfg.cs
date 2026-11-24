using BepInEx.Configuration;

namespace SmoothNet
{
    internal static class Cfg
    {
        // General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<string> HudKey;
        public static ConfigEntry<bool> HudOnStart;
        public static ConfigEntry<bool> AutoUpdate;
        public static ConfigEntry<bool> AskOnStartup;
        public static ConfigEntry<bool> StartupChoice;
        public static ConfigEntry<int> ConfigRevision;

        // Network
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<bool> HighRateSync;
        public static ConfigEntry<int> HighRateValue;
        public static ConfigEntry<string> ForceRegion;
        public static ConfigEntry<bool> AutoBestRegion;

        // Client prediction
        public static ConfigEntry<bool> PredictHeld;
        public static ConfigEntry<bool> PredictCart;
        public static ConfigEntry<bool> LocalCartSim;
        public static ConfigEntry<float> CartSimCorrection;
        public static ConfigEntry<bool> LocalHeldSim;
        public static ConfigEntry<float> HeldSimCorrection;
        public static ConfigEntry<float> PredictionStrength;
        public static ConfigEntry<float> ExtraLatency;
        public static ConfigEntry<float> MaxPredictionTime;
        public static ConfigEntry<float> ReleaseBlendTime;
        public static ConfigEntry<bool> ExtrapolateOthers;
        public static ConfigEntry<float> ExtrapolateMaxTime;
        public static ConfigEntry<bool> SmoothRemotePlayers;
        public static ConfigEntry<float> RemotePlayerMaxTime;

        // Host
        public static ConfigEntry<bool> CompensateUnmoddedCart;
        public static ConfigEntry<bool> CartSleepFix;
        public static ConfigEntry<bool> CollisionGcFix;

        public static void Bind(ConfigFile c)
        {
            Enabled = c.Bind("General", "Enabled", true,
                "SmoothNet 全体のON/OFF / Master switch");
            HudKey = c.Bind("General", "HudKey", "F8",
                new ConfigDescription("ネットワークHUDの表示切替キー / Key to toggle the network HUD",
                    new AcceptableValueList<string>("F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "None")));
            HudOnStart = c.Bind("General", "HudOnStart", false,
                "起動時にHUDを表示する / Show HUD on startup");
            AutoUpdate = c.Bind("General", "AutoUpdate", true,
                "新しいバージョンを自動でダウンロードし、次回起動時に適用する(署名検証あり) / Download signed updates in the background and apply them on the next launch");

            AskOnStartup = c.Bind("General", "AskOnStartup", true,
                "ゲーム起動のたびに、メインメニューでMODをこのプレイで使うか確認する / Ask at every launch (main menu) whether to use SmoothNet this session");
            StartupChoice = c.Bind("General", "StartupChoice", true,
                "前回の起動時の選択(確認画面の初期選択) / Last startup choice (pre-selected in the prompt)");
            ConfigRevision = c.Bind("General", "ConfigRevision", 0,
                "内部用 / Internal: config migration marker");

            SendRate = c.Bind("Network", "SendRate", 30,
                new ConfigDescription("パケット送信頻度(バニラ25)。RPCや入力が早く届く。1人だけ変えても安全 / Photon SendRate (vanilla 25). Safe per-player.",
                    new AcceptableValueRange<int>(25, 100)));
            HighRateSync = c.Bind("Network", "HighRateSync", false,
                "【実験的】全員がSmoothNetを入れている時だけ同期レートを上げる(ホストが自動管理)。実効 約31Hz→約62Hz、通信量約2倍なので4人以下推奨 / EXPERIMENTAL, adaptive: raise SerializationRate when ALL players have SmoothNet and every RTT is low and stable; falls back to vanilla for 30 s+ when someone's RTT climbs (host decides). Effective ~31Hz -> ~62Hz, ~2x traffic.");
            HighRateValue = c.Bind("Network", "HighRateValue", 30,
                new ConfigDescription("HighRateSync時の同期レート(PUNは間隔-8msで送るため40で実効約62Hz) / SerializationRate used by HighRateSync (30 = ~40Hz, 40 = ~62Hz effective; default 30 keeps traffic +30% only)",
                    new AcceptableValueRange<int>(30, 40)));
            ForceRegion = c.Bind("Network", "ForceRegion", "Off",
                new ConfigDescription("ホスト時に地域を強制(Offならメニューで選んだ地域)。日韓なら jp か kr / Force Photon region when hosting. For JP+KR use jp or kr.",
                    new AcceptableValueList<string>("Off", "jp", "kr", "asia", "hk", "us", "usw", "eu")));

            AutoBestRegion = c.Bind("Network", "AutoBestRegion", true,
                "自分がホストとしてロビーを作る時、選んだ地域よりPingが50ms以上低い地域があればそちらでホストする(他人のロビーに参加する時は変更しない。ForceRegion優先) / When creating a lobby as host, use the lowest-ping Photon region if the selected one is >50 ms slower (never when joining; ForceRegion wins)");

            // One-time migration of the old defaults (SendRate 60, HighRateValue 40) to the more conservative ones.
            if (ConfigRevision.Value < 1)
            {
                if (SendRate.Value == 60) SendRate.Value = 30;
                if (HighRateValue.Value == 40) HighRateValue.Value = 30;
                ConfigRevision.Value = 1;
                c.Save();
            }

            PredictHeld = c.Bind("ClientPrediction", "PredictHeld", true,
                "掴んだ物を自分の入力で即座に動かして見せる(クライアント時) / Predict objects you hold (as client)");
            PredictCart = c.Bind("ClientPrediction", "PredictCart", true,
                "押しているカートと中身を即座に動かして見せる(クライアント時) / Predict the cart you push and its contents");
            LocalCartSim = c.Bind("ClientPrediction", "LocalCartSim", true,
                "カートを押す時、自分の画面でカートを直接シミュレーションして即座に反応させる(クライアント時)。OFFで1.0.0の挙動 / Simulate the cart you push locally for instant response (as client). Off = 1.0.0 behaviour");
            CartSimCorrection = c.Bind("ClientPrediction", "CartSimCorrection", 3.0f,
                new ConfigDescription("ローカルのカートをホストの位置へ引き戻す強さ。大きいほど正確、小さいほど滑らか / How strongly the local cart is pulled toward the host's cart. Higher = more accurate, lower = smoother",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            LocalHeldSim = c.Bind("ClientPrediction", "LocalHeldSim", true,
                "掴んだ物を、自分の画面でバニラと同じ物理(ばね+回転)でシミュレーションして即座に反応させる(クライアント時、自分だけが掴んでいる時)。OFFで1.2.0の挙動 / Simulate the object you hold locally with vanilla grab physics for instant response (as client, sole grabber). Off = 1.2.0 behaviour");
            HeldSimCorrection = c.Bind("ClientPrediction", "HeldSimCorrection", 4.0f,
                new ConfigDescription("ローカルの掴んだ物をホストの位置へ引き戻す強さ。大きいほど正確、小さいほど滑らか / How strongly the locally simulated held object is pulled toward the host's. Higher = more accurate, lower = smoother",
                    new AcceptableValueRange<float>(0.5f, 12f)));
            PredictionStrength = c.Bind("ClientPrediction", "PredictionStrength", 1.0f,
                new ConfigDescription("予測の強さ。行き過ぎるなら下げる / Prediction strength. Lower if it overshoots",
                    new AcceptableValueRange<float>(0f, 1.5f)));
            ExtraLatency = c.Bind("ClientPrediction", "ExtraLatency", 0.05f,
                new ConfigDescription("ホスト側の追従遅れ見込み(秒) / Extra seconds assumed for host-side settling",
                    new AcceptableValueRange<float>(0f, 0.2f)));
            MaxPredictionTime = c.Bind("ClientPrediction", "MaxPredictionTime", 0.4f,
                new ConfigDescription("予測する最大時間(秒) / Max seconds to predict ahead",
                    new AcceptableValueRange<float>(0.05f, 0.8f)));
            ReleaseBlendTime = c.Bind("ClientPrediction", "ReleaseBlendTime", 0.3f,
                new ConfigDescription("手を離した後、予測からホスト位置に戻す時間(秒) / Seconds to blend back after release",
                    new AcceptableValueRange<float>(0.05f, 1f)));
            ExtrapolateOthers = c.Bind("ClientPrediction", "ExtrapolateOthers", true,
                "他人が持つ物・動くカート・投げられた物を速度で先読み表示 / Velocity-extrapolate objects moved by others");
            ExtrapolateMaxTime = c.Bind("ClientPrediction", "ExtrapolateMaxTime", 0.25f,
                new ConfigDescription("速度先読みの最大時間(秒) / Max extrapolation seconds",
                    new AcceptableValueRange<float>(0.05f, 0.5f)));
            SmoothRemotePlayers = c.Bind("ClientPrediction", "SmoothRemotePlayers", true,
                "他プレイヤーの見た目を遅延分先読みして描画(見た目のみ) / Draw other players ahead by their latency (visual only)");
            RemotePlayerMaxTime = c.Bind("ClientPrediction", "RemotePlayerMaxTime", 0.15f,
                new ConfigDescription("他プレイヤー先読みの最大時間(秒) / Max remote player extrapolation seconds",
                    new AcceptableValueRange<float>(0f, 0.3f)));

            CompensateUnmoddedCart = c.Bind("Host", "CompensateUnmoddedCart", true,
                "ホスト時、MOD未導入の人が押すカートを遅延補正する / As host, latency-compensate carts pushed by players without SmoothNet");
            CartSleepFix = c.Bind("Host", "CartSleepFix", true,
                "止まったカートと中身を物理的にスリープさせて負荷を減らす(バグ修正) / Let idle carts and contents sleep (fixes vanilla bug)");
            CollisionGcFix = c.Bind("Host", "CollisionGcFix", true,
                "衝突処理のメモリ確保を減らしカクつき(GC)を抑える / Reduce GC spikes from collision callbacks");
        }
    }
}
