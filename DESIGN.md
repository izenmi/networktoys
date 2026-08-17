# NetworkToys — Windowsネイティブ ネットワーク診断ツール

## Context

現場のネットワーク調査で使われている **EXPing** には決定的な不満がある: pingが1宛先ずつ逐次実行されるため、宛先が増えるほど結果が出るまで待たされる。これを **全宛先同時並列** にするのが本ツールの第一の目的。

あわせて、現場で別々のツールを立ち上げ直す手間(TCP疎通確認、DNS、traceroute、Wi-Fi電波状況)を1つのアプリに統合し、測定結果をそのまま証跡として残せるようにする。見た目はかわいいパステルカラーで、EXPingの素っ気なさとは対照的な、長時間眺めても疲れないUIを目指す。

完全な新規プロジェクト。このワークスペースにC#/.NETの資産もWindows向けCIも存在しないため、ゼロからの立ち上げになる。

## 確定した前提

| 項目 | 決定 | 理由 |
|---|---|---|
| 技術 | C# + WPF + **.NET 10 (LTS)** | Wi-Fi/ICMP/TCP/DNSがすべて標準API・P-Invokeで素直に叩ける。単一プロセスで追加ランタイム不要 |
| 配布 | self-contained 単一exe + ランタイム別途版 | 起動0.3〜0.6秒、メモリ60〜90MB目標 |
| ビルド | GitHub Actions `windows-latest` | 開発環境はLinux devcontainerでdotnet SDK未導入 |
| リポジトリ | `izenmi/networktoys` (public) | アプリ名もリポジトリ名も NetworkToys（旧 PingWatcher、その前は PastelNet）。旧 URL は GitHub がリダイレクトする |

**.NET 10 を選ぶ理由**: .NET 9 は2026年11月10日にサポート終了(3ヶ月後)。.NET 10 は LTS で2028年11月まで。

**ファイルサイズについて**: WPF はトリミング(`PublishTrimmed`)も NativeAOT も使えないため、self-contained の単一exeは **150MB 前後**になる。圧縮すれば 60〜70MB まで縮むが、圧縮アセンブリはメモリマップできず起動時展開が要るので ReadyToRun の効果と打ち消し合う。
したがって要件の「軽量」は**ファイルサイズではなく起動時間・メモリ・CPU で満たす**方針とし、サイズを気にする場合のために .NET ランタイム別途インストール版(数MB)も併せて配布する。どの構成を既定にするかは Phase 0 の CI 実測で決める。

2026-08-15 の再検討でも方針は不変: 単一ファイル圧縮(−66MB)はメモリ倍増・初回起動 5 秒、フォントのサブセット縮小(−3MB)は字面の一貫性とのトレードで、いずれも見送り。サイズが要る利用者は軽量版へ誘導する。

### 最大の制約 — ローカルで実行検証できない

開発コンテナはLinuxで、Windows側ドライブも見えず dotnet SDK も無い。**私はexeを一度も実行できない**。対策を設計に組み込む:

1. **ロジックをWindows非依存プロジェクトに分離** — IP範囲パース、MOS計算、OUI解決、レポート生成など「ネットワークにもUIにも触らない純粋関数」を `NetworkToys.Core` (net10.0) に切り出し、xUnitでCI実行する。バグの大半をここで潰す。
2. **CIスモークテスト** — GitHub-hosted の windows-latest はグラフィカルセッションを持つので、publish した exe を実際に起動し、5秒後にプロセスが生きていることを確認して終了させる。さらに `--selftest` 引数で全サービスを初期化して即終了するモードを作り、終了コードで判定する。
3. **こまめにCIへ通す** — 後述のフェーズごとに必ずグリーンにしてから次へ進む。

---

## スコープ

### 必須機能
1. 宛先リストの編集(ホスト名/IP/コメント)
2. **複数宛先への同時並列 ping** ← 最重要
3. パステルカラーのUI
4. TCP ping(任意ポートへのconnect所要時間)
5. 無線LAN情報の取得(SSID/BSSID/RSSI/リンク速度/チャンネル/認証方式)
6. テスト結果の保存
7. DNSテスト
8. traceroute
9. わかりやすい結果表示
10. 軽量・高速起動・低リソース

### 採用した追加機能
- **IPスキャン(範囲指定)** — CIDR / 開始-終了IP / 複数行指定
- **ネットワーク自動探索** — サブネット並列pingスイープ、ARPからMAC・ベンダー名(OUI)解決、簡易ポートスキャン
- **回線品質の計測** — ジッタ/パケットロスからのMOS値、traceroute定期実行による経路変化検知、Path MTU探索、Wi-Fiチャンネル混雑ビュー
- **現場レポート出力** — グラフ入りHTML/CSVエクスポート、SSID/IPセグメント検知による宛先セット自動切替(プロファイル)

