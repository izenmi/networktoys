using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;   // Thumb（列幅のつまみ）
using System.Windows.Media;
using PingWatcher.Core.Addressing;

namespace PingWatcher.App;

/// <summary>
/// <c>PingWatcher.exe --selftest</c> で走る自己診断。
///
/// 開発環境が Linux で exe を実行できないため、これが CI 側の目になる。
/// とくに XAML はコンパイル時に検出されないエラー(リソースキーの打ち間違い、
/// テンプレートの型不一致など)が多いので、MainWindow を実際に生成して確かめる。
///
/// 終了コード 0 = 全項目成功、1 = 失敗あり。結果は標準出力と selftest.log の両方に書く
/// (WinExe はコンソールに繋がらないことがあるため)。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var log = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, Action action)
        {
            // catch できないクラッシュ(AccessViolation や fail-fast)で全ログが失われると
            // 「どの検査で死んだか」すら分からない(ETW の構造体ずれで実際に起きた)。
            // どの検査に入ったかだけ先にファイルへ残し、完走したら全文で上書きする
            TryAppendProgress($"  実行中: {name}");

            try
            {
                action();
                log.AppendLine($"  OK    {name}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                log.AppendLine($"  FAIL  {name}");
                log.AppendLine($"        {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is { } inner)
                    log.AppendLine($"        原因: {inner.GetType().Name}: {inner.Message}");
            }
        }

        static void TryAppendProgress(string line)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.CurrentDirectory, "selftest.log"),
                    line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 進捗が残せないだけ。検査は続ける
            }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        // 検査でウィンドウを開閉するため、最後の 1 枚を閉じた時点でアプリが終わらないようにする
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        log.AppendLine($"PingWatcher selftest  ({DateTime.Now:yyyy/MM/dd HH:mm:ss})");
        log.AppendLine($"  ランタイム: {Environment.Version} / OS: {Environment.OSVersion.VersionString}");
        log.AppendLine();

        Check("Core: IpMath がリンクされ、期待どおり計算する", () =>
        {
            Assert(IpMath.ToUInt32(IPAddress.Parse("192.168.1.1")) == 3232235777u, "ToUInt32 の値が違う");
            Assert(Equals(IpMath.NetworkAddress(IPAddress.Parse("192.168.1.130"), 24), IPAddress.Parse("192.168.1.0")),
                   "NetworkAddress の値が違う");
            Assert(IpMath.UsableHostCount(24) == 254, "UsableHostCount の値が違う");
        });

        Check("リソース: Palette.xaml のブラシが解決できる", () =>
        {
            string[] keys =
            [
                "Brush.Background", "Brush.Surface", "Brush.SurfaceAlt", "Brush.Border",
                "Brush.Text", "Brush.TextMuted",
                "Brush.Ok.Bg", "Brush.Ok.Fg", "Brush.Warn.Bg", "Brush.Warn.Fg",
                "Brush.Error.Bg", "Brush.Error.Fg", "Brush.Info.Bg", "Brush.Info.Fg",
                "Brush.Accent.Bg", "Brush.Accent.Fg", "Brush.Chart.Line", "Brush.Chart.Fill",
                "Brush.Chip.Edge", "Brush.Button.Edge", "Brush.Scroll.Thumb", "Brush.Scroll.Thumb.Hover", "Brush.Scroll.Track", "Brush.Row.Line",
            ];
            foreach (string key in keys)
                Assert(Application.Current.TryFindResource(key) is SolidColorBrush, $"{key} が SolidColorBrush として引けない");

            // 面と枠のグラデーション。単色ではないので別に見る
            string[] gradients =
            [
                "Brush.Window.Backdrop", "Brush.Card.Face", "Brush.Card.Edge", "Brush.Accent.Gradient",
            ];
            foreach (string key in gradients)
                Assert(Application.Current.TryFindResource(key) is Brush, $"{key} が Brush として引けない");
        });

        Check("リソース: Controls.xaml のスタイルが解決できる", () =>
        {
            string[] keys =
            [
                "Card", "Badge", "Badge.Text", "Heading", "Caption", "Mono",
                "Button.Subtle", "Button.Icon", "ScrollBar.Thumb", "MenuBarItem",
            ];
            foreach (string key in keys)
                Assert(Application.Current.TryFindResource(key) is Style, $"{key} が Style として引けない");
        });

        Check("リソース: 型キーの暗黙スタイルが失われていない", () =>
        {
            // ListBox / ContextMenu などの既定スタイルは Foreground にシステム色（黒）を
            // 入れるため、暗黙スタイルでの上書きが消えると「ダークで文字だけ黒い」に退行する。
            // 見た目の退行はウィンドウ表示の検査を素通りするので、ここで存在を確かめる。
            Type[] types =
            [
                typeof(System.Windows.Controls.ListBox),
                typeof(System.Windows.Controls.ListBoxItem),
                typeof(System.Windows.Controls.ListView),
                typeof(System.Windows.Controls.ContextMenu),
                typeof(System.Windows.Controls.Menu),
                typeof(System.Windows.Controls.MenuItem),
                typeof(System.Windows.Controls.ToolTip),
                typeof(System.Windows.Controls.CheckBox),
                typeof(System.Windows.Controls.ComboBox),
                typeof(System.Windows.Controls.ComboBoxItem),
                typeof(System.Windows.Controls.TabItem),
                typeof(System.Windows.Controls.Button),
                typeof(System.Windows.Controls.TextBox),
                typeof(System.Windows.Controls.PasswordBox),
            ];
            foreach (Type type in types)
                Assert(Application.Current.TryFindResource(type) is Style, $"{type.Name} の暗黙スタイルが無い");

            // メニュー内の区切り線は専用キーで引かれる（Separator の暗黙スタイルは当たらない）
            Assert(Application.Current.TryFindResource(System.Windows.Controls.MenuItem.SeparatorStyleKey) is Style,
                   "MenuItem.SeparatorStyleKey のスタイルが無い");
        });

        Check("リソース: Tokens.xaml の寸法と書体が解決できる", () =>
        {
            // DynamicResource は引けなくても例外にならず、既定値で静かに描かれてしまう。
            // 打ち間違いに気づけるのはここだけ。
            string[] radii = ["Radius.Card", "Radius.Control", "Radius.Badge", "Radius.Chip"];
            foreach (string key in radii)
                Assert(Application.Current.TryFindResource(key) is CornerRadius, $"{key} が CornerRadius として引けない");

            string[] sizes = ["Size.Title", "Size.Heading", "Size.Body", "Size.Caption", "Size.Micro", "Size.RowHeight"];
            foreach (string key in sizes)
                Assert(Application.Current.TryFindResource(key) is double, $"{key} が double として引けない");

            foreach (string key in new[] { "Font.Ui", "Font.Mono" })
                Assert(Application.Current.TryFindResource(key) is FontFamily, $"{key} が FontFamily として引けない");
        });

        Check("配色: 明暗の 2 つのパレットが同じキーを持つ", () =>
        {
            // 片方にだけ色を足すと、切り替えた瞬間にその色が消える。
            // 目視では気づけないので突き合わせる。
            HashSet<string> dark = KeysOf("Resources/Palette.Dark.xaml");
            HashSet<string> light = KeysOf("Resources/Palette.xaml");

            Assert(dark.Count > 0, "ダークのパレットが空");

            string[] missingInLight = [.. dark.Except(light).Order()];
            string[] missingInDark = [.. light.Except(dark).Order()];

            Assert(missingInLight.Length == 0, $"ライトに無いキー: {string.Join(", ", missingInLight)}");
            Assert(missingInDark.Length == 0, $"ダークに無いキー: {string.Join(", ", missingInDark)}");

            log.AppendLine($"        共通のキー: {dark.Count} 件");
        });

        Check("配色: 文字と地のコントラストが足りている", () =>
        {
            // 淡くしたい気持ちと読めることは必ず衝突する。目で見て決めると、
            // 明るい画面で作った色が暗い画面で沈む。ここで毎回検算する。
            foreach (string source in new[] { "Resources/Palette.Dark.xaml", "Resources/Palette.xaml" })
            {
                var palette = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
                string name = source.Contains("Dark", StringComparison.Ordinal) ? "ダーク" : "ライト";

                Color ColorOf(string key)
                {
                    Assert(palette[key] is SolidColorBrush, $"{name}: {key} が SolidColorBrush ではない");
                    return ((SolidColorBrush)palette[key]!).Color;
                }

                void Ensure(string foreground, string background)
                {
                    Color f = ColorOf(foreground);
                    Color b = ColorOf(background);
                    double ratio = Core.Design.ColorMath.ContrastRatio(f.R, f.G, f.B, b.R, b.G, b.B);

                    Assert(ratio >= Core.Design.ColorMath.MinimumForText,
                           $"{name}: {foreground} を {background} に載せると {ratio:F2}（{Core.Design.ColorMath.MinimumForText} 必要）");
                }

                int pairs = 0;

                void Count(string foreground, string background)
                {
                    Ensure(foreground, background);
                    pairs++;
                }

                Count("Brush.Text", "Brush.Surface");
                Count("Brush.Text", "Brush.Background");
                Count("Brush.TextMuted", "Brush.Surface");
                Count("Brush.TextMuted", "Brush.SurfaceAlt");
                Count("Brush.TextMuted", "Brush.Background");

                foreach (string state in new[] { "Ok", "Warn", "Error", "Info", "Accent" })
                {
                    // バッジ（地色つき）と、地色を敷かない場所の両方で読めること
                    Count($"Brush.{state}.Fg", $"Brush.{state}.Bg");
                    Count($"Brush.{state}.Fg", "Brush.Surface");

                    // 実際の画面は状態の地色の上に通常の文字も載せている
                    // （差分の左右セル・経路の到達行・選択行など）。
                    // ここを検算に入れておかないと、どちらかを一段淡くした時点で
                    // 目視では気づけないまま基準を割る
                    Count("Brush.Text", $"Brush.{state}.Bg");
                    Count("Brush.TextMuted", $"Brush.{state}.Bg");
                }

                log.AppendLine($"        {name}: {pairs} 組すべて基準を満たす");
            }
        });

        Views.MainWindow? window = null;
        Check("MainWindow を生成できる(XAML の妥当性確認)", () => window = new Views.MainWindow());

        // Show せずに Measure を呼んでもテキスト整形までは到達せず、
        // フォント周りの初期化不良を見逃す(InvariantGlobalization の事故で実証済み)。
        // 実際に表示してレイアウトを完了させること。
        Check("MainWindow を表示してレイアウトできる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため表示できない");
            window!.Show();
            window.UpdateLayout();
            Assert(window.ActualWidth > 0 && window.ActualHeight > 0, "ウィンドウの実サイズが 0 のまま");
        });

        Check("画面を PNG に描き出せる(スクリーンショット機能)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // ファイルには書かない。描画とエンコードの経路が生きていることだけ確かめる
            using var stream = new MemoryStream();
            window!.CaptureWindow().Save(stream);

            Assert(stream.Length > 1000, $"PNG が小さすぎる ({stream.Length} バイト)");
        });

        Check("タブが 1 列に収まる既定のウィンドウ幅", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // タブを足すと黙って 2 列になり、既定幅の妥当性が誰にも分からなくなる。
            // 実測して既定幅と突き合わせる(ローカルで実行できないので、ここが唯一の目)
            double tabs = 0;

            foreach (object? item in window!.MainTabs.Items)
            {
                if (item is not System.Windows.Controls.TabItem tab) continue;

                tabs += tab.ActualWidth + tab.Margin.Left + tab.Margin.Right;
            }

            // 本体の左右余白(Grid の Margin)と、ウィンドウの枠ぶん
            double needed = tabs + 12 + SystemParameters.ResizeFrameVerticalBorderWidth * 2 + 4;

            log.AppendLine($"        タブの実寸 {tabs:F0}px / 必要 {needed:F0}px / 既定 {window.Width:F0}px");

            Assert(window.Width >= needed,
                   $"既定幅 {window.Width:F0}px ではタブが 2 列になる(必要 {needed:F0}px)");
        });

        Check("すべてのタブを表示できる(遅延生成される中身の検査)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // TabControl は選ばれたタブしか実体化しないので、既定タブの表示だけでは
            // 他のタブのテンプレート適用時のエラー(リソースキーの打ち間違いなど)を見逃す
            object? original = window!.MainTabs.SelectedItem;

            int visited = VisitTabs(window, window.MainTabs, _ => { });

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();

            log.AppendLine($"        実体化したタブ: {visited} 枚");
        });

        Check("表の見出しと行の左右が揃っている", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // 見出しは一覧の外にある別の Grid なので、余白がずれると列が噛み合わなくなる。
            // 実際に 3 通りのずれ方をしていた:
            //   ・接続のプロセス見出し行だけ枠に Padding があり二重に 4px 入っていた
            //   ・Meraki の 4 一覧が行コンテナを指定しておらず、既定の余白で描かれていた
            //   ・スクロールバーが出た瞬間に行だけ 14px 狭くなり、可変幅列より右がずれていた
            //
            // 見た目のずれは画面を出すだけの検査を素通りするので、規約として突き合わせる。
            //   見出しの余白 = 4,0,18,3（右の 18 は 行の 4 + スクロールバーの 14）
            //   行の一覧      = 縦スクロールバーを常に確保する
            const double ScrollBarWidth = 14;   // Controls.xaml の ScrollBar スタイルで固定
            var expected = new Thickness(4, 0, 4 + ScrollBarWidth, 3);

            object? original = window!.MainTabs.SelectedItem;
            var problems = new List<string>();
            int headers = 0;

            VisitTabs(window, window.MainTabs, _ =>
            {
                foreach (System.Windows.Controls.Grid header in FindTableHeaders(window))
                {
                    headers++;

                    if (header.Margin != expected)
                        problems.Add($"見出しの余白が {header.Margin}（期待は {expected}）");
                }
            });

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();

            Assert(problems.Count == 0, string.Join(" / ", problems.Distinct()));
            log.AppendLine($"        見合わせた見出し: {headers} 個");
        });

        Check("見えていないタブは動き出さない", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // TabItem.IsSelected は「その TabControl の中で選ばれているか」しか表さない。
            // 内側の TabControl は生成時に先頭の子を自動で選ぶので、親タブを一度も
            // 開いていなくても内側の 1 枚目は true になる。
            // そのまま OnActivated() を呼ぶと、見えていないタブが OS を叩き始める
            // （無線は位置情報の同意を求め、遮断は WFP を開き、接続は ETW を回す）。
            object? original = window!.MainTabs.SelectedItem;

            // 記録タブを選ぶ = ほかのタブはどれも見えていない状態
            window.ReportTab.IsSelected = true;
            window.UpdateLayout();

            TabItem[] mustBeHidden =
                [window.WifiTab, window.WfpTab, window.ConnectionsTab, window.TraceTab, window.IpConfigTab];

            foreach (TabItem tab in mustBeHidden)
            {
                Assert(!Views.MainWindow.IsShowing(tab),
                       $"記録タブを選んでいるのに「{tab.Header}」が見えている扱いになっている");
            }

            Assert(Views.MainWindow.IsShowing(window.ReportTab), "選んだタブが見えている扱いにならない");

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("Meraki タブのサブタブをすべて表示できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // サブタブの中身も選ばないと実体化しない(親タブを開くだけでは 1 枚目しか作られない)
            object? original = window!.MainTabs.SelectedItem;
            window.MerakiTab.IsSelected = true;

            foreach (object? item in window.MerakiSubTabs.Items)
            {
                ((System.Windows.Controls.TabItem)item).IsSelected = true;
                window.UpdateLayout();
            }

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("Meraki: キー未入力では取得できない(CI から API を叩かない)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;

            // タブを開くだけでは通信しない作りなので、上のタブ巡回でも API は叩かれない。
            // 念のため、キーが空の間はコマンドが動かないことも確かめておく
            Assert(shell.Meraki.ApiKey.Length == 0, "起動直後に API キーが入っている");
            Assert(!shell.Meraki.FetchCommand.CanExecute(null), "キー未入力でも取得できてしまう");
            Assert(!shell.Meraki.FetchClientsCommand.CanExecute(null), "キー未入力でもクライアントを取得できてしまう");
        });

        Check("Core: Meraki の応答を一覧の行に変換できる", () =>
        {
            const string uplinks = """
                [ { "networkId": "N_1", "serial": "Q2AA-1111-AAAA", "uplinks": [
                    { "interface": "wan1", "status": "active", "publicIp": "203.0.113.5" },
                    { "interface": "wan2", "status": "ready", "publicIp": "203.0.113.9" } ] } ]
                """;

            IReadOnlyList<Core.Cloud.MerakiUplinkRow> rows = Core.Cloud.MerakiCatalog.ParseUplinks([uplinks], []);

            Assert(rows.Count == 2, $"WAN1/WAN2 の 2 行になるはずが {rows.Count} 行");
            Assert(rows[0].State.StartsWith('●'), $"active が記号付きにならない: {rows[0].State}");
            Assert(Core.Cloud.MerakiCatalog.GlobalIpSummary(rows) == "203.0.113.5 / 203.0.113.9",
                   "グローバル IP の要約が期待どおりでない");
        });

        Check("メニューを開ける(ポップアップの実体化検査)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // ドロップダウンの中身(PART_Popup)は開くまで実体化されないので、
            // テンプレートやリソースキーの誤りはこの検査でしか捕まえられない
            foreach (object? item in window!.MainMenu.Items)
            {
                if (item is not System.Windows.Controls.MenuItem top) continue;

                top.IsSubmenuOpen = true;
                window.UpdateLayout();
                top.IsSubmenuOpen = false;
            }
            window.UpdateLayout();
        });

        Check("無線タブの画面を実体化できる(WLAN API には触れない)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // 無線タブは選んだ時点で WLAN API に触れるため全タブ検査からは外している。
            // ここでは抑止フラグを立て、XAML の実体化だけを確かめる
            object? original = window!.MainTabs.SelectedItem;
            window.SuppressWifiActivation = true;

            try
            {
                window.WifiTab.IsSelected = true;
                window.UpdateLayout();
            }
            finally
            {
                window.MainTabs.SelectedItem = original;
                window.UpdateLayout();
                window.SuppressWifiActivation = false;
            }
        });

        Check("配色を切り替えても表示し直せる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            AppTheme original = ThemeManager.Current;

            foreach (AppTheme theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme);
                window!.UpdateLayout();

                Assert(ThemeManager.Current == theme, $"{theme} に切り替わっていない");
                Assert(window.ActualWidth > 0 && window.ActualHeight > 0, $"{theme} でウィンドウの実サイズが 0");

                // 差し替えたパレットが実際に効いているか（色が引けるかまで見る）
                Assert(Application.Current.TryFindResource("Brush.Background") is SolidColorBrush,
                       $"{theme} で Brush.Background が引けない");
            }

            ThemeManager.Apply(original);
            window!.UpdateLayout();
            window.Close();
        });

        Check("確認ダイアログを表示できる", () =>
        {
            // XAML ではなくコードで組んでいるが、リソースキーの打ち間違いは
            // やはり実行時にしか分からないので、実際に表示まで通す
            var dialog = new Views.ConfirmDialog("確認", "検査用の本文です。", "実行する");
            dialog.Show();
            dialog.UpdateLayout();

            Assert(dialog.ActualWidth > 0 && dialog.ActualHeight > 0, "ダイアログの実サイズが 0");
            dialog.Close();
        });

        Check("ICMP を実行できる(応答の有無は問わない)", () =>
        {
            // 管理者権限なしで Ping が動くこと自体の確認。
            // CI や社内網ではループバックすら塞がれることがあるので、
            // 応答が返るかどうかは合否に含めない。
            using var ping = new System.Net.NetworkInformation.Ping();
            // 同期版の Send はミリ秒の int しか受け取らない（TimeSpan は SendPingAsync のみ）
            System.Net.NetworkInformation.PingReply reply = ping.Send(IPAddress.Loopback, 2000);
            log.AppendLine($"        ループバックの応答: {reply.Status}");
        });

        Check("OUI テーブルを読める(埋め込みリソースの確認)", () =>
        {
            int count = Services.OuiCatalog.Current.Count;
            Assert(count > 1000, $"登録件数が少なすぎます: {count}");

            string? vendor = Services.OuiCatalog.FindVendor("00-15-5D-01-02-03");
            Assert(vendor is not null, "既知の OUI を引けません");

            log.AppendLine($"        {count:N0} 件 / 00-15-5D → {vendor}");
        });

        Check("ARP テーブルを読める(件数は問わない)", () =>
        {
            Dictionary<string, string> arp = Interop.NativeMethods.GetArpTable();
            log.AppendLine($"        近隣キャッシュ: {arp.Count} 件");
        });

        Check("TCP/UDP の接続表を読める(件数は問わない)", () =>
        {
            List<Core.Net.ConnectionRow> connections = Interop.NativeMethods.GetConnectionTable();
            (int tcp, int udp, int processes) = Core.Net.ConnectionTableView.Count(connections);
            log.AppendLine($"        TCP {tcp} / UDP {udp} / {processes} プロセス");
        });

        Check("IP設定: プロキシ設定を読める", () =>
        {
            // 読むだけ。書き込み(ProxySettings.Apply)はランナーの設定を汚すので呼ばない
            Services.ProxyState state = Services.ProxySettings.Read();
            log.AppendLine($"        {state.Summary}");
        });

        Check("IP設定: アダプタを列挙できる(件数は問わない)", () =>
        {
            // ElevatedNetsh はここから絶対に呼ばない。CI ランナーは管理者なので
            // UAC なしで本当に適用され、ランナーのネットワークが壊れる
            IReadOnlyList<Services.NetworkAdapterInfo> adapters = Services.NetworkEnvironment.ListAdapters();
            log.AppendLine($"        アダプタ: {adapters.Count} 枚");
        });

        Check("一覧から次の道具へ送れる（行の型からアドレスが取れる）", () =>
        {
            // メニューは 6 つの一覧で 1 つの定義を使い回すので、
            // 行の型を 1 つでも取りこぼすと「右クリックしても何も起きない」になる。
            // ここは黙って失敗する側なので、型ごとに実際に取り出して確かめる。
            (object Row, string Expected)[] cases =
            [
                (new Core.Net.ConnectionDetailRow(
                    "TCP", "192.168.1.5:50000", "93.184.216.34:443", "ESTABLISHED",
                    Core.Net.ConnectionStateKind.Ok, "0", "0", "k"), "93.184.216.34"),

                // 角かっこ付きの IPv6。中身だけ取り出す
                (new Core.Net.ConnectionDetailRow(
                    "TCPv6", "[::1]:50000", "[2001:db8::1]:443", "ESTABLISHED",
                    Core.Net.ConnectionStateKind.Ok, "0", "0", "k"), "2001:db8::1"),

                // LISTEN の行はリモートが無い。宛先にはできない
                (new Core.Net.ConnectionDetailRow(
                    "TCP", "0.0.0.0:445", "—", "LISTEN",
                    Core.Net.ConnectionStateKind.Ok, "0", "0", "k"), ""),

                (new Core.Net.WfpBlockedRow(
                    "10:00:00", DateTime.UtcNow, "送信", "TCP", "192.168.1.5:50000",
                    "203.0.113.9:445", "app.exe", @"C:\app.exe", 1, "1", "0", "0", "k"), "203.0.113.9"),

                (new Core.Cloud.MerakiDeviceRow(
                    "MX-01", "MX68", "Q2QN", "18.1", "本社", "オンライン",
                    Core.Net.ConnectionStateKind.Ok, "203.0.113.1", "192.168.128.1"), "192.168.128.1"),

                (new Core.Cloud.MerakiClientRow(
                    "pc-01", "192.168.128.50", "aa:bb:cc:dd:ee:ff", "10", "Dell", "1 MB", "10:00"), "192.168.128.50"),

                (new ViewModels.FileServerLogRow("10:00:00", "10.1.1.1", "%LINK-3-UPDOWN", 3), "10.1.1.1"),

                // 見覚えのない型は空。落ちてはいけない
                (new object(), ""),
            ];

            foreach ((object row, string expected) in cases)
            {
                var probe = new System.Windows.Controls.MenuItem { DataContext = row };
                string actual = Views.MainWindow.AddressOf(probe);

                Assert(actual == expected,
                       $"{row.GetType().Name} から取れたのは「{actual}」（期待は「{expected}」）");
            }

            log.AppendLine($"        行の型: {cases.Length} 通り");
        });

        Check("試験: 項目の行テンプレートを実体化できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // 項目が 0 件だと行テンプレートが一度も作られず、
            // リソースキーの誤りを素通りしてしまう
            var shell = (ViewModels.ShellViewModel)window!.DataContext;
            object? original = window.MainTabs.SelectedItem;

            window.VerifyTab.IsSelected = true;
            shell.Verify.ApplyTemplateCommand.Execute(null);
            window.UpdateLayout();

            Assert(shell.Verify.Rows.Count > 0, "ひな型を入れても行が作られない");

            // タブを開いてひな型を入れただけでは、外へ 1 バイトも出ないこと。
            // 出す作りにすると CI の Windows ランナーから本番へ試験が飛ぶ
            Assert(shell.Verify.Results.Count == 0, "実行していないのに結果が入っている");

            shell.Verify.Reset();
            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("試験: 偽の STUN サーバと UDP で往復できる", () =>
        {
            // Teams の音声が通るかは UDP の応答で決まる。実機も外部通信も要らずに、
            // loopback へ立てた偽サーバ相手に送受信の経路を丸ごと通す
            // （偽の Cisco 機器と同じ手）
            using var server = new System.Net.Sockets.UdpClient(
                new IPEndPoint(IPAddress.Loopback, 0));

            int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
            var seen = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 51234);

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Task serve = Task.Run(async () =>
            {
                System.Net.Sockets.UdpReceiveResult got = await server.ReceiveAsync(stop.Token);

                // 受け取った要求と同じトランザクション ID で返す
                byte[] response = Core.Verify.StunMessage.BuildSuccessResponse(
                    got.Buffer.AsSpan(8, 12), seen);

                await server.SendAsync(response, got.RemoteEndPoint, stop.Token);
            }, stop.Token);

            Services.StunOutcome outcome = Services.StunProbe
                .RunAsync("127.0.0.1", port, 3000, stop.Token).GetAwaiter().GetResult();

            Assert(outcome.Reachable, $"応答を受け取れない: {outcome.Problem}");
            Assert(Equals(outcome.SeenAddress, seen),
                   $"外から見えるアドレスが違う: {outcome.SeenAddress}");

            stop.Cancel();
            try { serve.Wait(2000); } catch (AggregateException) { /* 止めたので握る */ }

            log.AppendLine($"        往復 {outcome.ElapsedMs:0} ms / 見えたアドレス {outcome.SeenAddress}");
        });

        Check("収集: 機器の行テンプレートを実体化できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // 機器が 0 台だと PasswordBox を含むテンプレートが一度も作られず、
            // リソースキーの誤りを素通りしてしまう。1 行流し込んで実体化させる
            var shell = (ViewModels.ShellViewModel)window!.DataContext;
            object? original = window.MainTabs.SelectedItem;

            window.CollectTab.IsSelected = true;
            shell.Collect.Import([("192.0.2.1", "自己診断用")]);
            window.UpdateLayout();

            Assert(shell.Collect.Rows.Count == 1, $"機器の行が作られない: {shell.Collect.Rows.Count}");

            // 行が増えても「行を追加」が押せること。一覧に高さを取り切られて
            // ボタンのぶんが残らなくなる事故を 2 度やっている
            shell.Collect.Import(Enumerable.Range(2, 40).Select(i => ($"192.0.2.{i}", "自己診断用")));
            window.UpdateLayout();

            Assert(window.CollectAddRow.ActualHeight > 0 && window.CollectAddRow.ActualWidth > 0,
                   $"行が {shell.Collect.Rows.Count} 台になると「行を追加」が潰れる"
                   + $"（{window.CollectAddRow.ActualWidth}x{window.CollectAddRow.ActualHeight}）");

            shell.Collect.Reset();
            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("収集: 偽の Cisco 機器から Telnet で集められる", () =>
        {
            // 実機も外部通信も要らずに、TCP → Telnet の制御除去 → 状態機械 →
            // 保存テキストの組み立て、までの実経路を丸ごと通す。この機能の防波堤
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => ServeFakeCisco(listener));

            var options = new Core.Terminal.CiscoSessionOptions
            {
                SettleTime = TimeSpan.FromMilliseconds(80),
                IdleTimeout = TimeSpan.FromSeconds(2),
                LoginTimeout = TimeSpan.FromSeconds(5),
                CommandTimeout = TimeSpan.FromSeconds(5),
            };

            var request = new Services.CollectRequest(
                "127.0.0.1", port, UseSsh: false,
                new Core.Terminal.DeviceCredentials("admin", "pass", "enable"), "");

            Core.Terminal.DeviceCollectionResult result = Services.DeviceCollector.CollectAsync(
                request, ["show version"], options, TimeSpan.FromSeconds(5), null, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert(result.FailureMessage is null, $"収集に失敗: {result.FailureMessage}");
            Assert(result.LearnedHostname == "R1", $"ホスト名を学習できない: {result.LearnedHostname}");

            Core.Terminal.CommandResult command = result.Commands.Single();
            Assert(command.Output.Contains("Cisco IOS Software", StringComparison.Ordinal),
                   $"出力が取れていない: {command.Output}");

            // 保存テキストに認証情報が混ざらないこと
            string report = Core.Terminal.DeviceReport.Render(result);
            Assert(!report.Contains("pass", StringComparison.OrdinalIgnoreCase), "保存テキストにパスワードが混ざっている");

            log.AppendLine($"        {command.Output.Split('\n').Length} 行を取得");
        });

        Check("収集: SSH.NET が単一ファイル発行でも読み込める", () =>
        {
            // 依存を足した直後に単一ファイル発行で初めて出る事故(型が解決できない、
            // ネイティブ資産が展開されない)を、ここで捕まえる。
            // 外部へは接続しない — 閉じているループバックのポートに向けるだけ
            Assert(typeof(Renci.SshNet.ShellStream).IsSubclassOf(typeof(Stream)),
                   "ShellStream が Stream を継承していない(状態機械へ渡せない)");

            using var probe = new Services.SshConnection();
            Stream? shell = probe.Open("127.0.0.1", 1, "u", "p", TimeSpan.FromSeconds(2), out string? error);

            Assert(shell is null, "閉じたポートに繋がってしまった");
            Assert(error is { Length: > 0 }, "失敗の理由が空");

            log.AppendLine($"        {error}");
        });

        Check("FTP: 画面の案内にパスワードが出ない", () =>
        {
            // FTP は認証情報を URL に埋める書き方なので、素直に組み立てると
            // 画面に平文で残り、F12 の画面保存にも焼き込まれる。
            // 実物は「コマンドをコピー」でクリップボードへ渡す決まり。
            const string secret = "s3cr3t-not-on-screen";

            var ftp = new ViewModels.FtpViewModel("192.0.2.1")
            {
                User = "backup",
                Password = secret,
            };

            Assert(!ftp.CommandHint.Contains(secret, StringComparison.Ordinal),
                   $"案内にパスワードが出ている: {ftp.CommandHint}");
            Assert(ftp.CommandHint.Contains("backup", StringComparison.Ordinal),
                   "ユーザー名は出したい（伏せるのはパスワードだけ）");

            // SFTP はそもそも案内に認証情報を載せない
            var sftp = new ViewModels.SftpViewModel("192.0.2.1") { User = "backup", Password = secret };
            Assert(!sftp.CommandHint.Contains(secret, StringComparison.Ordinal),
                   $"SFTP の案内にパスワードが出ている: {sftp.CommandHint}");
        });

        Check("収集: 引き継ぎファイルにパスワードの入れ物が無い", () =>
        {
            // 保存しないと決めたものは、置き場所を用意した時点で誰かが入れてしまう
            System.Reflection.PropertyInfo[] properties = typeof(Core.Storage.HandoverPanels).GetProperties();

            foreach (System.Reflection.PropertyInfo property in properties)
            {
                Assert(!property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase),
                       $"引き継ぎに {property.Name} がある");
            }

            foreach (System.Reflection.PropertyInfo property in typeof(Core.Storage.AppSettingsDocument).GetProperties())
            {
                Assert(!property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase),
                       $"設定に {property.Name} がある");
            }
        });

        Check("昇格の引き継ぎで画面の内容が往復する", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;
            string path = Services.HandoverService.NewPath();

            const string pasted = "interface Gi0/1\n description handover\n";
            shell.Converter.InputText = "show version の出力";
            shell.Dns.Name = "handover.example.test";
            shell.DeviceCompare.BeforeText = pasted;

            try
            {
                Core.Storage.HandoverStore.Save(path, Services.HandoverService.Capture(shell));

                // 読んだ側が必ず消す(機器の出力に認証情報が入りうるので残さない)
                Core.Storage.HandoverDocument? loaded = Core.Storage.HandoverStore.LoadAndDelete(path);
                Assert(loaded is not null, "引き継ぎファイルを読み戻せない");
                Assert(!File.Exists(path), "引き継ぎファイルが消えていない");

                // いったん消してから書き戻し、本当に往復しているか確かめる
                shell.Converter.InputText = "";
                shell.Dns.Name = "";
                shell.DeviceCompare.BeforeText = "";

                Services.HandoverService.Apply(loaded!, shell);

                Assert(shell.Converter.InputText == "show version の出力", "パースの入力が戻っていない");
                Assert(shell.Dns.Name == "handover.example.test", "DNS の宛先が戻っていない");
                Assert(shell.DeviceCompare.BeforeText == pasted,
                       $"差分比較の貼り付けが戻っていない: {shell.DeviceCompare.BeforeText.Length} 文字");

                // API キーは意図して引き継がない(保存しない決まりのもの)
                Assert(!loaded!.Panels.GetType().GetProperties().Any(p => p.Name.Contains("ApiKey")),
                       "引き継ぎに API キーの入れ物がある");
            }
            finally
            {
                Core.Storage.HandoverStore.Delete(path);
            }
        });

        Check("WFP: 合成したイベントを 1 フィールドずつ読み出せる", () =>
        {
            // レイアウト誤りに対する主対策。実イベントが 1 件も無い環境でも必ず走る。
            // オフセット表どおりに自前で組み立てたバッファをパーサに食わせ、
            // 読み出した値が書いた値と一致することを確かめる
            const string appPath = @"\device\harddiskvolume1\windows\system32\svchost.exe";
            var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            byte[] appBytes = System.Text.Encoding.Unicode.GetBytes(appPath + "\0");

            IntPtr item = Marshal.AllocHGlobal(120);
            IntPtr drop = Marshal.AllocHGlobal(56);
            IntPtr app = Marshal.AllocHGlobal(appBytes.Length);

            try
            {
                for (int i = 0; i < 120; i += 8) Marshal.WriteInt64(item, i, 0);
                for (int i = 0; i < 56; i += 8) Marshal.WriteInt64(drop, i, 0);
                Marshal.Copy(appBytes, 0, app, appBytes.Length);

                Marshal.WriteInt64(item, 0, written.ToFileTimeUtc());   // timeStamp
                Marshal.WriteInt32(item, 8, 0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x0010 | 0x0020 | 0x0100);
                Marshal.WriteInt32(item, 12, 0);                        // ipVersion = v4
                Marshal.WriteByte(item, 16, 6);                         // ipProtocol = TCP
                Marshal.WriteInt32(item, 20, unchecked((int)0xC0A8010A)); // local  192.168.1.10
                Marshal.WriteInt32(item, 36, unchecked((int)0xCB007109)); // remote 203.0.113.9
                Marshal.WriteInt16(item, 52, unchecked((short)51234));
                Marshal.WriteInt16(item, 54, 443);
                Marshal.WriteInt32(item, 64, appBytes.Length);          // appId.size
                Marshal.WriteIntPtr(item, 72, app);                     // appId.data
                Marshal.WriteInt32(item, 104, 3);                       // type = CLASSIFY_DROP
                Marshal.WriteIntPtr(item, 112, drop);                   // union

                Marshal.WriteInt64(drop, 0, 0x1122334455667788);        // filterId
                Marshal.WriteInt16(drop, 8, 44);                        // layerId
                Marshal.WriteInt32(drop, 24, 0);                        // msFwpDirection = 送信
                Marshal.WriteInt32(drop, 28, 0);                        // isLoopback

                Core.Net.WfpBlockedEvent? parsed = Interop.WfpNativeMethods.ParseDropEvent(item);
                Assert(parsed is not null, "合成したイベントを読み出せない");

                Assert(parsed!.TimeUtc == written, $"時刻が違う: {parsed.TimeUtc:O}");
                Assert(parsed.Protocol == 6, $"プロトコルが違う: {parsed.Protocol}");
                Assert(parsed.Local?.ToString() == "192.168.1.10", $"送信元が違う: {parsed.Local}");
                Assert(parsed.Remote?.ToString() == "203.0.113.9", $"宛先が違う: {parsed.Remote}");
                Assert(parsed.LocalPort == 51234, $"送信元ポートが違う: {parsed.LocalPort}");
                Assert(parsed.RemotePort == 443, $"宛先ポートが違う: {parsed.RemotePort}");
                Assert(parsed.AppIdRaw == appPath, $"パスが違う: {parsed.AppIdRaw}");
                Assert(parsed.FilterId == 0x1122334455667788UL, $"フィルタ ID が違う: {parsed.FilterId}");
                Assert(parsed.LayerId == 44, $"レイヤ ID が違う: {parsed.LayerId}");
                Assert(parsed.Direction == Core.Net.WfpDirection.Outbound, "向きが違う");
            }
            finally
            {
                Marshal.FreeHGlobal(app);
                Marshal.FreeHGlobal(drop);
                Marshal.FreeHGlobal(item);
            }
        });

        Check("WFP: エンジンを開いて記録設定を読める(管理者のときのみ)", () =>
        {
            // ここで FwpmEngineSetOption0 を呼んではいけない。CI ランナーは管理者なので
            // 本当にランナーのシステム設定が変わり、途中で落ちれば戻らない
            if (!Services.NetTraceSession.IsAdministrator)
            {
                log.AppendLine("        管理者ではないため省略");
                return;
            }

            (bool opened, int valueType, uint collect) = Interop.WfpNativeMethods.InspectOption();
            Assert(opened, "WFP エンジンを開けない");

            // FWP_VALUE0 の先頭は FWP_DATA_TYPE。UINT32(3) 以外ならこの構造体の理解が違う
            Assert(valueType == 3, $"FWP_VALUE0 の型が UINT32(3) でない: {valueType}");
            log.AppendLine($"        記録: {(collect != 0 ? "有効" : "無効")}");
        });

        Check("WFP: 遮断イベントを列挙できる(管理者のときのみ・件数は問わない)", () =>
        {
            if (!Services.NetTraceSession.IsAdministrator)
            {
                log.AppendLine("        管理者ではないため省略");
                return;
            }

            // 件数は合否に含めない(記録が無効なら 0 件が正常)。ここを通ること自体が、
            // レイアウト誤りによる AccessViolation が起きていない証拠になる
            Interop.WfpReadResult result = Interop.WfpNativeMethods.Read(200);

            Assert(result.Error is null, $"列挙に失敗: {result.Error}");
            log.AppendLine($"        全 {result.TotalSeen} 件中 遮断 {result.Events.Count} 件"
                         + $" / 記録: {(result.CollectOption != 0 ? "有効" : "無効")}");
            log.AppendLine($"        アプリ情報あり {result.WithAppId} 件"
                         + $" / 見たフラグ 0x{result.FlagsSeen:X4}"
                         + $" / 未知のフラグで捨てた {result.SkippedByFlags} 件");

            foreach (Core.Net.WfpBlockedEvent e in result.Events.Take(3))
                log.AppendLine($"        {e.TimeUtc:HH:mm:ss} {e.Protocol} {e.Remote}:{e.RemotePort} {e.AppIdRaw}");
        });

        Check("遮断: プロセスが取れない理由を切り分けられる", () =>
        {
            // 「—」は「アプリ情報が付いていない」で、「⚠ 読み取れず」とは別。
            // 原因が Windows 側かこちらの読み違いかはフラグでしか分けられないので、
            // その分岐だけは実機の状態に依らず確かめておく。
            //
            // WfpNativeMethods.Read は呼ばない（外に触れずに判定だけ試す）
            var one = new Core.Net.WfpBlockedEvent(
                TimeUtc: DateTime.UtcNow,
                Direction: Core.Net.WfpDirection.Outbound,
                Protocol: 6,
                Local: IPAddress.Loopback,
                LocalPort: 1,
                Remote: IPAddress.Loopback,
                RemotePort: 2,
                ScopeId: 0,
                AppIdRaw: "",
                FilterId: 1,
                LayerId: 2,
                IsLoopback: true);

            string Notice(uint flagsSeen, bool inbound, byte protocol = 6)
            {
                Core.Net.WfpBlockedEvent e = one with
                {
                    Direction = inbound ? Core.Net.WfpDirection.Inbound : Core.Net.WfpDirection.Outbound,
                    Protocol = protocol,
                };

                // アプリ情報あり 0 件の場合だけを見る（1 件でもあれば案内は出ない）
                return ViewModels.WfpViewModel.DescribeAppIdGapForTest(
                    new Interop.WfpReadResult([e], 1, 1, null, WithAppId: 0, flagsSeen, 0));
            }

            // 1 件でも取れていれば案内は出さない
            Assert(ViewModels.WfpViewModel.DescribeAppIdGapForTest(
                       new Interop.WfpReadResult([one], 1, 1, null, WithAppId: 1, 0x013F, 0)).Length == 0,
                   "取れている環境で案内が出ている");

            // 実機で踏んだ形。0x011F はパケットの情報だけで、アプリ(0x20)もユーザー(0x40)も無い。
            // 遮断がすべて受信なら、持ち主のアプリが存在しないので当然
            Assert(Notice(0x011F, inbound: true).Contains("すべて受信", StringComparison.Ordinal),
                   "受信だけなのに別の理由を出している");

            // 送信でも ICMP はソケットを使わないので、アプリが存在しない（実機で踏んだ形）
            Assert(Notice(0x011F, inbound: false, protocol: 1)
                       .Contains("ソケットを使わない", StringComparison.Ordinal),
                   "ICMP の送信を「おかしい」と言っている");

            // TCP/UDP の送信が落ちているのに分からないなら、それは環境の話
            Assert(Notice(0x011F, inbound: false, protocol: 6)
                       .Contains("TCP/UDP の送信が", StringComparison.Ordinal),
                   "TCP の送信が落ちているのに理由を濁している");

            // フラグが立っているのに 1 件も読めない = こちらの読み方が悪い
            Assert(Notice(0x013F, inbound: true).Contains("読み出せていません", StringComparison.Ordinal),
                   "フラグが立っているのに Windows のせいにしている");
        });

        Check("ETW 通信量セッションを開始して停止できる(管理者のときのみ)", () =>
        {
            // 非管理者では ETW のカーネルネットワークイベントを購読できない。
            // その経路(案内表示への縮退)は CI では踏めないので、実機で確認する
            if (!Services.NetTraceSession.IsAdministrator)
            {
                log.AppendLine("        管理者ではないため省略");
                return;
            }

            var aggregator = new Core.Net.TrafficAggregator();
            using var session = new Services.NetTraceSession(aggregator);
            Assert(session.Start(), $"ETW セッションを開始できない: {session.FailureMessage}");

            // ループバックで自前のトラフィックを流す。ここが通れば EVENT_RECORD の
            // レイアウト誤り(catch できない AccessViolation)は起きていない。
            // カーネルバッファのフラッシュは 1 秒周期なので、件数は合否に含めない
            Task.Run(async () =>
            {
                using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using System.Net.Sockets.TcpClient server = await listener.AcceptTcpClientAsync();

                byte[] payload = new byte[64 * 1024];
                await client.GetStream().WriteAsync(payload);
                await client.GetStream().FlushAsync();
                await Task.Delay(2000);
            }).GetAwaiter().GetResult();

            Dictionary<Core.Net.FlowKey, Core.Net.FlowTotals> drained = aggregator.Drain();
            long bytes = drained.Values.Sum(t => t.Sent + t.Received);
            log.AppendLine($"        {drained.Count} フロー / {bytes:N0} バイト");

            session.Stop();
            Assert(!session.IsRunning, "ETW セッションが停止していない");
        });

        Check("FTP サーバを起動して停止できる", () =>
        {
            // 実際の転送は CI では確かめられない。ここで見たいのは
            // 待受の開始と後始末が例外なく通ること。ポート 0 で衝突を避ける
            string root = Path.Combine(Path.GetTempPath(), $"pingwatcher-ftp-{Guid.NewGuid():N}");
            using var server = new Services.FtpServer(root);
            server.Start(0);
            Assert(server.IsRunning, "FTP サーバが起動していない");
            server.Stop();
            Assert(!server.IsRunning, "FTP サーバが停止していない");
        });

        Check("TFTP サーバを起動して停止できる", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), $"pingwatcher-tftp-{Guid.NewGuid():N}");
            using var server = new Services.TftpServer(root);
            server.Start(0);   // ポート 0 で衝突を避ける
            Assert(server.IsRunning, "TFTP サーバが起動していない");
            server.Stop();
            Assert(!server.IsRunning, "TFTP サーバが停止していない");
        });

        Check("SFTP サーバを起動して停止できる", () =>
        {
            // 実際の SSH ハンドシェイクは CI では確かめられない。ここで見たいのは
            // FxSsh が動き、ホスト鍵の生成→待受→停止が通ること
            string root = Path.Combine(Path.GetTempPath(), $"pingwatcher-sftp-{Guid.NewGuid():N}");
            string hostKey = Path.Combine(Path.GetTempPath(), $"pingwatcher-sftp-key-{Guid.NewGuid():N}.txt");
            using var server = new Services.SftpServer(root, hostKey);
            server.Start(0);
            Assert(server.IsRunning, "SFTP サーバが起動していない");
            server.Stop();
            Assert(!server.IsRunning, "SFTP サーバが停止していない");
        });

        Check("syslog サーバを起動して停止できる", () =>
        {
            using var server = new Services.SyslogReceiver();
            server.Start(0);   // ポート 0 で衝突を避ける
            Assert(server.IsRunning, "syslog サーバが起動していない");
            server.Stop();
            Assert(!server.IsRunning, "syslog サーバが停止していない");
        });

        Check("SNMP Trap 受信を起動して停止できる", () =>
        {
            using var server = new Services.SnmpTrapReceiver();
            server.Start(0);
            Assert(server.IsRunning, "Trap 受信が起動していない");
            server.Stop();
            Assert(!server.IsRunning, "Trap 受信が停止していない");
        });

        Check("traceroute を実行できる", () =>
        {
            // ループバック相手なら 1 ホップで届くはず。ここでも見たいのは
            // 経路探索の呼び出しが通ることで、ネットワークの状態ではない。
            IReadOnlyList<Services.TraceHop> hops = Task.Run(() =>
                Services.TraceProbe.TraceAsync(IPAddress.Loopback, 3, 1000, 0, CancellationToken.None))
                .GetAwaiter().GetResult();

            log.AppendLine($"        ループバックへの経路: {hops.Count} ホップ");
        });

        Check("DnsClient がリンクされ、問い合わせを実行できる", () =>
        {
            // 応答が返るかはネットワーク次第なので合否に含めない。
            // ここで見たいのは型のロードと呼び出しが通ること。
            // UI スレッドで待つとデッドロックするので、必ず Task.Run で逃がす。
            Services.DnsLookupResult result = Task.Run(() =>
                Services.DnsProbe.QueryAsync("1.1.1.1", "one.one.one.one", "A", 3000, CancellationToken.None))
                .GetAwaiter().GetResult();

            log.AppendLine($"        1.1.1.1 への問い合わせ: {result.Summary}");
        });

        Check("ipconfig /all を実行して読み取れる", () =>
        {
            Services.CommandCapture capture = Task.Run(() =>
                Services.SystemInfoProbe.GetIpConfigAsync(CancellationToken.None))
                .GetAwaiter().GetResult();

            Assert(capture.Ok, $"取得に失敗しました: {capture.Text}");
            log.AppendLine($"        ipconfig の出力: {capture.Text.Length} 文字 / {capture.Text.Split('\n').Length} 行");
        });

        Check("route print -4 を実行して読み取れる", () =>
        {
            Services.CommandCapture capture = Task.Run(() =>
                Services.SystemInfoProbe.GetRouteTableAsync(CancellationToken.None))
                .GetAwaiter().GetResult();

            Assert(capture.Ok, $"取得に失敗しました: {capture.Text}");
            log.AppendLine($"        route の出力: {capture.Text.Split('\n').Length} 行");
        });

        
        Check("宛先リストを保存して読み戻せる", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"pingwatcher-selftest-{Guid.NewGuid():N}.json");
            try
            {
                var document = new Core.Storage.TargetDocument
                {
                    Targets = [new Core.Models.Target { Host = "127.0.0.1", Comment = "自己診断" }],
                };
                Core.Storage.TargetStore.Save(path, document);

                Core.Storage.TargetDocument loaded = Core.Storage.TargetStore.Load(path, out string? storeError);
                Assert(storeError is null, $"読み込みに失敗: {storeError}");
                Assert(loaded.Targets.Count == 1, "宛先の件数が合わない");
                Assert(loaded.Targets[0].Comment == "自己診断", "日本語のコメントが壊れている");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });

        Check("設定を exe と同じフォルダに置ける", () =>
        {
            string directory = AppData.Directory();

            Assert(System.IO.Directory.Exists(directory), $"保存先が作られていない: {directory}");

            // 実際に書けることまで見る。場所が決まっただけでは保存できるとは限らない
            string probe = AppData.PathOf($"selftest-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "確認");
                Assert(File.ReadAllText(probe) == "確認", "書いた内容を読み戻せない");
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }

            log.AppendLine($"        保存先: {directory}");
            log.AppendLine(AppData.IsBesideExecutable
                ? "        exe と同じフォルダに置いています"
                : "        exe の横に書けないため %APPDATA% へ逃がしています");
        });

        Check("アイコンが exe に埋め込まれている", () =>
        {
            // ApplicationIcon の指定漏れやパスの打ち間違いはビルドを通ってしまう。
            // 実行ファイルから引けるかどうかで確かめる。
            string? path = Environment.ProcessPath;
            Assert(path is not null, "自分の実行ファイルの場所が分からない");

            int count = Interop.NativeMethods.CountIcons(path!);
            Assert(count > 0, "exe にアイコンが入っていない（ApplicationIcon の指定を確認）");

            log.AppendLine($"        exe に入っているアイコン: {count} 個");
        });

        Check("記録をテキストで書き出せる", () =>
        {
            var data = new Core.Reporting.ReportData(
                "自己診断",
                DateTime.Now,
                "備考",
                DateTime.Now,
                1000,
                [("IP", "127.0.0.1")],
                [new Core.Reporting.ReportRow(
                    "127.0.0.1", "127.0.0.1", "1階 EPS", "ICMP",
                    new Core.Metrics.RttStatistics(10, 0, 100, 0, 0, 0, 0, 0),
                    [], "不通", true)],
                IpConfig: "Windows IP 構成",
                Wireless: [("SSID", "office")]);

            string text = Core.Reporting.TextReportWriter.Render(data);

            Assert(text.Contains("[NG]", StringComparison.Ordinal), "応答なしの印が出ていない");
            Assert(text.Contains("1階 EPS", StringComparison.Ordinal), "日本語の備考が落ちている");
            Assert(text.Contains("[ipconfig /all]", StringComparison.Ordinal), "ipconfig の節が無い");
            Assert(text.Contains("[無線 LAN]", StringComparison.Ordinal), "無線の節が無い");

            log.AppendLine($"        テキスト記録: {text.Split('\n').Length} 行");
        });

        Check("埋め込みフォント(Noto Sans JP)が読める", () =>
        {
            // exe に同梱した OTF が FontFamily から引けること。ビルドアクションが
            // Resource でないと 0 件になり、静かに游ゴシックへ落ちる
            var family = new System.Windows.Media.FontFamily(
                new Uri("pack://application:,,,/"), "./Resources/Fonts/#Noto Sans JP");

            bool loaded = family.GetTypefaces().Count > 0;
            Assert(loaded, "Noto Sans JP を埋め込みから引けない（ビルドアクションが Resource か確認）");

            // 実際に日本語グリフで整形できるところまで見る
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var probe = new FormattedText("応答 12.4 ms", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 13, Brushes.Black, 1.0);
            Assert(probe.Width > 0, "埋め込みフォントで整形できない");
        });

        Check("日本語テキストを整形できる(フォント初期化の確認)", () =>
        {
            var text = new FormattedText(
                "応答 12.4 ms ／ ロス 0%",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"),
                13,
                Brushes.Black,
                1.0);
            Assert(text.Width > 0, "整形結果の幅が 0");
        });

        log.AppendLine();
        log.AppendLine(failures.Count == 0
            ? "結果: すべて成功"
            : $"結果: {failures.Count} 件失敗 — {string.Join(", ", failures)}");

        string text = log.ToString();
        Console.WriteLine(text);
        try
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest.log"), text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"selftest.log を書けませんでした: {ex.Message}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// パレット辞書を単独で読み込んで、定義しているキーを列挙する。
    /// マージ済みの Application.Resources 越しでは、どちらのテーマが
    /// そのキーを持っているのか分からないため、直接読む。
    /// </summary>
    /// <summary>
    /// 自己診断用の偽 Cisco 機器。ログインを求め、enable を通し、
    /// ページャ無効化に 1 本だけ応じ、show version に固定の出力を返す。
    ///
    /// <b>Telnet の制御(IAC)もわざと混ぜる</b> — 除去できていなければ
    /// プロンプトの判定が狂って収集が失敗するので、実経路の検査になる。
    /// </summary>
    private static void ServeFakeCisco(System.Net.Sockets.TcpListener listener)
    {
        try
        {
            using System.Net.Sockets.TcpClient client = listener.AcceptTcpClient();
            using System.Net.Sockets.NetworkStream stream = client.GetStream();

            void Send(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            // IAC DO ECHO / IAC WILL SGA を先に投げる(こちらは断るはず)
            stream.Write([255, 253, 1, 255, 251, 3], 0, 6);
            Send("\r\nUser Access Verification\r\n\r\nUsername: ");

            var line = new StringBuilder();
            byte[] buffer = new byte[1024];
            bool loggedIn = false;
            bool enabled = false;
            bool askedEnable = false;

            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return;

                for (int i = 0; i < read; i++)
                {
                    char c = (char)buffer[i];

                    // こちらが返した IAC(こちらは全部断る)は文字として読まない。
                    // 混ぜたままにするとコマンドの先頭に制御バイトが付いて一致しなくなる
                    if (buffer[i] >= 0x80) continue;

                    if (c != '\n')
                    {
                        if (c is not ('\r' or '\0') && !char.IsControl(c)) line.Append(c);
                        continue;
                    }

                    string text = line.ToString().Trim();
                    line.Clear();

                    if (!loggedIn)
                    {
                        if (text == "admin") Send("\r\nPassword: ");
                        else if (text == "pass") { loggedIn = true; Send("\r\nR1>"); }
                        else Send("\r\nUsername: ");
                        continue;
                    }

                    if (!enabled)
                    {
                        if (text == "enable") { askedEnable = true; Send("\r\nPassword: "); continue; }

                        if (askedEnable) { enabled = true; Send("\r\nR1#"); continue; }
                    }

                    string prompt = enabled ? "\r\nR1#" : "\r\nR1>";

                    if (text.StartsWith("terminal ", StringComparison.Ordinal))
                    {
                        Send(text == "terminal length 0" ? prompt : "\r\n% Invalid input detected" + prompt);
                        continue;
                    }

                    Send(text == "show version"
                        ? "\r\nCisco IOS Software, Version 15.2(4)E\r\nuptime is 1 day" + prompt
                        : prompt);
                }
            }
        }
        catch (Exception)
        {
            // 自己診断の相手役なので、切れたら黙って終わる
        }
    }

    /// <summary>
    /// タブを 1 枚ずつ選んで実体化する。<b>束ねたタブ(内側の TabControl)まで再帰する。</b>
    ///
    /// TabControl は選ばれたタブしか実体化しないので、既定の表示だけでは
    /// テンプレート適用時のエラー(リソースキーの打ち間違いなど)を見逃す。
    /// 内側まで下りないと、束ねたタブが丸ごと検査から漏れる。
    ///
    /// <b>無線タブだけは選ばない。</b>選んだ時点で WLAN API に触れる作りで、
    /// 位置情報の同意を求める時機を人の操作に合わせているため。
    /// </summary>
    /// <returns>実体化したタブの枚数。</returns>
    private static int VisitTabs(
        Views.MainWindow window, System.Windows.Controls.TabControl tabs, Action<TabItem> onShown)
    {
        int visited = 0;
        object? original = tabs.SelectedItem;

        foreach (object? item in tabs.Items)
        {
            if (item is not TabItem tab) continue;
            if (ReferenceEquals(tab, window.WifiTab)) continue;

            tabs.SelectedItem = tab;
            window.UpdateLayout();

            visited++;
            onShown(tab);

            // 中身が実体化した後でないと内側の TabControl は見つからない
            foreach (System.Windows.Controls.TabControl inner in FindInnerTabs(tab))
                visited += VisitTabs(window, inner, onShown);
        }

        tabs.SelectedItem = original;
        window.UpdateLayout();

        return visited;
    }

    private static IEnumerable<System.Windows.Controls.TabControl> FindInnerTabs(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);

            if (child is System.Windows.Controls.TabControl inner)
            {
                yield return inner;
                continue;   // その中はこの TabControl 自身の巡回で辿る
            }

            foreach (System.Windows.Controls.TabControl found in FindInnerTabs(child))
                yield return found;
        }
    }

    /// <summary>
    /// 表の見出しの <see cref="System.Windows.Controls.Grid"/> をすべて拾う。
    ///
    /// <b>目印は列幅のつまみ（<c>Tag="テーブル名.列番号"</c> の <see cref="Thumb"/>）。</b>
    /// つまみは見出しにだけ置く決まり（行テンプレートに入れると全行に付く）なので、
    /// これを持つ Grid＝見出し、で一意に見分けられる。
    /// </summary>
    private static IEnumerable<System.Windows.Controls.Grid> FindTableHeaders(DependencyObject root)
    {
        if (root is System.Windows.Controls.Grid grid && HasColumnGrip(grid))
            yield return grid;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            foreach (System.Windows.Controls.Grid found in FindTableHeaders(VisualTreeHelper.GetChild(root, i)))
                yield return found;
        }

        static bool HasColumnGrip(System.Windows.Controls.Grid grid)
        {
            foreach (UIElement child in grid.Children)
            {
                if (child is Thumb { Tag: string tag } && tag.Contains('.'))
                    return true;
            }

            return false;
        }
    }

    private static HashSet<string> KeysOf(string relativeSource)
    {
        var dictionary = new ResourceDictionary { Source = new Uri(relativeSource, UriKind.Relative) };

        return [.. dictionary.Keys.OfType<string>()];
    }
}
