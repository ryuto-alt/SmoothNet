# SmoothNet — R.E.P.O. lag reduction

**日本語 / 한국어 / English**

---

## 🇯🇵 日本語

R.E.P.O.のマルチプレイで、**カートが重い、掴んだ物がワンテンポ遅れる、他の人がカクカク動く**といったラグ感を減らすMODです。

### 何をするの？

| 機能 | 誰に効く | 内容 |
|---|---|---|
| 掴んだ物の予測 | クライアント | 掴んだ物が、マウスの動きにすぐ反応します |
| カートの予測 | クライアント | 押しているカートと中身が、自分の動きにすぐ追従します |
| 他人が動かす物の先読み | クライアント | 投げられた物や他人が持つ物が、今あるはずの位置に表示されます |
| 他プレイヤーの描画先読み | クライアント | 他の人の動きが、遅延ぶん補正されて滑らかになります |
| 送信頻度アップ | 全員 | 操作や合図が早く相手に届きます |
| MOD未導入者のカート補正 | ホスト | MODを入れていない人が押すカートも、ホスト側で遅延を補正します |
| カートのスリープ修正 | ホスト | 止まっているカートと中身の物理計算を止めて、ホストを軽くします |
| GCスパイク削減 | ホスト | 衝突処理のゴミを減らして、ホストのカクつきを抑えます |
| ネットワークHUD（F8） | 全員 | 地域、Ping、ホストFPS、MOD導入状況を表示します |

**ホストの判定（ダメージ、お金、破損など）は一切変えていません。** クライアント側の予測は「見た目と手応え」を先取りするだけで、正解は常にホストが持っています。予測がずれても、すぐホストの状態に戻ります。

### 導入
1. BepInExを入れる（r2modman / Thunderstore Mod Manager 推奨）。
2. `SmoothNet.dll` を `BepInEx/plugins/` に入れる。
3. **一緒に遊ぶ人も全員入れるのがおすすめです。** クライアント側の機能は、入れた人にだけ効きます。

### おすすめ設定（日韓で遊ぶ場合）
- **ホストは、ゲームの地域選択で `jp` か `kr` を選んでください。** `asia`（シンガポール）は遠いので避けましょう。
- 設定は、ゲーム内のREPOConfig（MODSメニュー）か `BepInEx/config/ryuto.smoothnet.cfg` で変えられます。
- 予測が行き過ぎると感じたら `PredictionStrength` を 0.7 くらいに下げてください。

### 1.4.0
- **アップデートがゲームを1回起動するだけで反映されるようになりました。** ゲーム起動直後（MODの読み込み前）に最大4秒だけ最新版を確認し、新しければその場で署名を検証して適用します。ネットワークが遅い・つながらない場合は待たずに通常どおり起動し、これまでどおりバックグラウンドで取得します。`General.AutoUpdate` をOFFにすると確認しません。
- バックグラウンドで取得した場合は、メインメニューで「今すぐ再起動して適用しますか？」と聞きます（「再起動する」を押した時だけ、Steam経由で自動的に起動し直します。ロビーやゲーム中には出ません）。
- アップデーター本体（SmoothNetUpdater.dll）も署名付きで配布し、ゲーム終了後に自動で入れ替えます。1.2.0/1.3.xの人も再インストール不要です。

### 1.3.1
- 起動時のオン/オフ選択でマウスカーソルが見えない問題を修正しました。ゲーム本来のメニュー（ポップアップ）に変更したので、マウスでもキーボード/コントローラーでも操作できます。「次回から聞かない」のチェックは廃止し、聞かないようにするには設定の `General.AskOnStartup` をOFFにしてください。戻る/Escはオフ扱いです。