### 採用しないもの
タスクトレイ常駐、トースト通知、障害タイムライン。
※ レイテンシのスパークライン程度の可視化は「わかりやすい表示」の一部として含める。

---

## プロジェクト構成

```
networktoys/
├── NetworkToys.sln
├── src/
│   ├── NetworkToys.Core/            # net10.0 — Windows非依存・テスト対象
│   │   ├── Addressing/            # IpRangeParser, Cidr, IpMath
│   │   ├── Quality/               # JitterCalculator, MosScore, LossStats
│   │   ├── Oui/                   # OuiLookup + oui.tsv.gz(埋め込みリソース)
│   │   ├── Reporting/             # HtmlReportBuilder, CsvWriter, SvgSparkline
│   │   └── Models/                # PingSample, Target, ScanResult, Profile …
│   └── NetworkToys.App/             # net10.0-windows — WPF本体
│       ├── App.xaml(.cs)          # --selftest 引数の処理もここ
│       ├── Views/                 # MainWindow + 各タブのView
│       ├── ViewModels/
│       ├── Services/              # 実際にネットワークを叩く層
│       ├── Interop/               # NativeMethods(iphlpapi)
│       └── Resources/             # Palette.xaml, Controls.xaml, Icons.xaml
├── tests/
│   └── NetworkToys.Core.Tests/      # xUnit — CIで必ず実行
└── .github/workflows/build.yml
```

`NetworkToys.App` は `NetworkToys.Core` を参照する。逆参照は禁止(Coreがテスト可能であり続けるため)。

---

## 依存パッケージ(最小限に絞る)

| パッケージ | 用途 | 備考 |
|---|---|---|
| `ManagedNativeWifi` **3.0.2** | Native Wifi API のラッパ | **依存パッケージなし**。これ以外の選択肢は自前P/Invokeになる |
| `DnsClient` **1.8.0** | 任意レコードのDNS照会 | netstandard2.0、アクティブメンテ。A/AAAA/CNAME/MX/NS/TXT/SOA/SRV/PTR 等に対応 |
| `FxSsh` **1.4.0** | SFTPサーバのSSH層 | MIT / net8.0。SSHの自前実装は非現実的なのでこれだけ依存を許容。SFTPサブシステム実装はライブラリに含まれず`Vendor/FxSsh/SftpService.cs`に取り込み |
| `xunit` (テストのみ) | Coreのユニットテスト | |

グラフ描画・JSON・HTTP・SQLiteのライブラリは**入れない**(標準機能と自前実装で足りる)。バージョンは実装時に最新安定版を再確認する。

**MVVM ライブラリも入れない**(Phase 1 で判断)。必要なのは `ObservableObject` と `RelayCommand` だけで、合わせて40行ほど。ローカルでビルドを確認できない状況で、ソースジェネレータのバージョン依存を持ち込む方がリスクが大きい。

---

## 各機能の実装方式

### ICMP ping — 並列実行の設計(本ツールの核)

`System.Net.NetworkInformation.Ping` を使う。Windowsでは内部的に `IcmpSendEcho` を呼ぶため **管理者権限は不要**。

重要な設計判断: **「全宛先を1ラウンドずつ Task.WhenAll で回す」のではなく、宛先ごとに独立した周期ループを持たせる。**
ラウンド同期方式だと、タイムアウトする1宛先が他の全宛先を待たせてしまい、EXPingと同じ待たされ感が残る。宛先ごとに `PeriodicTimer` で独立して回せば、死んでいるホストが他に一切影響しない。

- **`Ping` インスタンスは同時に1リクエストしか扱えない**(進行中に再度 `SendPingAsync` すると `InvalidOperationException`)。宛先ごとに1インスタンスを持たせ、その宛先内では逐次にする。traceroute のように1宛先で同時に複数投げる場面ではインスタンスをプールする
- **RTT は自前の `Stopwatch.GetTimestamp()` で測る。** `PingReply.RoundtripTime` は `Status != Success` のとき 0 が返り信用できない
- 同時実行数は `SemaphoreSlim` で制限(既定64、設定で変更可)。数百宛先でもスレッドプールを枯渇させない
- **`Task.Run` で同期版を包まない。** 非同期 API は I/O 完了ポートを使うので数百同時でもスレッドを消費しないが、同期版を包むとスレッドプールが枯渇する
- 各宛先の履歴は**固定長リングバッファ**(既定300サンプル)。サンプルはクラスではなく構造体で持ち GC 圧を避ける
- ホスト名は起動時に一度だけ解決してIPをキャッシュ。毎回DNSを引かない(EXPingより速い理由のひとつ)

