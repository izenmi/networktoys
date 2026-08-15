# PingWatcher 開発メモ

Windows ネイティブのネットワーク診断ツール。C# + WPF + .NET 10 (LTS)。

## この作業環境で最初に理解すべきこと

**開発コンテナは Linux で、dotnet SDK が無く、Windows 側ドライブも見えない。exe を一度も実行できない。**
コンパイルすらローカルではできないので、**GitHub Actions が唯一のコンパイラであり、唯一の実行環境**。

そのための設計:

1. **`PingWatcher.Core` (net10.0) に純粋ロジックを押し出す** — ネットワークにも WPF にも触らないコード（IP 範囲パース、統計、MOS 算出、OUI 解決、レポート生成）はすべてここへ。CI の xUnit で検証できる面積を最大化する。
   原則: **テストできない部分（WPF・P/Invoke・実ネットワーク）を、テストできる純関数の薄いラッパーに追い込む。**
   例: traceroute なら「Ping を投げる部分」と「ホップ列を解析・整形する部分」を分け、後者を固定データでテストする。
2. **`--selftest`** — `PingWatcher.exe --selftest` で自己診断し、終了コードを返す。**XAML のエラー（リソースキーの打ち間違いなど）はコンパイルを通り抜けて実行時に落ちる**ので、MainWindow を実際に生成・表示するこの検査が実質唯一の防波堤。新しい画面やリソースを足したら必ずここに検査を追加する。
   **ウィンドウは `Show()` してから `UpdateLayout()` すること。** Show せずに `Measure()` を呼んでもテキスト整形まで到達せず、フォント初期化の不具合を見逃す（InvariantGlobalization の事故で実証済み）。
3. **1 フェーズ = 1 まとまり = CI グリーン** — 大きな差分を積むと失敗の切り分けが不可能になる。

## 実装上の罠（調査済み。踏む前に読むこと）

### ビルド・配布

- **WPF はトリミングできない。** `PublishTrimmed=true` は NETSDK1168 でビルドエラー。`PublishAot` も不可。self-contained のサイズは削れない前提で、「軽量」は起動時間とメモリで満たす。
- **`PublishSingleFile` + `PublishReadyToRun` は起動失敗の報告がある**（dotnet/wpf#7282, #11436）。Phase 0 のマトリクスで実測して採用形を決める。
- **単一ファイルでは `Assembly.Location` が空文字を返す。** exe 横のファイルは `AppContext.BaseDirectory`、自分の exe パスは `Environment.ProcessPath` を使う。
- **`InvariantGlobalization` は WPF では使えない（CI で実測）。** `true` にすると ICU が同梱されず、`MS.Internal.FontCache.MajorLanguages` の型初期化に失敗して**最初の TextBlock を測った瞬間に落ちる**。厄介なのは `dotnet publish` が正常に通ることで、ウィンドウを表示して初めて死ぬ。サイズ削減のために再度有効化しないこと。
- 圧縮（`EnableCompressionInSingleFile`）はサイズを大きく削るが、圧縮アセンブリはメモリマップできず起動時展開が要るので R2R と打ち消し合う。

### 測定

- **`Ping` インスタンスは同時に 1 リクエストしか扱えない。** 進行中に再度 `SendPingAsync` すると `InvalidOperationException`。宛先ごとに 1 インスタンスを持たせ、その宛先内では逐次にする。traceroute の TTL 並列など 1 宛先で同時に複数投げる場面ではインスタンスをプールする。
- **`PingReply.RoundtripTime` は `Status != Success` のとき信用できない**（0 が返る）。RTT は必ず自前の `Stopwatch.GetTimestamp()` で測る。
- **`Task.Run` で同期版を包まない。** `Ping`/`Socket` の非同期 API は I/O 完了ポートを使うので数百同時でもスレッドを消費しないが、同期版を包むとスレッドプールが枯渇する。
- **traceroute の TTL 完全並列は欠落ホップを生む。** ICMP TTL 超過はルータ側でレート制限されるため、TTL ごとに 20〜30ms のスタガーを入れる。確実性優先の逐次モードも設定で用意する。
- **TCP ping はエフェメラルポートを食う。** FIN で行儀よく閉じると TIME_WAIT が約 4 分残り、200 宛先 × 1 秒間隔なら数分で枯渇する。採用した対策は **`LingerState = new LingerOption(true, 0)` の RST クローズ**（TIME_WAIT を残さない）。測定用の接続はデータを流さないので RST で切っても相手は困らない。ソケットを触る箇所を増やすときは必ずこれを踏襲する。
- **Path MTU にはブラックホールがある。** ICMP「要フラグメント」を返さないルータがあると、大きすぎる場合が `PacketTooBig` ではなく `TimedOut` になる。両者を区別して記録し、UI でも書き分ける。
- **経路変化は ECMP で誤検知しやすい。** 2 回連続で異なった場合のみ「変化」と判定する。