### 1.3.0
- **掴んだ物とドアが瞬間移動する問題を修正しました。** ドアはクライアント側に蝶番(ジョイント)が無く、予測がありえない位置へ動かしていたのが原因です。ドアはバニラの追従に戻し、予測を一切かけません。
- 掴んだ物は、自分の画面でバニラと同じ物理（ばね＋回転）をシミュレーションして即座に反応します（自分だけが掴んでいる時。ホストの判定は変わりません）。ホストのパケットで急に位置が飛ぶことがなくなります。
- 新しい設定: `LocalHeldSim`（ON/OFF。OFFで1.2.0の動作）、`HeldSimCorrection`（ホストの位置へ引き戻す強さ、既定4.0）。
- **HighRateSync（既定OFFのまま）は自動調整式になりました。** 全員のPingが100ms未満で10秒安定している時だけ上げ、誰かのPingが120ms超、またはベースラインより40ms以上上がって1秒続くと、30秒以上バニラ(25)に戻します。`SendRate` も、自分のPingが上がるか送信キューが溜まると30秒だけ25に戻ります。切り替えはログに記録されます。**ただしHighRateSyncは実験的で、回線が弱い人のPingを上げることがあるため、既定OFFのままにしてください。**
- F8 HUD: 通信量（送信/受信KB/s、パケット/s、送信キュー）、各プレイヤーのPing、瞬間移動(スナップ)の回数と直近の内容を表示します。ホストでPingが高いのにサーバー地域が jp/kr でない場合は、メニューで地域変更を促す通知が出ます（`ForceRegion` の既定は変えていません）。
- **起動時の確認:** ゲームを起動してメインメニューが出ると、「このプレイでSmoothNetを使うか」を聞きます（オン/オフ）。**オフを選ぶとバニラと同じ動作**になり、他のプレイヤーからも「MODなし」として扱われます（自動アップデートは動き続けます）。設定: `General.AskOnStartup`（既定ON）。F8のHUDに現在の状態が出ます。
- **ホストの地域を自動で最適化:** 自分がロビーを作る時、選んだ地域より Ping が50ms以上低い地域があれば、そちらでホストして通知します（他人のロビーに参加する時は変更しません。`ForceRegion` を設定していればそちら優先）。設定: `Network.AutoBestRegion`（既定ON）。
- 既定値を控えめにしました: `SendRate` 60→30、`HighRateValue` 40→30（約40Hz）。古い既定値のままの設定は自動で新しい値になります。HighRateSyncは「全員のPingが100ms未満で10秒安定」してからだけ上がり、少しでも悪化すると即座にバニラへ戻ります。

### 1.2.0 / 自動アップデート
- 新しいバージョンを**自動でダウンロードし、次にゲームを起動した時に適用**します。ダウンロード完了時にメニューで通知が出ます。
- アップデートは作者の秘密鍵による**署名つき**です。署名・ハッシュ・DLLの名前とバージョンを確認してから適用し、失敗したら何もしません。起動に2回失敗したら自動で元のバージョンに戻ります。
- 自動更新には `patchers/SmoothNetUpdater.dll` が必要です（SmoothNet-Setup.exe やThunderstore/r2modmanなら自動で入ります。手動導入なら `BepInEx/patchers/ryuto-SmoothNet/` に置いてください）。
- 設定 `General.AutoUpdate`（既定ON）でOFFにできます。HUD(F8)にバージョンと、他の人とバージョンが違う時の警告が出ます。

### 1.1.0
- カートを押す時、自分の画面でカートを直接シミュレーションして即座に反応します（ホストの判定は変わりません）。
- 新しい設定: `LocalCartSim`（ON/OFF。OFFで1.0.0の動作）、`CartSimCorrection`（ホストのカートへ引き戻す強さ、既定3.0）。

---

## 🇰🇷 한국어

R.E.P.O. 멀티플레이에서 **카트가 무겁게 느껴지거나, 잡은 물건이 늦게 따라오거나, 다른 플레이어가 끊겨 보이는** 렉 체감을 줄여 주는 모드입니다.

### 기능