### UIへの反映(ここを外すと重くなる)

- 測定スレッドから直接 `ObservableCollection` を触らない
- 結果は `Channel` に積み、**UI側で100ms間隔(10Hz)にまとめて反映**(1宛先1更新だと数百宛先で描画が破綻する)。測定が1秒間隔なので10Hzで十分足りる
- 一覧は `DataGrid` ではなく `ItemsControl` + `VirtualizingStackPanel`(`IsVirtualizing=True`, `VirtualizationMode=Recycling`)
- スパークラインは `Polyline` ではなく `DrawingVisual` への直接描画(数百個並べても軽い)

### TCP ping

`Socket.ConnectAsync` + `Stopwatch`、`CancellationTokenSource` でタイムアウト。
**結果を3状態で区別して表示する**: `Open`(接続成功) / `Refused`(RSTが返る=ホストは生きているがポートは閉) / `Timeout`(無応答)。この区別は現場の切り分けで効くので、色も分ける。

**ICMPがブロックされる環境ではTCP pingが主役になる**ので、UI上でICMPと対等に扱う。宛先ごとにICMP/TCPを選べるようにし、ICMPが全滅したときは「TCPで試しますか」を提示する。
なお TCP は**一時ポートを食う**(切断後 TIME_WAIT が約4分残る)。同時接続数だけでなく**レート**も制限する。

### DNS

`System.Net.Dns` は A/AAAA/PTR しか引けない。任意レコードには **DnsClient.NET** (NuGet, MIT) を使う。
DnsQuery_W の P/Invoke でも実装できるが、DnsClient.NET を推す理由は **問い合わせ先DNSサーバを指定できる**こと。現場では「社内DNSでは引けるが外部DNSでは引けない」といった切り分けが必要になる。応答時間の計測も込みで取れる。

- 対応レコード: A / AAAA / CNAME / MX / NS / TXT / SOA / SRV / PTR
- サーバ指定: システム既定 / 手入力 / よく使うパブリックDNS(8.8.8.8, 1.1.1.1)のプリセット
- 同じ名前を複数サーバに同時に投げて**結果を横並び比較**できるようにする(これはEXPingにない価値)

### traceroute

`Ping` + `PingOptions { Ttl = n }` で実装。`IPStatus.TtlExpired` のときの応答元が中継ルータ。
ここも**TTL 1〜30 を並列に投げる**。逐次だと30ホップ分の待ち時間が積み上がる。

ただし**完全同時に投げると欠落ホップが増える**。ICMP の TTL 超過応答は多くのルータでレート制限されるため、**TTL ごとに 20〜30ms のスタガーを入れた並列**を既定とし、確実性を優先する「逐次モード」も設定で用意する。
逆引き(PTR)は経路表示をブロックしないよう後追いで非同期解決し、解決でき次第UIに差し込む。

### Wi-Fi

**ManagedNativeWifi 3.0.2**(依存なし、Native Wifi APIのマネージドラッパ)を使用。

**⚠ Windows 11 24H2 以降は位置情報の同意が必要。** 同意がないと `WlanScan` / `WlanGetAvailableNetworkList` / `WlanGetNetworkBssList` / `WlanQueryInterface`(現在の接続) が `ERROR_ACCESS_DENIED` を返し、ManagedNativeWifi は `UnauthorizedAccessException` を投げる。対策:

- **起動時にスキャンしない。** 無線タブを開く、またはユーザーが「スキャン」を押したときに初めて呼ぶ(同意を求めるタイミングとして自然で、許可率も上がる)
- 例外を捕捉して「設定 > プライバシーとセキュリティ > 位置情報 で許可してください」の案内と `ms-settings:privacy-location` を開くボタンを出す