### Windows API

- **Wi-Fi 情報は Windows 11 24H2 以降、位置情報の同意がないと `ERROR_ACCESS_DENIED` になる。** 対象は `WlanScan` / `WlanGetAvailableNetworkList` / `WlanGetNetworkBssList` / `WlanQueryInterface`(現在の接続)。ManagedNativeWifi は `UnauthorizedAccessException` として投げる。
  - 起動時にスキャンしない。Wi-Fi 画面を開く、またはユーザーが「スキャン」を押したときに初めて呼ぶ。
  - 例外を捕捉して `ms-settings:privacy-location` への導線を出す。
- **接続環境の判定を SSID に依存させない。** 上記の理由で SSID は取れないことがある。有線でも成り立つよう、ローカル IPv4 サブネットとデフォルトゲートウェイを先に見る。
- **`WlanScan` の再スキャン間隔は最短 5 秒、既定 10〜15 秒。** Windows の WLAN サービス自体が既定 60 秒間隔でしかスキャンしないので、短くしても新しい結果は返らない。
- **`arp -a` をパースしない。** 出力が OS の表示言語でローカライズされるため日本語環境と英語環境で壊れる。`iphlpapi.dll` の `GetIpNetTable2` を P/Invoke する（管理者権限不要）。同じ理由で **`netstat` もパースしない**。接続一覧は `GetExtendedTcpTable` / `GetExtendedUdpTable`（非管理者で全プロセスの PID まで取れる）。この 4 種の行テーブルは**配列が先頭ヘッダの +4 から始まる**。`GetIpNetTable2` の +8 を写経すると 1 行目から壊れる。
- 全機能を**非管理者で動作させる**方針。`app.manifest` は `asInvoker` 固定。**唯一の例外は接続タブの通信量（B/秒）**で、ETW のカーネルネットワークイベントが管理者限定のため、非管理者では列を「—」にして案内を出す縮退動作になる（接続一覧そのものは非管理者で動く）。昇格の強制はしないが、案内行に「管理者として再起動」ボタンを置き、望む人だけ `runas` で明示的に昇格できる（確認ダイアログ付き。UAC 拒否 1223 は握って続行）。
- **ETW（通信量表示）の罠。** ①セッション名は固定（`PingWatcher-KernelNet`）なので、**開始前に必ず `ControlTrace` STOP で前回の残骸を掃除**する（クラッシュ後はセッションが OS に残り続ける）。②ネイティブに渡すコールバックの**デリゲートはフィールドに保持**しないと GC に回収されて即死する。③`ProcessTrace` はセッションが止まるまで返らないので専用の背景スレッドで回し、停止は `CloseTrace` → `ControlTrace` STOP → `Join(1秒)`。④TraceEvent（NuGet）は不採用 — 必要なのは 1 プロバイダ・8 イベント ID の固定レイアウトだけで、ローカルで検証できないバージョン依存の方がリスクが大きい。⑤`EVENT_RECORD` のレイアウト誤りは catch 不能の AccessViolation でプロセスごと落ちる。防波堤は自己診断の ETW 検査（CI の Windows ランナーは管理者なので実経路が通る）。非管理者の縮退表示だけは CI で踏めないため実機確認。

### UI