| 기능 | 대상 | 설명 |
|---|---|---|
| 잡은 물건 예측 | 클라이언트 | 잡은 물건이 마우스 움직임에 바로 반응합니다 |
| 카트 예측 | 클라이언트 | 미는 카트와 안의 물건이 내 움직임을 바로 따라옵니다 |
| 다른 사람이 움직이는 물건 보정 | 클라이언트 | 던져진 물건이나 다른 사람이 든 물건이 현재 위치에 표시됩니다 |
| 다른 플레이어 표시 보정 | 클라이언트 | 다른 사람의 움직임이 지연만큼 보정되어 부드러워집니다 |
| 전송 빈도 증가 | 모두 | 조작과 신호가 더 빨리 전달됩니다 |
| 모드 없는 플레이어의 카트 보정 | 호스트 | 모드가 없는 사람이 미는 카트도 호스트에서 지연을 보정합니다 |
| 카트 슬립 버그 수정 | 호스트 | 멈춘 카트와 안의 물건의 물리 연산을 멈춰 호스트 부하를 줄입니다 |
| GC 스파이크 감소 | 호스트 | 충돌 처리에서 생기는 메모리 낭비를 줄여 끊김을 줄입니다 |
| 네트워크 HUD (F8) | 모두 | 지역, 핑, 호스트 FPS, 모드 설치 여부를 표시합니다 |

**호스트의 판정(데미지, 돈, 파손 등)은 전혀 바꾸지 않습니다.** 클라이언트 예측은 화면과 조작감만 앞당기며, 정답은 항상 호스트에 있습니다. 예측이 틀려도 바로 호스트 상태로 돌아갑니다.

### 설치
1. BepInEx를 설치합니다 (r2modman / Thunderstore Mod Manager 추천).
2. `SmoothNet.dll`을 `BepInEx/plugins/`에 넣습니다.
3. **같이 하는 사람 모두 설치하는 것을 추천합니다.** 클라이언트 기능은 설치한 사람에게만 적용됩니다.

### 추천 설정 (한일 플레이)
- **호스트는 지역 선택에서 `jp` 또는 `kr`을 고르세요.** `asia`(싱가포르)는 멀어서 피하는 게 좋습니다.
- 설정은 게임 내 REPOConfig(MODS 메뉴) 또는 `BepInEx/config/ryuto.smoothnet.cfg`에서 바꿀 수 있습니다.
- 예측이 너무 앞서 나간다고 느껴지면 `PredictionStrength`를 0.7 정도로 낮추세요.

### 1.4.0
- **업데이트가 게임을 한 번만 켜도 바로 적용돼요.** 게임을 켜자마자(모드 로딩 전) 최대 4초 동안 최신 버전을 확인하고, 새 버전이면 서명을 검증한 뒤 그 자리에서 적용합니다. 네트워크가 느리거나 연결되지 않으면 기다리지 않고 평소처럼 시작하며, 예전처럼 백그라운드에서 받습니다. `General.AutoUpdate`를 끄면 확인하지 않습니다.
- 백그라운드로 받은 경우에는 메인 메뉴에서 "지금 다시 시작해서 적용할까요?"라고 묻습니다 ("다시 시작"을 눌렀을 때만 Steam으로 자동 재실행합니다. 로비나 게임 중에는 뜨지 않습니다).
- 업데이트 프로그램(SmoothNetUpdater.dll) 자체도 서명해서 배포하고 게임 종료 후 자동으로 교체합니다. 1.2.0/1.3.x 사용자도 다시 설치할 필요가 없어요.

### 1.3.1
- 시작 화면의 켜기/끄기 선택에 마우스 커서가 보이도록 게임 메뉴로 변경했습니다. 마우스와 키보드/컨트롤러 모두 쓸 수 있습니다. "다음부터 묻지 않기" 체크는 없어졌으니, 묻지 않으려면 설정의 `General.AskOnStartup`을 끄세요. 뒤로가기/Esc는 "끄기"로 처리됩니다.