- 接続中: SSID / BSSID / シグナル品質(%) / RSSI(dBm) / チャンネル / 帯域 / 認証・暗号方式 / 送受信リンク速度
- **取れないもの**: ノイズフロアと SNR は wlanapi が公開しておらず取得できない。チャンネル幅(20/40/80/160MHz)もビーコンの IE を自前解析しないと分からないため、初版はチャンネル番号のみとする。Rx/Tx レートは**ネゴシエート済みリンク速度**でありスループットではない旨をUIに明記する
- 周辺AP: `ScanNetworksAsync()`。**Windowsにはスキャン頻度の制限がある**ため、自動更新は最短でも10秒間隔、既定30秒とする(Windows の WLAN サービス自体が既定60秒間隔でしかスキャンしないので、短くしても新しい結果は返らない)
- チャンネル混雑ビュー: 2.4GHz/5GHz/6GHz帯ごとに、チャンネル軸 × RSSI の山型グラフを重ねて描画。自分のAPを濃色、他をパステルの半透明で
- RSSI時系列: 接続中APのRSSIを1秒間隔でスパークライン表示(電波の弱い場所を歩いて特定する用途)

### ARP / OUIベンダー解決

- ARPテーブルは `arp -a` のパースではなく **iphlpapi.dll の `GetIpNetTable2`** を P/Invoke。ロケール非依存で堅牢
- OUIは IEEE の MA-L 登録簿を前処理して `先頭3バイト(hex) → ベンダー名` の最小TSVにし、**gzip して埋め込みリソース**化(生4MB → 約400KB)。CIでのネット取得は不安定なので**前処理済みファイルをリポジトリにコミット**する
- 更新用に、TSVを再生成する小さなスクリプトを `tools/` に置く(手動実行)

### IPスキャン(範囲指定)

入力欄は1つで、複数の書式を受け付ける(パーサは `NetworkToys.Core` に置きテストする):

```
192.168.1.0/24              CIDR
192.168.1.1-192.168.1.100   開始-終了
192.168.1.1-100             最終オクテットのみの短縮
192.168.1.10                単体
10.0.0.0/24, 10.0.1.0/24    カンマ区切り(複数行入力も可)
```

- 実行前に**対象ホスト数を表示**して確認させる(`/16` を誤爆すると65534件になる)
- 上限を設ける(既定4096件、超える場合は警告して続行可否を問う)
- 並列ICMP + 応答があったホストにARP照会 → MAC/ベンダー名、逆引き、任意でポートスキャン
- 現在のPCのIPとサブネットマスクから**既定値を自動入力**しておく

### 簡易ポートスキャン

- 既定は「よく使う22ポート」(22/80/443/445/3389/…)のプリセット。全65535は既定にしない
- 同時接続数を制限(既定256)。無制限だとWindowsの一時ポートを食い潰す
- **ウイルス対策ソフト・EDRに検知される可能性がある**ため、UIに注意書きを出し、機能自体を設定でOFFにできるようにする

### 回線品質

- **ジッタ**: RFC 3550 の移動平均方式 `J += (|D| - J) / 16`
- **パケットロス率**: 直近N回のウィンドウで算出
- **MOS値**: E-model(ITU-T G.107)の簡易版。R値 → MOS に変換し、5段階を色で表示。「遅延・ジッタ・ロスから見た通話品質の目安」として提示する(あくまで推定値である旨をUIに明記)
- **Path MTU探索**: `DontFragment=true` でペイロード長を二分探索(548〜1472バイト、11回程度で収束)。MTU = ペイロード + ICMPヘッダ8 + IPヘッダ20。
  **ブラックホールに注意**: ICMP「要フラグメント」を返さないルータがあると、大きすぎる場合が `PacketTooBig` ではなく `TimedOut` になる。両者を区別して記録し、UIでも「境界を検出(ICMP応答あり)」と「タイムアウトから推定(ブラックホールの可能性)」を書き分ける
- **経路変化検知**: tracerouteを定期実行し、前回のホップ列と差分を取る。変化したらその行をハイライトし、変化履歴を残す。
  **ECMPによる経路揺れで誤検知しやすい**ので、2回連続で異なった場合のみ「変化」と判定する

### 永続化

**SQLiteは入れない**(軽量要件を優先)。

