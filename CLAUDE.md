# PastelNet 開発メモ

Windows ネイティブのネットワーク診断ツール。C# + WPF + .NET 10 (LTS)。

## この作業環境で最初に理解すべきこと

**開発コンテナは Linux で、dotnet SDK が無く、Windows 側ドライブも見えない。exe を一度も実行できない。**
コンパイルすらローカルではできないので、**GitHub Actions が唯一のコンパイラであり、唯一の実行環境**。

そのための設計:

1. **`PastelNet.Core` (net10.0) に純粋ロジックを押し出す** — ネットワークにも WPF にも触らないコード（IP 範囲パース、統計、MOS 算出、OUI 解決、レポート生成）はすべてここへ。CI の xUnit で検証できる面積を最大化する。
   原則: **テストできない部分（WPF・P/Invoke・実ネットワーク）を、テストできる純関数の薄いラッパーに追い込む。**
   例: traceroute なら「Ping を投げる部分」と「ホップ列を解析・整形する部分」を分け、後者を固定データでテストする。
2. **`--selftest`** — `PastelNet.exe --selftest` で UI を出さずに自己診断し、終了コードを返す。**XAML のエラー（リソースキーの打ち間違いなど）はコンパイルを通り抜けて実行時に落ちる**ので、MainWindow を実際に生成するこの検査が実質唯一の防波堤。新しい画面やリソースを足したら必ずここに検査を追加する。
3. **1 フェーズ = 1 まとまり = CI グリーン** — 大きな差分を積むと失敗の切り分けが不可能になる。

## 実装上の罠（調査済み。踏む前に読むこと）

### ビルド・配布

- **WPF はトリミングできない。** `PublishTrimmed=true` は NETSDK1168 でビルドエラー。`PublishAot` も不可。self-contained のサイズは削れない前提で、「軽量」は起動時間とメモリで満たす。
- **`PublishSingleFile` + `PublishReadyToRun` は起動失敗の報告がある**（dotnet/wpf#7282, #11436）。Phase 0 のマトリクスで実測して採用形を決める。
- **単一ファイルでは `Assembly.Location` が空文字を返す。** exe 横のファイルは `AppContext.BaseDirectory`、自分の exe パスは `Environment.ProcessPath` を使う。
- **`InvariantGlobalization=true`** にしているので `CultureInfo` はすべて Invariant。**日付・数値の書式は必ず明示指定**（`"yyyy/MM/dd HH:mm:ss"`）。日本語の表示自体には影響しない。
- 圧縮（`EnableCompressionInSingleFile`）はサイズを大きく削るが、圧縮アセンブリはメモリマップできず起動時展開が要るので R2R と打ち消し合う。

### 測定

- **`Ping` インスタンスは同時に 1 リクエストしか扱えない。** 進行中に再度 `SendPingAsync` すると `InvalidOperationException`。宛先ごとに 1 インスタンスを持たせ、その宛先内では逐次にする。traceroute の TTL 並列など 1 宛先で同時に複数投げる場面ではインスタンスをプールする。
- **`PingReply.RoundtripTime` は `Status != Success` のとき信用できない**（0 が返る）。RTT は必ず自前の `Stopwatch.GetTimestamp()` で測る。
- **`Task.Run` で同期版を包まない。** `Ping`/`Socket` の非同期 API は I/O 完了ポートを使うので数百同時でもスレッドを消費しないが、同期版を包むとスレッドプールが枯渇する。
- **traceroute の TTL 完全並列は欠落ホップを生む。** ICMP TTL 超過はルータ側でレート制限されるため、TTL ごとに 20〜30ms のスタガーを入れる。確実性優先の逐次モードも設定で用意する。
- **TCP ping はエフェメラルポートを食う。** 切断後 TIME_WAIT が約 4 分残るので、同時数だけでなくレートも制限する。
- **Path MTU にはブラックホールがある。** ICMP「要フラグメント」を返さないルータがあると、大きすぎる場合が `PacketTooBig` ではなく `TimedOut` になる。両者を区別して記録し、UI でも書き分ける。
- **経路変化は ECMP で誤検知しやすい。** 2 回連続で異なった場合のみ「変化」と判定する。

### Windows API

- **Wi-Fi 情報は Windows 11 24H2 以降、位置情報の同意がないと `ERROR_ACCESS_DENIED` になる。** 対象は `WlanScan` / `WlanGetAvailableNetworkList` / `WlanGetNetworkBssList` / `WlanQueryInterface`(現在の接続)。ManagedNativeWifi は `UnauthorizedAccessException` として投げる。
  - 起動時にスキャンしない。Wi-Fi 画面を開く、またはユーザーが「スキャン」を押したときに初めて呼ぶ。
  - 例外を捕捉して `ms-settings:privacy-location` への導線を出す。
- **プロファイル判定を SSID に依存させない。** 上記の理由で SSID は取れないことがある。判定は「ローカル IPv4 サブネット → デフォルトゲートウェイ IP → ゲートウェイ MAC → SSID（取れたら加点）」の順で行う。有線環境でも動くようになる。
- **`WlanScan` の再スキャン間隔は最短 5 秒、既定 10〜15 秒。** Windows の WLAN サービス自体が既定 60 秒間隔でしかスキャンしないので、短くしても新しい結果は返らない。
- **`arp -a` をパースしない。** 出力が OS の表示言語でローカライズされるため日本語環境と英語環境で壊れる。`iphlpapi.dll` の `GetIpNetTable2` を P/Invoke する（管理者権限不要）。
- 全機能を**非管理者で動作させる**方針。`app.manifest` は `asInvoker` 固定。

### UI

- **測定結果ごとに `Dispatcher.Invoke` しない。** 500 宛先 × 1Hz = 500 通知/秒で UI が溶ける。`Channel<T>` に積んで 10Hz の単一ポンプでまとめて適用する。
- `ObservableCollection` の**構造変化は宛先の追加/削除時のみ**。測定結果は既存の行 VM のプロパティ更新として流す。
- 一覧は仮想化必須（`VirtualizationMode=Recycling`、行高固定）。ソート・フィルタはティックごとに `Refresh()` しない。
- **状態は色だけで表さない。** 緑と赤は色覚多様性下で潰れる。`● 応答` / `▲ 遅延` / `✕ 不達` のように記号と文字を必ず併記し、色は補助にとどめる。
- **地色と文字色はパレット表のペアで使う。** 地色を敷くのは状態バッジだけ。数値は文字色のみで表現する。
- グラフで **RTT と損失率を 2 軸で重ねない**。存在しない相関を作り出す。分けて描く。

## 構成

```
src/PastelNet.Core/   純粋ロジック。Windows 非依存。ここを厚くする
src/PastelNet.App/    WPF 本体。Core を参照する（逆参照は禁止）
tests/                Core のユニットテスト
tools/                OUI テーブル生成などの手動スクリプト
```

## フェーズ

Phase 0 骨組みと CI ／ 1 並列 ping ／ 2 TCP ping・DNS・traceroute ／ 3 IP スキャン ／ 4 無線 LAN ／ 5 回線品質 ／ 6 レポート。
Phase 1 完了時点で「EXPing の不満を解消する」という当初目的は達成される。

## 確認の依頼先

CI で分かるのはビルド成否・ユニットテスト・起動可否まで。**見た目と実機の挙動（配色の印象、数百宛先での描画、Wi-Fi API の実際の応答）はユーザーに確認してもらう**しかない。フェーズ完了時にスクリーンショットを依頼すること。