### 1.3.0
- **들고 있는 물건과 문이 순간이동하던 문제를 수정했습니다.** 문은 클라이언트에 경첩(조인트)이 없는데 예측이 불가능한 위치로 밀어 넣고 있었습니다. 이제 문은 예측 없이 바닐라 추적을 그대로 씁니다.
- 잡은 물건은 내 화면에서 바닐라와 같은 물리(스프링 + 회전)를 직접 시뮬레이션해 바로 반응합니다 (나 혼자 잡고 있을 때. 호스트의 판정은 바뀌지 않습니다). 호스트 패킷이 올 때마다 위치가 튀는 현상이 사라집니다.
- 새 설정: `LocalHeldSim` (ON/OFF, OFF면 1.2.0 동작), `HeldSimCorrection` (호스트 위치로 되돌리는 강도, 기본 4.0).
- **HighRateSync(기본 OFF 그대로)가 자동 조절식이 되었습니다.** 모든 플레이어의 핑이 100ms 미만으로 10초 안정적일 때만 올리고, 누군가의 핑이 120ms를 넘거나 기준치보다 40ms 이상 올라 1초 지속되면 30초 이상 바닐라(25)로 되돌립니다. `SendRate`도 내 핑이 오르거나 송신 큐가 쌓이면 30초 동안 25로 돌아갑니다. 전환은 로그에 기록됩니다. **다만 HighRateSync는 실험 기능이라 회선이 약한 사람의 핑을 올릴 수 있으므로 기본 OFF로 두세요.**
- F8 HUD: 트래픽(송신/수신 KB/s, 패킷/s, 송신 큐), 플레이어별 핑, 순간이동(스냅) 횟수와 마지막 내용을 표시합니다. 호스트인데 핑이 높고 서버 지역이 jp/kr이 아니면 지역을 바꾸라는 알림이 뜹니다 (`ForceRegion` 기본값은 그대로).
- **시작 시 확인:** 게임을 켜고 메인 메뉴가 나오면 "이번 판에 SmoothNet을 쓸지" 묻습니다 (켜기/끄기). **끄면 바닐라와 완전히 같게 동작**하고, 다른 플레이어에게도 "모드 없음"으로 보입니다 (자동 업데이트는 계속 동작). 설정: `General.AskOnStartup` (기본 ON). F8 HUD에 현재 상태가 표시됩니다.
- **호스트 지역 자동 최적화:** 내가 로비를 만들 때, 고른 지역보다 핑이 50ms 이상 낮은 지역이 있으면 그쪽으로 호스트하고 알려줍니다 (남의 로비에 들어갈 때는 바꾸지 않습니다. `ForceRegion`이 설정되어 있으면 그쪽이 우선). 설정: `Network.AutoBestRegion` (기본 ON).
- 기본값을 보수적으로 바꿨습니다: `SendRate` 60→30, `HighRateValue` 40→30 (약 40Hz). 예전 기본값 그대로인 설정은 자동으로 새 값이 됩니다. HighRateSync는 "모두의 핑이 100ms 미만으로 10초 안정"된 뒤에만 올라가고, 조금이라도 나빠지면 즉시 바닐라로 돌아갑니다.

### 1.2.0 / 자동 업데이트
- 새 버전을 **자동으로 내려받고, 다음에 게임을 켤 때 적용**합니다. 다운로드가 끝나면 메뉴에 알림이 뜹니다.
- 업데이트는 제작자의 개인 키로 **서명**되어 있습니다. 서명, 해시, DLL 이름과 버전을 확인한 뒤에만 적용하며, 하나라도 틀리면 아무것도 하지 않습니다. 시작에 2번 실패하면 자동으로 이전 버전으로 되돌립니다.
- 자동 업데이트에는 `patchers/SmoothNetUpdater.dll`이 필요합니다 (SmoothNet-Setup.exe나 Thunderstore/r2modman이면 자동으로 들어갑니다. 수동 설치라면 `BepInEx/patchers/ryuto-SmoothNet/`에 넣으세요).
- 설정 `General.AutoUpdate`(기본 ON)로 끌 수 있습니다. HUD(F8)에 버전과, 다른 사람과 버전이 다를 때 경고가 표시됩니다.

### 1.1.0
- 카트를 밀 때 내 화면에서 카트를 직접 시뮬레이션해서 즉시 반응합니다 (호스트의 판정은 바뀌지 않습니다).
- 새 설정: `LocalCartSim` (ON/OFF, OFF면 1.0.0 동작), `CartSimCorrection` (호스트 카트 쪽으로 되돌리는 강도, 기본 3.0).

---

## 🇬🇧 English

Reduces perceived lag in R.E.P.O. multiplayer:
- client-side prediction for held objects and pushed carts (including their contents);
- velocity extrapolation for objects other players move;
- latency-compensated remote player rendering;
- higher packet send rate;
- host-side cart compensation for players without the mod;
- a vanilla cart-never-sleeps physics fix;
- fewer collision GC allocations;
- a network HUD (F8).