- 宛先リスト / 設定 / プロファイル: JSON。`System.Text.Json` の **source generator** を使いリフレクションを回避(起動速度に効く)
- 測定結果: セッション単位の **JSONL**(1行1サンプル)。追記のみなので軽く、途中でクラッシュしても壊れない
- 保存先: **exe と同じフォルダ**。設定は `settings.json` に統合(2026-08-16。旧 `targets.json`/`tcp-targets.json`/`theme.txt`/`columns.txt` は初回起動時に取り込んで片付ける)。作業記録は `sessions\`。書き込めない場所に置かれたときだけ `%APPDATA%\NetworkToys\` へ逃がす
- 古いセッションは既定30日で自動削除(設定可)

### レポート出力

外部ライブラリを使わず**自前でHTMLを生成**し、グラフは**インラインSVG**で描く(この方式は既存プロジェクトの「依存ゼロ自作SSG」と同じ流儀)。

- 内容: 測定日時 / 実施者メモ / 接続環境(SSID・IP・GW・DNS) / 宛先ごとの成否・RTT統計・スパークライン / traceroute結果 / DNS結果 / スキャン結果一覧
- 1ファイル完結(画像もCSSも埋め込み)なのでメール添付でそのまま送れる
- CSVも同時出力(Excelで加工したい人向け。BOM付きUTF-8)

### プロファイル(接続環境による自動切替) — 取りやめ

> **実装したが 2026-08-15 に削除した。** 現場ごとに宛先を覚えさせるより、
> 宛先リストをテキストとして手元で管理するほうが早い、というのが実際に使った結論。
> 以下は当時の設計。再提案しないこと。

接続環境を条件に、宛先リストのセットを自動で切り替える。
例: `SSID=office-wifi → 社内サーバ群`、`192.168.10.0/24 → A社現場`。
検知したら**自動で切り替えず、まず画面上部に「A社現場のプロファイルに切り替えますか?」と控えめに提案する**(勝手に切り替わると事故になる)。

**判定キーをSSIDに依存させない。** 上記のとおり位置情報が未許可だとSSID自体が取れない。次の優先順で判定する:

1. ローカル IPv4 サブネット(`NetworkInterface` から取得。権限不要)
2. デフォルトゲートウェイの IP(同上)
3. ゲートウェイの MAC(ARPから。同じサブネットが複数拠点にある場合の識別に効く)
4. SSID(取得できた場合のみ加点)

これで有線環境でも位置情報未許可の環境でもプロファイル切替が成立する。

---

## UI構成

### 画面

上部にタブ(またはサイドのアイコンバー)、下部に共通ステータスバー(現在の接続環境: SSID / IP / GW / DNS)。

| タブ | 内容 |
|---|---|
| Ping | 並列 ping。開始 / 停止と宛先リストの編集もこの画面。既定画面 |
| Wi-Fi | 接続中AP詳細 / RSSI時系列 / 周辺APのチャンネル混雑グラフ |
| 差分比較 | 機器の show 出力を作業前後で突き合わせる |
| WFP | Windows Filtering Platform が落とした通信の一覧(管理者権限) |
| IP設定 | IPv4 設定の切替 + プロキシ設定 |
| 業務確認 | 業務確認試験(プロキシを切り替えて回す。ひな型は自作できる) |
| **その他　18 ▾** | 調べる(TCP Ping / Traceroute / スキャン / DNS / 通信状況 / サブネット計算)<br>受ける(FTP / TFTP / SFTP / syslog / SNMP Trap)<br>NW機器(ログ採取 / showコマンド整形 / Meraki / ファイル転送 / SNMP Get / Cisco ACI / Cisco WLC) |

太字はまとめたタブ。**単独のタブを左、まとめたタブを右**に並べる。まとめたタブは
見出しに**中身の件数と `▾`** を出し(自己診断が実際の本数と突き合わせる)、
**押すと分類ごとに並んだメニューが降りてくる**(`MainWindow.OnMainTabsMouseDown` が
内側の TabControl から組み立てる。分類は各 `TabItem` の `Tag`)。

**帯には「いま開いているもの」だけを出す。** 16 枚を素で並べると名前が読めない。
`ItemContainerStyle` に「選ばれていなければ `Collapsed`」を当てる —
`TabControl.Resources` に暗黙スタイルとして置くと、**Meraki の中のサブタブまで
巻き添えで隠れる**ので必ず `ItemContainerStyle` 側に置くこと。

記録の画面は持たない。書き出しはファイルメニューから(2026-08-16 にタブを畳んだ)。
`RelayCommand` は `CommandManager` に乗っていないので、**メニューを開くたびに
`RefreshSaveCommands()` を呼ぶ**こと。呼ばないと起動直後の判定のまま押せなくなる。

**どのタブも先頭に 1 行の説明と ⓘ(ホバーで詳しい説明)を置く。** タブ名だけでは
何の画面か伝わらない(WFP がまさにそれだった)。足し忘れは自己診断が捕まえる。

中身は TabControl のままなので、**束ねたタブの中身は親を開くまで動き出さない**という
決まりはそのまま効く — ただし **`TabItem` の中身は視覚ツリーで `TabItem` の下に無い**
(親 `TabControl` の `ContentPresenter` の下に置かれる)ので、先祖をたどる判定
(`MainWindow.IsShowing` / `Show`)は**論理ツリー**で行うこと。


### パステル配色(`Resources/Palette.xaml` に定義)

**重要な使い分け**: パステルは **UIの地色(バッジ・タブ・カード)専用**で、**グラフの線の色には使わない**。淡い色を6本の折れ線に使うと、通常色覚でも隣り合う系列が識別できず、色覚多様性下ではほぼ確実に潰れる。複数系列のグラフ(Phase 5以降)では、同じ色相を濃く踏んだ別系統の色を使う。単色のスパークライン(Phase 1)は `Brush.Chart.Line` 1色で足りる。

ライト:
```
背景          #FDFBF7   温かみのあるオフホワイト
サーフェス     #FFFFFF
罫線          #EFE9F4
文字          #4A4458   真っ黒を避けた紫みのダークグレー
文字(淡)      #8B839B