- **`Style` の `Setter` の中に、イベントハンドラを持つ要素を置かない。** `<Setter Property="ContextMenu">` の下に `Click="..."` 付きの `MenuItem` を書くと、**ビルドは通り、起動した瞬間に `XamlParseException: Set connectionId threw an exception` で落ちる**（`MenuItem` を `Grid` にキャストできない、のように無関係な型が出る）。リソースは後から展開されるのに、結線の番号は文書順で振られるためずれる。`ControlTemplate` / `DataTemplate` の中は独自の結線を持つので安全。**メニューはテンプレートの中の要素に付ける**こと。
- **既定スタイルを持つコントロールは、色を継承してくれない。** `ListBox` / `ListView` の既定スタイルは `Foreground` にシステム色（＝黒）を入れるため、暗い配色にしても行の中の `TextBlock` だけ黒く残る（`ItemsControl` は既定スタイルを持たないので継承される）。暗黙スタイルで上書きすること。**キー付きスタイルは暗黙スタイルを継承しない**ので、`BasedOn="{StaticResource {x:Type ListBox}}"` を明示する。`ContextMenu` / `MenuItem` / `ToolTip` も同様にシステム色で描かれる。
- **`TargetListBox` スタイルは Ping 用の `ItemTemplate` を既定で当ててくる。** 型別の暗黙 DataTemplate で出し分ける一覧(接続タブ)にこのスタイルを使うときは、`ItemTemplate="{x:Null}"` で明示的に外すこと。外し忘れるとスタイル側のテンプレートが暗黙テンプレートより優先され、**全行が Ping の「◌ 待機」バッジで描かれる**(バインドは静かに失敗するのでビルドも selftest も通ってしまう。実機で発覚した)。
- **既定のテンプレートが敷いている装飾は、色を指定しても消えない。** `ContextMenu` は左端にアイコン用の白い帯を持っており、`Background` を変えても残る。項目を自前で描くならテンプレートごと差し替えること。**メニュー内の区切り線は `{x:Static MenuItem.SeparatorStyleKey}` という専用のキーで引かれる**ので、`Separator` の暗黙スタイルは当たらない。
- **`ComboBox` の閉じているときの表示に `DisplayMemberPath` は効かない。** 選択中の項目は `SelectionBoxItemTemplate` 経由で描かれるため、**型名がそのまま出る**。ドロップダウンを開いたときだけ正しく見えるので気づきにくい。`ItemTemplate` を明示すること。
- **測定結果ごとに `Dispatcher.Invoke` しない。** 500 宛先 × 1Hz = 500 通知/秒で UI が溶ける。`Channel<T>` に積んで 10Hz の単一ポンプでまとめて適用する。
- `ObservableCollection` の**構造変化は宛先の追加/削除時のみ**。測定結果は既存の行 VM のプロパティ更新として流す。
- 一覧は仮想化必須（`VirtualizationMode=Recycling`、行高固定）。ソート・フィルタはティックごとに `Refresh()` しない。**仮想化の指定は ListBox 側の添付プロパティに書く。** `VirtualizingStackPanel` はパネル要素に書いた属性ではなく ItemsControl 側を読むため、`<VirtualizingStackPanel VirtualizationMode="Recycling" />` は無言で無視される（実際にやらかした）。折り返し（タイル敷き詰め）の一覧は WrapPanel では仮想化できない。かつて自作の仮想化パネルで実装したが、**「1 行に複数エントリを並べない」というユーザー指示で 2026-08-15 に一望表示ごと削除した**。再提案しないこと。
- **配色は明暗 2 つある。色は必ず `DynamicResource` で引く。** `ThemeManager` が `Palette.xaml` / `Palette.Dark.xaml` を丸ごと差し替えるため、`StaticResource` で引いた色は切り替えても古いまま残る。**コードから `TryFindResource` した結果を static にキャッシュしない**（Sparkline で実際にやって切替に追随しない不具合になった。依存プロパティで受け取る形に直してある）。色を足すときは**両方のパレットに同じキーを**足すこと（自己診断が突き合わせている）。色に依らない寸法・書体は `Tokens.xaml`。
- **文字と地のコントラストは自己診断が検算する。** 淡くしたい気持ちと読めることは必ず衝突するので、目視で決めない。`Core/Design/ColorMath.cs` の WCAG 比で 4.5 以上を満たすこと。
- **`DropShadowEffect` を一覧まわりで使わない。** 中身が毎秒書き換わるので、効果を挟むと中間サーフェス経由の再描画になる。立体感は「上辺だけ明るいグラデーションの枠」で出す（`Brush.Card.Edge`）。
- **状態は色だけで表さない。** 緑と赤は色覚多様性下で潰れる。`● 応答` / `✕ 不達` / `⊘ 拒否` のように記号と文字を必ず併記し、色は補助にとどめる。（「▲ 遅延」の状態表示は 2026-08-15 にユーザー指示で廃止。再提案しない）
- **地色と文字色はパレット表のペアで使う。** 地色を敷くのは状態バッジだけ。数値は文字色のみで表現する。
- グラフで **RTT と損失率を 2 軸で重ねない**。存在しない相関を作り出す。分けて描く。