The host stays fully authoritative. Prediction is visual only, re-anchored on every host packet, and never sent back. Install on every player for the full effect. For a Japan + Korea group, the host should pick region `jp` or `kr`.

### 1.4.0
- **Updates now apply after a single launch.** Right at startup (before mods load) SmoothNet checks for a newer release for at most 4 seconds; if there is one it is signature-verified and applied on the spot. On a slow or missing connection the game starts normally and the background download works as before. `General.AutoUpdate = false` disables the check.
- When the update was only downloaded in the background, the main menu asks "Restart now to apply it?" (only the "Restart" button relaunches the game through Steam; never shown in a lobby or in a level).
- The updater itself (SmoothNetUpdater.dll) is now published signed and swapped automatically after the game exits, so 1.2.0/1.3.x users need no reinstall.

### 1.3.1
- The startup on/off choice now uses the game's own popup so the mouse cursor is visible (mouse, keyboard and controller all work). The "don't ask again" checkbox is gone: set `General.AskOnStartup` to false in the config to stop being asked. Back/Esc counts as Off.

### 1.3.0
- **Fixed held items and doors teleporting.** Doors have no hinge joint on clients, so the old prediction pushed them to impossible poses. Hinged doors now use plain vanilla follow with no prediction at all.
- Held objects are simulated locally with the vanilla grab physics (spring + camera-relative rotation) while you are the only grabber, so they respond instantly and no longer jump on every host packet. The host stays authoritative.
- New config: `LocalHeldSim` (on/off; off = 1.2.0 behaviour) and `HeldSimCorrection` (pull toward the host's pose, default 4.0).
- **HighRateSync (still default off) is now adaptive.** It is raised only while every player's RTT stayed under 100 ms for 10 s, and drops back to vanilla 25 for at least 30 s if anyone's RTT exceeds 120 ms or rises 40 ms over its baseline for ~1 s. `SendRate` also falls back to 25 for 30 s when your own RTT climbs or the outgoing queue grows. Transitions are logged. **HighRateSync is still experimental and can raise ping on weaker connections: leave it off unless everyone has a good link.**
- F8 HUD: traffic (KB/s out/in, packets/s, outgoing queue), per-player RTT, and a counter of hard snaps (teleports) with the last one. When hosting with high ping on a region other than jp/kr, a one-time hint toast suggests changing the region (the `ForceRegion` default is unchanged).
- **Startup choice:** when the main menu first appears you are asked whether to use SmoothNet this session (on/off). **Off means truly vanilla gameplay** and other players see you as unmodded (the auto-updater keeps working). Config: `General.AskOnStartup` (default on). The F8 HUD shows the current state.
- **Automatic host region:** when you create a lobby and another Photon region has more than 50 ms lower ping than the one selected, SmoothNet hosts there and tells you (never when joining someone else's lobby; `ForceRegion` wins when set). Config: `Network.AutoBestRegion` (default on).
- More conservative defaults: `SendRate` 60 -> 30 and `HighRateValue` 40 -> 30 (~40 Hz); configs still holding the old defaults are migrated. HighRateSync is only raised after every player's RTT stayed under 100 ms for 10 s and drops back to vanilla at the first sign of trouble.

### 1.2.0 / auto-update
- New versions are **downloaded in the background and applied on the next game launch**. A toast in the menu tells you when one is ready.
- Updates are **signed** with the author's private key. The mod verifies the signature, the SHA-256, the assembly name and the version before anything is applied; if any check fails nothing happens. If the new version fails to start twice, the previous version is restored automatically.
- Auto-update needs `patchers/SmoothNetUpdater.dll` (included by SmoothNet-Setup.exe and by Thunderstore/r2modman; for a manual install put it in `BepInEx/patchers/ryuto-SmoothNet/`).
- Turn it off with `General.AutoUpdate` (default on). The HUD (F8) shows the version and warns when other players run a different one.

### 1.1.0
- When you push a cart, it is now simulated locally on your screen so it reacts instantly (the host stays authoritative).
- New config entries: `LocalCartSim` (on/off; off = 1.0.0 behaviour) and `CartSimCorrection` (strength of the pull toward the host's cart, default 3.0).