成功/応答あり  #A8E6CF (地) / #2E7D5B (文字)   ミント
警告/遅延      #FFE0B2 (地) / #A9631B (文字)   ピーチ
エラー/不達    #FFC9C6 (地) / #B8433C (文字)   ローズ
情報/実行中    #B5E2FA (地) / #1F6E96 (文字)   スカイ
アクセント/選択 #C7CEEA (地) / #4A54A0 (文字)   ラベンダー
```

ダーク(背景 `#2B2733` / サーフェス `#363042`)では、同じ色相のまま彩度を落として明度を上げた版を用意する。

**可読性の担保**: パステル地に淡色文字を置くと読めなくなるので、**地色と文字色は必ず上表のペアで使う**。RTT値などの数値は地色を敷かず文字色のみで表現し、状態バッジだけ地色を使う。

### 「わかりやすい表示」の具体
- 状態は色だけでなく**記号でも区別**(● 応答 / ▲ 遅延 / ✕ 不達)。色覚特性に依存しない
- RTTは数値とスパークラインを併置。数字だけだと傾向が見えない
- 数百行でも一覧性を保つため、行の高さを詰めた**コンパクト表示モード**を用意

---

## ビルド設定(csproj)

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
<Nullable>enable</Nullable>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PublishSingleFile>true</PublishSingleFile>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<PublishTrimmed>false</PublishTrimmed>       <!-- WPFは非対応。有効にすると起動時に落ちる -->
<InvariantGlobalization>false</InvariantGlobalization>  <!-- true は WPF では使えない。下の実測結果を参照 -->
<ServerGarbageCollection>false</ServerGarbageCollection>
<TieredPGO>true</TieredPGO>
```

### Phase 0 の実測結果(確定)

GitHub Actions の windows-latest 上で 5 構成を実測した。実機はこれより速いはずなので絶対値は参考値。

| 構成 | GUI起動 | メモリ | 配布サイズ | 採否 |
|---|---|---|---|---|
| **単一exe + R2R** | 663 ms | 93.5 MB | 124.8 MB | **採用(既定)** |
| 単一exe のみ | 639 ms | 93.3 MB | 124.7 MB | R2R と差がないので R2R 有効のままにする |
| R2R のみ(フォルダ配布) | 641 ms | 91.1 MB | 131.2 MB | 単一exeの利点がない |
| 単一exe + 圧縮 | 720 ms | **182.7 MB** | 58.6 MB | 不採用。メモリ2倍・初回起動5秒 |
| **ランタイム別途** | 683 ms | 91.2 MB | **0.2 MB** | **併せて配布**(サイズ重視の選択肢) |

**`InvariantGlobalization=true` は使えない。** ICU を落とすと `MS.Internal.FontCache.MajorLanguages` の型初期化に失敗し、最初の TextBlock を測った瞬間に落ちる。厄介なことに `dotnet publish` は正常に通り、ウィンドウを表示するまで気づけない。しかも self-contained ではサイズも変わらなかった(ICU ファイル自体は同梱され、設定だけが無効化される)ため、得るものが何もない。

**注意点**

- `PublishTrimmed` は **WPFでは使えない**。複数の既知不具合あり([dotnet/wpf#4216](https://github.com/dotnet/wpf/issues/4216) 他)。サイズ削減は諦める
- `PublishSingleFile` + `PublishReadyToRun` の同時指定で起動失敗する報告がある([dotnet/wpf#7282](https://github.com/dotnet/wpf/issues/7282), [dotnet/wpf#11436](https://github.com/dotnet/wpf/issues/11436))。**Phase 0 のCIで「両方ON」「R2Rのみ」「SingleFileのみ」の3構成をビルドし、スモークテストで起動するものを採用する**
- `InvariantGlobalization=true` は ICU を落とせてサイズ・起動に効くが、`CultureInfo` が全て Invariant になる。**日付・数値の書式は必ず明示指定**(`"yyyy/MM/dd HH:mm:ss"` など)する規約にする。日本語の表示自体には影響しない

---

## GitHub Actions (`.github/workflows/build.yml`)

`windows-latest` 単一ジョブ構成。既存プロジェクトのPages用ワークフローとは全く別物になる。

1. **test** — `dotnet test tests/NetworkToys.Core.Tests`(ネットワーク非依存の純粋ロジック)
2. **build** — `dotnet publish src/NetworkToys.App -c Release`(上記の設定)
3. **smoke** — publishしたexeを起動 → 5秒待機 → プロセス生存確認 → 終了。加えて `NetworkToys.exe --selftest` で全サービスの初期化を通し、終了コード0を確認
4. **artifact** — exeをArtifactにアップロード(毎push)
5. **release** — `v*` タグのpush時のみ、exeを添付してGitHub Releaseを作成

トリガは `push (main)` / `pull_request` / `workflow_dispatch` / `push (tags: v*)`。

---

## 配布と署名

**未署名で配っている。** そのため Chrome は「一般的にダウンロードされていません／不審なファイル」、
Windows は SmartScreen の「WindowsによってPCが保護されました」を出す。

どちらも「危険なものが見つかった」ではなく**「署名が無く、まだ広まっていない」**という判定。
評判はバイナリのハッシュ単位で貯まるので、**リリースのたびにゼロへ戻る**。
待っていても消えないし、リリースを重ねても改善しない。

補える範囲は次の 3 つ。いずれも警告そのものは消えない。

- `SHA256SUMS.txt` を Release に添える（**実装済み**）
- ビルドを CI だけで行い、ログを公開したままにする（**実装済み**。誰がどのコミットから作ったかが残る）
- Google / Microsoft へ誤検知として報告する（README に導線あり）

**難読化や圧縮で検知を避けにいかないこと。** 効果が薄いうえ、
高エントロピーなバイナリはかえってヒューリスティックに引っかかる。
`EnableCompressionInSingleFile` を採らないのはメモリと起動時間が理由だが、この面でも都合がよい。

### 恒久的に消すには

コード署名証明書が要る。取りうる選択肢:

| 方法 | 費用 | SmartScreen | CI から使えるか |
|---|---|---|---|
| **Azure Trusted Signing** | 月 $10 程度 | **即時に効く**（Microsoft のルートにつながる） | ○ 公式の action がある |
| SignPath Foundation | 無料（OSS 向け・審査あり） | ○ | ○ |
| 従来の OV 証明書 | 年 3〜7 万円 | △ 評判は貯め直し | △ 別途クラウド HSM が要る |
| EV 証明書 | 年 5〜10 万円 | ○ 即時 | △ 同上 |

**2023 年 6 月以降、認証局が発行するコード署名の秘密鍵はハードウェア保管が必須**になった。
USB トークン式は GitHub Actions から触れないので、CI で署名するならクラウド署名サービスを選ぶことになる。
これが Trusted Signing / SignPath を推す理由で、値段だけの話ではない。

導入するときは publish のあと・zip にまとめる前に署名ステップを挟む。
**Secrets が未設定のときは署名を飛ばす作り**にすること（fork からの PR とローカル検証を壊さないため）。

---

## 実装フェーズ

**2026-08-15 時点で Phase 0〜6 をすべて実装済み。** 実装中に判明した設計変更は以下:

- **MVVM ライブラリは不採用** — `ObservableObject` と `RelayCommand` を自前で持つ(合わせて40行)
- **`InvariantGlobalization` は使えない** — WPF のテキスト整形が ICU を必要とする(実測)
- **品質指標は一覧の列にしない** — 行を選んだときに画面下へ出す。列を増やすと一覧性が落ちるため
- **宛先リストは EXPing 互換のテキスト一括編集** — 1件ずつ追加する UI は廃止(ユーザー要望)
- **プロファイル判定に SSID を必須にしない** — ゲートウェイMAC 3点/サブネット 3点/ゲートウェイIP 2点/SSID 1点で、合計3点以上を一致とみなす

**2026-08-15 に「接続」タブを追加。** GetExtendedTcpTable/GetExtendedUdpTable の接続一覧(非管理者可)+ ETW(Microsoft-Windows-Kernel-Network)による接続ごとの送受信 B/秒(管理者限定・非管理者では案内つきで一覧のみに縮退)。「全機能非管理者」方針の初の例外。詳細は CLAUDE.md の Windows API の項。

**無線サーベイ(フロア図+ヒートマップ)は 2026-08-16 に実装後、ユーザー判断で削除した。** 「思っていたものと違う」とのことで、再提案しないこと(実装は git 履歴 8acb3e2 に残っている)。

各フェーズの終わりに必ずCIをグリーンにしてから次へ進む。ローカルで動作確認できない以上、これが唯一の安全網。

| Phase | 内容 | 完了条件 |
|---|---|---|
| **0** | リポジトリ作成、sln + 3プロジェクトの骨組み、パステル配色の空ウィンドウ、CI(test/build/smoke/artifact)、ビルド設定3構成の実測 | exeがCI上で起動する。採用するpublish設定が確定する |
| **1** | 宛先リストの編集・保存、**並列ping**、状態バッジ + スパークライン、UI基盤(仮想化・バッチ更新) | 手元のWindowsで数百宛先を同時監視できる |
| **2** | TCP ping(3状態表示)、DNSテスト(複数サーバ比較)、traceroute(並列TTL) | 各タブが単体で使える |
| **3** | IPスキャン(範囲パーサ + テスト)、ARP/OUI解決、簡易ポートスキャン | サブネットを指定してホスト一覧が出る |
| **4** | Wi-Fi情報、RSSI時系列、チャンネル混雑グラフ | 無線タブが完成 |
| **5** | ジッタ/ロス/MOS、Path MTU、経路変化検知 | 品質指標が監視画面に出る |
| **6** | HTML/CSVレポート、プロファイル自動切替提案、セッション閲覧 | 現場の証跡として提出できる |

Phase 1 の完了時点で「EXPingの不満を解消する」という当初目的は達成される。以降は付加価値。

---

## リスクと注意点

| リスク | 対応 |
|---|---|
| **exeをローカルで実行検証できない** | Coreへのロジック分離 + xUnit + CIスモークテスト。それでもUIの見た目だけは実機確認が必要 — Phase 0 と 1 の完了時にスクリーンショットをいただきたい |
| WPFのトリミング非対応・SingleFile+R2Rの不具合 | Phase 0 でCI実測して構成を確定 |
| ICMPがブロックされる環境 | pingが全滅しても TCP ping / DNS で代替できる導線をUIに用意 |
| ポートスキャンのEDR誤検知 | 既定は22ポートのプリセット、設定で機能OFF可、UIに注意書き |
| **Windows 11 24H2 で Wi-Fi API に位置情報の同意が必要** | 起動時にスキャンしない。`UnauthorizedAccessException` を捕捉して設定画面への導線を出す。プロファイル判定はSSIDに依存させない |
| Windowsのスキャン頻度制限で古いAP情報が返る | 自動更新は最短10秒・既定30秒。最終取得時刻をUIに表示 |
| 単一ファイル発行で `Assembly.Location` が空になる | exe横のファイルは `AppContext.BaseDirectory`、自分のパスは `Environment.ProcessPath` を使う |
| 未署名exeのSmartScreen警告 | ReleaseにSHA256を添付し、READMEに回避手順を明記 |
| 大量宛先でのUI描画破綻 | 仮想化 + 16msバッチ更新 + DrawingVisual描画 |
| `/16` などの誤爆スキャン | 実行前に対象件数を表示、既定上限4096件 |
| 管理者権限 | **全機能を非管理者で動作させる**方針。ICMPもARP参照も接続一覧も権限不要。唯一の例外は接続タブの通信量(ETW が管理者限定)で、非管理者では案内を出して一覧のみに縮退する。`asInvoker` は不変 |

---

## 検証方法

- **CI**: `dotnet test`(Coreロジック)、publish成功、exe起動スモークテスト、`--selftest` の終了コード
- **手元のWindows**: Artifactからexeをダウンロードして起動。以下を実機確認していただく
  - 起動時間(目標0.6秒以内)とタスクマネージャ上のメモリ(目標90MB以内)
  - 宛先を100件以上登録して並列pingがカクつかないこと
  - Wi-Fi情報が実際に取得できること(APIの挙動は実機でしか分からない)
  - パステル配色の見え方(モニタによって印象が変わるため)
- **回帰**: IP範囲パーサ、MOS計算、OUI解決、レポート生成には必ずユニットテストを書く