## アイコン

元は `src/PingWatcher.App/Resources/AppIcon.svg`（見張るふくろう、瞳が ping の波紋）。
`tools/icon/build_icon.sh` を**手で実行**して 16〜256px の 9 サイズを焼き、`.ico` にまとめる
（CI に入れると ImageMagick を毎回入れることになるので入れていない）。

- **24px 以下は `AppIcon.Small.svg` から焼く。** 通常版を縮めると瞳の細い輪が潰れて全体がぼやける。
  小さいところは輪を捨て、目・耳・くちばしを大きくした別の絵にしてある。1 枚で全サイズを賄わない。
- 埋め込みは csproj の `ApplicationIcon`。**指定漏れやパスの誤りはビルドを通る**ので、
  自己診断が `shell32` の `ExtractIconEx` で exe から数えて確かめている
  （`System.Drawing` は `UseWPF` だけでは参照されないため使えない）。

## 設定の保存先

**exe と同じフォルダ**。`AppData.PathOf()` を通すこと。持ち出して使うための作り。
書き込めない場所（Program Files など）に置かれたときだけ `%APPDATA%\PingWatcher\` へ逃がす。
場所は**実際に 1 ファイル書いて消して**決めている。属性だけでは判定できないため。
旧名 `PastelNet` のフォルダがあれば初回に一度だけ複製して引き継ぐ（移動ではない）。

**設定は `settings.json` の 1 ファイルに統合**（2026-08-16、ユーザー指示）。配色・列幅・Ping/TCP の宛先リストと測定既定値がすべて入る。入り口は `App/Settings.cs`（起動時に `Settings.Initialize()` — **`ThemeManager.Initialize()` より前**でないと配色が読めない）。旧 4 ファイル（targets.json / tcp-targets.json / theme.txt / columns.txt）は初回起動時に取り込み、統合ファイルを書けたときだけ削除する。新しい設定を足すときは `AppSettingsDocument` にプロパティを足し、`Settings.Current` を書き換えて `Settings.Save()` を呼ぶ（ファイルを増やさない）。

## 構成

```
src/PingWatcher.Core/   純粋ロジック。Windows 非依存。ここを厚くする
src/PingWatcher.App/    WPF 本体。Core を参照する（逆参照は禁止）
tests/                Core のユニットテスト
tools/                OUI テーブル生成などの手動スクリプト
```

## フェーズ

Phase 0 骨組みと CI ／ 1 並列 ping ／ 2 TCP ping・DNS・traceroute ／ 3 IP スキャン ／ 4 無線 LAN ／ 5 回線品質 ／ 6 レポート。
Phase 1 完了時点で「EXPing の不満を解消する」という当初目的は達成される。

## 確認の依頼先

CI で分かるのはビルド成否・ユニットテスト・起動可否まで。**見た目と実機の挙動（配色の印象、数百宛先での描画、Wi-Fi API の実際の応答）はユーザーに確認してもらう**しかない。フェーズ完了時にスクリーンショットを依頼すること。
