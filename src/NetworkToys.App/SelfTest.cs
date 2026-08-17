using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;   // Thumb（列幅のつまみ）
using System.Windows.Media;
using NetworkToys.Core.Addressing;

namespace NetworkToys.App;

/// <summary>
/// <c>NetworkToys.exe --selftest</c> で走る自己診断。
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

        // ポート 0 では待受先が分からず自分で繋げない。空きを 1 つ借りて番号だけ控える
        static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();

            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        static void TryDelete(string directory)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せなくても検査は済んでいる
            }
        }

        // 検査でウィンドウを開閉するため、最後の 1 枚を閉じた時点でアプリが終わらないようにする
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        log.AppendLine($"NetworkToys selftest  ({DateTime.Now:yyyy/MM/dd HH:mm:ss})");
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

        Check("DockPanel の途中の子に向きを書き忘れていない", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // DockPanel は Dock を書かない子を「左」に置く。上から積むつもりの行で 1 つ
            // 書き忘れると、その行が左端に立ち、以降のすべてが右へずれる
            // （ファイル転送の「接続先 ▾」で実際に起きた。2026-08-17 にユーザーが実機で発見）。
            // 最後の子だけは残りを埋める役なので、書いていなくてよい。
            object? original = window!.MainTabs.SelectedItem;
            var problems = new List<string>();
            int panels = 0;

            VisitTabs(window, window.MainTabs, _ =>
            {
                foreach (System.Windows.Controls.DockPanel panel in FindDockPanels(window))
                {
                    panels++;

                    for (int i = 0; i < panel.Children.Count - 1; i++)
                    {
                        UIElement child = panel.Children[i];

                        if (DependencyPropertyHelper.GetValueSource(
                                child, System.Windows.Controls.DockPanel.DockProperty).BaseValueSource
                            != BaseValueSource.Default)
                        {
                            continue;
                        }

                        problems.Add($"{child.GetType().Name}（{panel.Children.Count} 個中 {i + 1} 番目）");
                    }
                }
            });

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();

            Assert(problems.Count == 0,
                   "DockPanel.Dock を書いていない子がある: " + string.Join(" / ", problems.Distinct()));

            log.AppendLine($"        見合わせた DockPanel: {panels} 個");
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

            // Ping タブを選ぶ = ほかのタブはどれも見えていない状態
            window.PingTab.IsSelected = true;
            window.UpdateLayout();

            // Meraki は入れ子の中。親を開いていないのに「見えている」と誤判定すると、
            // タブを開いただけで本番の API を叩き始める
            System.Windows.Controls.TabItem[] mustBeHidden =
                [window.WifiTab, window.WfpTab, window.ConnectionsTab, window.TraceTab,
                 window.IpConfigTab, window.MerakiTab, window.AciTab, window.WlcTab];

            foreach (System.Windows.Controls.TabItem tab in mustBeHidden)
            {
                Assert(!Views.MainWindow.IsShowing(tab),
                       $"Ping タブを選んでいるのに「{tab.Header}」が見えている扱いになっている");
            }

            Assert(Views.MainWindow.IsShowing(window.PingTab), "選んだタブが見えている扱いにならない");

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("Meraki タブのサブタブをすべて表示できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // サブタブの中身も選ばないと実体化しない(親タブを開くだけでは 1 枚目しか作られない)
            object? original = window!.MainTabs.SelectedItem;

            // Meraki は「調べる」の中にある。先祖ごと開かないと中身が作られない
            Views.MainWindow.Show(window.MerakiTab);

            foreach (object? item in window.MerakiSubTabs.Items)
            {
                ((System.Windows.Controls.TabItem)item).IsSelected = true;
                window.UpdateLayout();
            }

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("ACI タブのサブタブをすべて表示できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            object? original = window!.MainTabs.SelectedItem;

            Views.MainWindow.Show(window.AciTab);

            foreach (object? item in window.AciSubTabs.Items)
            {
                ((System.Windows.Controls.TabItem)item).IsSelected = true;
                window.UpdateLayout();
            }

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("WLC タブのサブタブをすべて表示できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            object? original = window!.MainTabs.SelectedItem;

            Views.MainWindow.Show(window.WlcTab);

            foreach (object? item in window.WlcSubTabs.Items)
            {
                ((System.Windows.Controls.TabItem)item).IsSelected = true;
                window.UpdateLayout();
            }

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("WLC: 資格情報が空では取得できない(CI から WLC を叩かない)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;

            Assert(shell.Wlc.Password.Length == 0, "起動直後にパスワードが入っている");
            Assert(!shell.Wlc.FetchCommand.CanExecute(null), "資格情報が空でも取得できてしまう");
            Assert(!shell.Wlc.FetchRoguesCommand.CanExecute(null), "資格情報が空でも不正 AP を取得できてしまう");

            Assert(!new ViewModels.WlcViewModel().ConfirmFingerprint("test"),
                   "証明書の確認が、結線前から「はい」に倒れている");
        });

        Check("Catalyst Center タブのサブタブをすべて表示できる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            object? original = window!.MainTabs.SelectedItem;

            Views.MainWindow.Show(window.DnacTab);

            foreach (object? item in window.DnacSubTabs.Items)
            {
                ((System.Windows.Controls.TabItem)item).IsSelected = true;
                window.UpdateLayout();
            }

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("Catalyst Center: 資格情報が空では取得できない(CI から実機を叩かない)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;

            Assert(shell.Dnac.Password.Length == 0, "起動直後にパスワードが入っている");
            Assert(!shell.Dnac.FetchClientCommand.CanExecute(null), "資格情報が空でも取得できてしまう");

            Assert(!new ViewModels.DnacViewModel().ConfirmFingerprint("test"),
                   "証明書の確認が、結線前から「はい」に倒れている");
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

        Check("ACI: 資格情報が空では取得できない(CI から APIC を叩かない)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;

            // タブを開くだけでは通信しない作り（OnActivated を持たない）。
            // 念のため、パスワードが空の間はどの取得も動かないことを確かめておく
            Assert(shell.Aci.Password.Length == 0, "起動直後にパスワードが入っている");
            Assert(!shell.Aci.FetchCommand.CanExecute(null), "資格情報が空でも取得できてしまう");
            Assert(!shell.Aci.FetchEndpointsCommand.CanExecute(null),
                   "資格情報が空でもエンドポイントを取得できてしまう");
            Assert(!shell.Aci.ExportTenantCommand.CanExecute(null),
                   "資格情報が空でもテナントの設定を書き出せてしまう");

            // 窓を開くのは画面の仕事。結線し忘れたら「受け入れない」に倒れること
            Assert(!new ViewModels.AciViewModel().ConfirmFingerprint("test"),
                   "証明書の確認が、結線前から「はい」に倒れている");
        });

        Check("ACI: 偽の APIC と login→取得→ページング→logout を往復できる", () =>
        {
            var fake = new FakeApic();

            using (var client = new Services.ApicClient("apic.selftest.invalid", null, fake))
            {
                client.LoginAsync("admin", "pw", "", CancellationToken.None).GetAwaiter().GetResult();

                IReadOnlyList<string> pages = client
                    .ClassAsync("faultInst", Core.Fabric.AciCatalog.FaultFilter, null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                client.LogoutAsync().GetAwaiter().GetResult();

                // totalCount が 3 なので 2 ページ目まで取りに行く
                Assert(pages.Count == 2, $"ページを取り切れていない: {pages.Count} ページ");

                IReadOnlyList<Core.Fabric.AciFaultRow> rows =
                    Core.Fabric.AciCatalog.ParseFaults(Core.Fabric.AciMoReader.Parse(pages));

                Assert(rows.Count == 3, $"行に変換できていない: {rows.Count} 件");
                Assert(rows[0].Severity.StartsWith('✕'), $"重大度が記号付きになっていない: {rows[0].Severity}");
            }

            Assert(fake.Paths.Contains("/api/aaaLogin.json"), "ログインしていない");
            Assert(fake.Paths.Contains("/api/aaaLogout.json"), "ログアウトしていない");

            // 取得のたびにトークンを付け直しているか（Cookie の入れ物に任せていない）
            for (int i = 0; i < fake.Paths.Count; i++)
            {
                if (fake.Paths[i] == "/api/aaaLogin.json") continue;

                Assert(fake.Cookies[i] == "APIC-cookie=T1",
                       $"{fake.Paths[i]} にトークンが付いていない: 「{fake.Cookies[i]}」");
            }

            // 読み取り専用。設定を書く口へは 1 度も行かない
            Assert(fake.Paths.All(p => p.StartsWith("/api/aaa", StringComparison.Ordinal)
                                       || p.StartsWith("/api/node/class/", StringComparison.Ordinal)),
                   $"見覚えのない宛先へ要求している: {string.Join(" / ", fake.Paths)}");
        });

        Check("ACI: テナントの設定を書き出して見比べられる形にできる", () =>
        {
            var fake = new FakeApic();

            using var client = new Services.ApicClient("apic.selftest.invalid", null, fake);

            client.LoginAsync("admin", "pw", "", CancellationToken.None).GetAwaiter().GetResult();

            string json = client
                .SubtreeAsync(Core.Fabric.AciCatalog.TenantExportPath("Prod"), CancellationToken.None)
                .GetAwaiter().GetResult();

            string text = Core.Fabric.AciConfigExport.Render("", Core.Fabric.AciMoReader.Parse(json));

            Assert(text.Contains("fvTenant uni/tn-Prod", StringComparison.Ordinal),
                   "テナントの設定を行にできていない");
            Assert(text.Contains("fvBD uni/tn-Prod/BD-Web", StringComparison.Ordinal),
                   "子のオブジェクトが出ていない");

            // これが抜けると稼働値まで混ざり、設定を変えていなくても差分になる
            Assert(fake.Paths.Any(p => p.Contains("rsp-prop-include=config-only", StringComparison.Ordinal)),
                   $"設定だけを求めていない: {string.Join(" / ", fake.Paths)}");

            // 枝を丸ごと返す問い合わせ。ページを重ねない
            Assert(!fake.Paths.Any(p => p.Contains("/api/mo/", StringComparison.Ordinal)
                                        && p.Contains("page=", StringComparison.Ordinal)),
                   "書き出しにページングを重ねている");
        });

        Check("ACI: 証明書を受け入れていない相手には繋がない", () =>
        {
            using var client = new Services.ApicClient("apic.selftest.invalid", null, new RefusingHost());

            bool refused = false;

            try
            {
                client.LoginAsync("admin", "pw", "", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Services.PinnedCertificateException)
            {
                refused = true;
            }
            catch (Services.ApicApiException)
            {
                // 証明書の失敗として扱えていない
            }

            Assert(refused, "TLS で断られたのに、証明書の失敗として扱っていない");
        });

        Check("WLC: 偽の WLC と RESTCONF を往復できる", () =>
        {
            var fake = new FakeWlc();

            using (var client = new Services.WlcClient(
                       "wlc.selftest.invalid", "admin", "pw", null, fake))
            {
                string clients = client
                    .GetAsync(Core.Wireless.WlcCatalog.ClientCommonPaths[0], CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(Core.Wireless.WlcYang.Rows(clients).Count == 1,
                       "クライアントの応答を読めていない");

                // モジュール名は版で割れる。1 本目が 404 なら 2 本目へ行くこと
                string rrm = client
                    .GetFirstAsync(Core.Wireless.WlcCatalog.RrmPaths, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(Core.Wireless.WlcYang.Rows(rrm).Count == 1, "候補の 2 本目へ進んでいない");

                // 204 No Content は「0 件」であって失敗ではない
                string empty = client
                    .GetAsync(Core.Wireless.WlcCatalog.ApTagPaths[0], CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(Core.Wireless.WlcYang.Rows(empty).Count == 0, "204 を 0 件として扱えていない");
            }

            Assert(fake.Paths.Count >= 4, $"要求が少なすぎる: {fake.Paths.Count} 本");

            // 読み取り専用。GET 以外は 1 度も出さない
            Assert(fake.Methods.All(m => m == "GET"),
                   $"GET 以外を投げている: {string.Join(" / ", fake.Methods.Distinct())}");

            Assert(fake.Paths.All(p => p.StartsWith("/restconf/data/", StringComparison.Ordinal)),
                   $"RESTCONF 以外の宛先へ行っている: {string.Join(" / ", fake.Paths)}");

            // Basic 認証と Accept は毎回付く（Accept が無いと実機は 406 を返す）
            Assert(fake.Authorizations.All(a => a.StartsWith("Basic ", StringComparison.Ordinal)),
                   "Basic 認証が付いていない要求がある");

            Assert(fake.Accepts.All(a => a.Contains("yang-data+json", StringComparison.Ordinal)),
                   $"Accept が RESTCONF の形式になっていない: {string.Join(" / ", fake.Accepts.Distinct())}");
        });

        Check("WLC: 証明書を受け入れていない相手には繋がない", () =>
        {
            using var client = new Services.WlcClient(
                "wlc.selftest.invalid", "admin", "pw", null, new RefusingHost());

            bool refused = false;

            try
            {
                client.GetAsync(Core.Wireless.WlcCatalog.ApCapwapPaths[0], CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Services.PinnedCertificateException)
            {
                refused = true;
            }
            catch (Services.WlcApiException)
            {
                // 証明書の失敗として扱えていない
            }

            Assert(refused, "TLS で断られたのに、証明書の失敗として扱っていない");
        });

        Check("Catalyst Center: 偽の Catalyst Center と token→取得→ページングを往復できる", () =>
        {
            var fake = new FakeDnac();

            using (var client = new Services.DnacClient("dnac.selftest.invalid", null, fake))
            {
                client.LoginAsync("admin", "pw", CancellationToken.None).GetAwaiter().GetResult();

                string enrichment = client
                    .ClientAsync("ip_address", "10.10.22.98", CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(Core.Assurance.DnacCatalog.ParseConnections(
                           Core.Assurance.DnacJson.Rows(enrichment)).Count == 1,
                       "端末の応答を行にできていない");

                IReadOnlyList<string> pages = client.DevicesAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(pages.Count == 2, $"ページを取り切れていない: {pages.Count} ページ");

                // 版が無い機能は 404。次の候補へ進めること
                string health = client
                    .GetFirstAsync(Core.Assurance.DnacCatalog.DeviceHealthPaths, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(Core.Assurance.DnacJson.Rows(health).Count == 1, "候補の 2 本目へ進んでいない");
            }

            Assert(fake.Paths[0] == Core.Assurance.DnacCatalog.TokenPath, "先にログインしていない");

            // ログインだけが Basic。以後は毎回 X-Auth-Token を自分で付ける
            for (int i = 1; i < fake.Paths.Count; i++)
            {
                Assert(fake.Tokens[i] == "T1", $"{fake.Paths[i]} にトークンが付いていない");
                Assert(fake.Authorizations[i].Length == 0,
                       $"{fake.Paths[i]} に Basic 認証が残っている");
            }

            Assert(fake.Authorizations[0].StartsWith("Basic ", StringComparison.Ordinal),
                   "ログインが Basic 認証になっていない");

            // 端末の絞り込みはクエリではなくヘッダで渡す
            Assert(fake.EntityTypes.Contains("ip_address"), "entity_type をヘッダで渡していない");
            Assert(fake.EntityValues.Contains("10.10.22.98"), "entity_value をヘッダで渡していない");

            // offset は 1 始まり。0 を渡すと版によっては何も返らない
            Assert(fake.Paths.Any(p => p.Contains("offset=1&", StringComparison.Ordinal)),
                   $"機器一覧の offset が 1 始まりになっていない: {string.Join(" / ", fake.Paths)}");
            Assert(fake.Paths.Any(p => p.Contains("offset=501&", StringComparison.Ordinal)),
                   "2 ページ目を取りに行っていない");

            // 読み取り専用。POST はトークンの取得だけで、宛先は /dna/ の下だけ
            for (int i = 0; i < fake.Paths.Count; i++)
            {
                Assert(fake.Methods[i] == "GET" || fake.Paths[i] == Core.Assurance.DnacCatalog.TokenPath
                       || fake.Paths[i] == Core.Assurance.DnacCatalog.ReadRequestPath,
                       $"想定していない書き込みをしている: {fake.Methods[i]} {fake.Paths[i]}");

                Assert(fake.Paths[i].StartsWith("/dna/", StringComparison.Ordinal),
                       $"見覚えのない宛先へ要求している: {fake.Paths[i]}");
            }
        });

        Check("Catalyst Center: 証明書を受け入れていない相手には繋がない", () =>
        {
            using var client = new Services.DnacClient("dnac.selftest.invalid", null, new RefusingHost());

            bool refused = false;

            try
            {
                client.LoginAsync("admin", "pw", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Services.PinnedCertificateException)
            {
                refused = true;
            }
            catch (Services.DnacApiException)
            {
                // 証明書の失敗として扱えていない
            }

            Assert(refused, "TLS で断られたのに、証明書の失敗として扱っていない");
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

        Check("差分比較: 結果を出していないときは「貼り付けに戻る」を押せない", () =>
        {
            // 常に押せるままだと、貼り付け欄を出している間に押しても何も起きず、
            // 「押しても効かない」と読まれる（2026-08-17 にそう報告された）
            var compare = new ViewModels.DeviceCompareViewModel();

            Assert(compare.IsEditing, "起動直後に貼り付け欄が出ていない");
            Assert(!compare.EditCommand.CanExecute(null), "結果が無いのに「貼り付けに戻る」を押せる");

            compare.BeforeText = "hostname a";
            compare.AfterText = "hostname b";
            compare.CompareCommand.Execute(null);

            Assert(!compare.IsEditing, "比較しても結果に切り替わらない");
            Assert(compare.EditCommand.CanExecute(null), "結果を出しているのに戻れない");

            compare.EditCommand.Execute(null);

            Assert(compare.IsEditing, "「貼り付けに戻る」で貼り付け欄に戻らない");
            Assert(!compare.EditCommand.CanExecute(null), "戻った後もまだ押せる");
        });

        Check("文字の上を叩いても落ちない（Run は Visual ではない）", () =>
        {
            // 差分の色分けは TextBlock の中に Run を並べる。その上でマウスを押すと
            // OriginalSource が Run になり、VisualTreeHelper.GetParent に渡した瞬間に
            // InvalidOperationException でアプリごと落ちた（2026-08-17 のクラッシュ）。
            // たどり方 1 か所に閉じてあるので、ここが通れば全部の経路が通る。
            var block = new System.Windows.Controls.TextBlock();
            var run = new System.Windows.Documents.Run("差分の一部");

            block.Inlines.Add(run);

            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(block);

            DependencyObject? parent = Views.MainWindow.ClickedParentOf(run);

            Assert(ReferenceEquals(parent, block), "Run の親が、載っている TextBlock にならない");

            // そこから先は視覚の親でたどれる（＝上へ抜けられる）
            Assert(ReferenceEquals(Views.MainWindow.ClickedParentOf(block), panel),
                   "TextBlock から上へたどれない");
        });

        Check("差分比較: 機器から取ってくる小窓を表示できる", () =>
        {
            // 窓はコードで組んでいるので、リソースキーの打ち間違いは実行時にしか出ない。
            // 実際に表示まで通す。ここでは 1 バイトも外へ出さない（「取得」は押さない）
            var dialog = new Views.DeviceFetchDialog("検査用", "show version");
            dialog.Show();
            dialog.UpdateLayout();

            Assert(dialog.ActualWidth > 0 && dialog.ActualHeight > 0, "小窓の実サイズが 0");
            dialog.Close();
        });

        Check("差分比較: 比較する対象に合わせた show が既定になる", () =>
        {
            Assert(Core.Work.DeviceComparison.CommandFor(Core.Work.DeviceOutputKind.Configuration)
                       == "show running-config",
                   "show run の既定が違う");

            Assert(Core.Work.DeviceComparison.CommandFor(Core.Work.DeviceOutputKind.RouteTable)
                       == "show ip route",
                   "show ip route の既定が違う");

            // 「そのまま比較」には決まった形が無い。勝手に何かを流さない
            Assert(Core.Work.DeviceComparison.CommandFor(Core.Work.DeviceOutputKind.PlainText).Length == 0,
                   "そのまま比較に既定のコマンドを入れている");
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
                    "本社", "pc-01", "192.168.128.50", "aa:bb:cc:dd:ee:ff", "10", "Dell", "1 MB", "10:00"),
                 "192.168.128.50"),

                (new Core.Fabric.AciEndpointRow(
                    "00:50:56:AA:BB:CC", "192.168.10.50", "Prod", "Web", "vlan-100",
                    "101", "eth1/1"), "192.168.10.50"),

                (new Core.Wireless.WlcClientRow(
                    "aabb.ccdd.eeff", "192.168.20.31", "Apple", "AP-1F-01", "Corp", "5GHz ch36",
                    -58, "-58", "良い", "34", "866", "● 通信中", Core.Design.SeverityKind.Ok, ""), "192.168.20.31"),

                (new Core.Assurance.DnacConnectionRow(
                    "AA:BB:CC:DD:EE:01", "10.10.22.98", "pc-1234", "有線", "sw-3f-01", "Gi1/0/13",
                    "550", "", "", "10 ● 良好", Core.Design.SeverityKind.Ok, "Global/Tokyo/3F",
                    "2026-08-17 10:00:00"), "10.10.22.98"),

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

        Check("ライセンス表示が exe に埋め込まれている", () =>
        {
            // 配布物からテキストが落ちても読めるように、exe にも入れてある。
            // MIT / Apache-2.0 / OFL 1.1 は再配布時の添付を条件にしている
            string? notices = Views.MainWindow.ReadNotices();

            Assert(notices is { Length: > 2000 },
                   $"ライセンス表示を読めない（{notices?.Length ?? -1} 文字）");
            Assert(notices!.Contains("Noto Sans JP", StringComparison.Ordinal),
                   "同梱フォントの表示が入っていない");
            Assert(notices.Contains("Permission is hereby granted", StringComparison.Ordinal),
                   "MIT の本文が入っていない");
        });

        Check("文字サイズを変えられる", () =>
        {
            // 画面側は寸法をすべて DynamicResource で引いているので、
            // Application.Resources に入れれば全画面へ伝わる。
            // ここでは「入れた値が実際に引ける」ところまでを見る
            double body = (double)Application.Current.TryFindResource("Size.Body")!;
            double row = (double)Application.Current.TryFindResource("Size.RowHeight")!;

            try
            {
                UiScale.Apply(1.5);

                double bigBody = (double)Application.Current.TryFindResource("Size.Body")!;
                double bigRow = (double)Application.Current.TryFindResource("Size.RowHeight")!;

                Assert(bigBody > body, $"文字が大きくならない: {body} → {bigBody}");
                Assert(bigRow > row, $"行の高さが追随しない: {row} → {bigRow}");

                // ツールチップの幅は本文の大きさから出す。
                // 直書きのままだと「全角 68 字」の決まりが倍率で崩れる
                double width = (double)Application.Current.TryFindResource("Size.TooltipWidth")!;

                Assert(Math.Abs(width - (bigBody * 68 + 24)) < 0.5,
                       $"ツールチップの幅が本文の大きさに追随しない: {width}");

                UiScale.Apply(0.85);
                Assert((double)Application.Current.TryFindResource("Size.Body")! < body,
                       "小さくできない");
            }
            finally
            {
                // 後続の寸法検査（既定幅・見出しの余白・ⓘ の 68 字）を壊さないよう必ず戻す
                UiScale.Apply(1.0);
            }

            Assert(Math.Abs((double)Application.Current.TryFindResource("Size.Body")! - body) < 0.001,
                   "標準に戻したのに元の大きさへ戻らない");
        });

        Check("読めない文字サイズは標準に落ちる", () =>
        {
            // settings.json は手で書き換えられるし、版が変われば壊れた値も入りうる
            foreach (double bad in new[] { 0, -1, 99, double.NaN })
            {
                UiScale.Apply(bad);

                Assert(UiScale.Is(1.0), $"{bad} を渡したのに標準にならない（いま {UiScale.Current}）");
            }
        });

        Check("文字サイズを変えると列幅も比例する", () =>
        {
            // 文字だけ大きくすると、幅がピクセルで決まっている列で文字が切れる
            ViewModels.TableColumns tables = ViewModels.TableColumns.Instance;
            ViewModels.ColumnLayout layout = ViewModels.ColumnLayout.Instance;

            tables.Reset();
            layout.Reset();

            double conn = tables["conn.1"].Value;
            double target = layout.Target.Value;

            try
            {
                // 比例させるのは MainWindow が UiScale.Changed を拾ってやる。
                // ここで自分でも Scale を呼ぶと二重にかかるので、実際の道だけを通す
                UiScale.Apply(1.5);

                Assert(tables["conn.1"].Value > conn, $"表の列幅が追随しない: {conn}");
                Assert(layout.Target.Value > target, $"Ping の列幅が追随しない: {target}");

                // 戻したときに元の幅へ戻ること（丸めで少しずれるので幅を持たせる）
                UiScale.Apply(1.0);

                Assert(Math.Abs(tables["conn.1"].Value - conn) <= 1,
                       $"戻しても元の幅にならない: {conn} → {tables["conn.1"].Value}");
            }
            finally
            {
                UiScale.Apply(1.0);
                tables.Reset();
                layout.Reset();
            }
        });

        Check("列幅を既定に戻せる", () =>
        {
            // ドラッグで崩したときの逃げ道。これが無いと settings.json を直接編集するしかない
            ViewModels.ColumnLayout layout = ViewModels.ColumnLayout.Instance;
            ViewModels.TableColumns tables = ViewModels.TableColumns.Instance;

            double stateWidth = layout.State.Value;
            double connWidth = tables["conn.0"].Value;

            layout.State = new System.Windows.GridLength(stateWidth + 40);
            tables.Drag("conn.0", 30);

            Assert(layout.State.Value != stateWidth, "列幅を変えられていない（検査が成立しない）");
            Assert(tables["conn.0"].Value != connWidth, "表の列幅を変えられていない（検査が成立しない）");

            layout.Reset();
            tables.Reset();

            Assert(layout.State.Value == 84, $"Ping の列幅が既定に戻らない: {layout.State.Value}");
            Assert(tables["conn.0"].Value == 56, $"接続の列幅が既定に戻らない: {tables["conn.0"].Value}");
        });

        Check("最前面固定を覚えている", () =>
        {
            // メニューにチェック項目があるのに保存しておらず、毎回外れていた
            bool original = Settings.Current.Topmost;

            try
            {
                Settings.Current.Topmost = true;

                Assert(Settings.Current.Topmost,
                       "設定に最前面固定の入れ物が無い");
            }
            finally
            {
                Settings.Current.Topmost = original;
            }
        });

        Check("タブ: どのタブにも 1 行の説明と ⓘ がある", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // タブ名だけでは何の画面か分からない（WFP がまさにそれだった）。
            // 足し忘れは黙って進むので、ここで全タブぶん突き合わせる
            var missing = new List<string>();

            foreach (System.Windows.Controls.TabItem tab in AllTabs(window!.MainTabs))
            {
                // 「その他」は中身を選ぶだけの入れ物で、説明する内容が無い
                // （2026-08-16 ユーザー指示で説明と ⓘ を外した）
                if (ReferenceEquals(tab, window.OtherTab)) continue;

                string header = tab.Header?.ToString() ?? "";
                string name = header.Split('　')[0];   // 「その他　16 ▾」→「その他」

                // ⓘ は Style ごと引くので、Text ではなく ToolTip の有無で見る
                System.Windows.Controls.TextBlock[] blocks =
                    [.. FindIn<System.Windows.Controls.TextBlock>(tab.Content)];

                if (blocks.FirstOrDefault(t => Equals(t.Text, "ⓘ"))?.ToolTip
                    is not string { Length: > 30 } help)
                {
                    missing.Add(header);
                    continue;
                }

                // 地の文に戻っていないこと。145 文字の 1 段落は、
                // ホバーしている間に読み切れないというのが出発点だった
                foreach (string problem in CheckHelpShape(name, help))
                    missing.Add(problem);

                // 1 行の説明は「その画面で何ができるか」を一言で。
                // 短すぎると伝わらないが、長いと 1 行の役目を外れる
                // （細かい話は ⓘ に置く。以前は 100 文字を超えていて読まれなかった）。
                if (blocks.FirstOrDefault(t => t.Style == tab.TryFindResource("TabIntro") as Style)
                    is not { Text: { } intro })
                {
                    missing.Add($"{header}（1 行の説明が無い）");
                }
                else if (Core.Reporting.TextWidth.Of(intro) is < 20 or > 100)
                {
                    int chars = Core.Reporting.TextWidth.Of(intro) / 2;
                    missing.Add($"{header}（1 行の説明が {chars} 字。10〜50 字に収めること）");
                }
            }

            Assert(missing.Count == 0,
                   $"説明が無いタブ: {string.Join(" / ", missing)}");
        });

        Check("showコマンド整形: 保存ボタンが 2 つとも VM に繋がっている", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            System.Windows.Controls.TabItem? tab = AllTabs(window!.MainTabs)
                .FirstOrDefault(t => Equals(t.Header, "showコマンド整形"));

            Assert(tab is not null, "showコマンド整形タブが見つからない");

            // 束縛先の綴りを間違えてもビルドも起動も通り、「押せないボタン」として
            // 黙って残る。実際に押せるかは実機でしか見られないので、
            // せめて束縛先の名前が VM にあることだけは機械で突き合わせる
            foreach (string label in new[] { "Excel で保存", "CSV で保存" })
            {
                System.Windows.Controls.Button? button =
                    FindIn<System.Windows.Controls.Button>(tab!.Content)
                        .FirstOrDefault(b => Equals(b.Content, label));

                Assert(button is not null, $"「{label}」ボタンが無い");

                string? path = System.Windows.Data.BindingOperations
                    .GetBinding(button!, ButtonBase.CommandProperty)?.Path.Path;

                Assert(path is not null, $"「{label}」に Command の束縛が無い");
                Assert(typeof(ViewModels.ConvertViewModel).GetProperty(path!) is not null,
                       $"「{label}」の束縛先 {path} が ConvertViewModel に無い");
            }
        });

        Check("編集できる ComboBox を置いていない", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // ComboBox のテンプレートを置き換えてあり、PART_EditableTextBox を持たない。
            // IsEditable にすると入力欄が描かれず、選んでも表示が空のままになる
            var editable = new List<string>();

            foreach (System.Windows.Controls.TabItem tab in AllTabs(window!.MainTabs))
            {
                foreach (System.Windows.Controls.ComboBox box in
                         FindIn<System.Windows.Controls.ComboBox>(tab.Content))
                {
                    if (box.IsEditable) editable.Add(tab.Header?.ToString() ?? "?");
                }
            }

            Assert(editable.Count == 0,
                   $"編集できる ComboBox がある（素の TextBox にすること）: {string.Join(" / ", editable.Distinct())}");
        });

        Check("タブ: まとめた中身は分類されている", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // メニューは Tag で分類を作る。付け忘れると見出しの無い 15 連になる
            System.Windows.Controls.TabControl[] inner =
                [.. FindInnerTabsInContent(window!.OtherTab.Content)];

            Assert(inner.Length == 1, "その他タブの中身が見つからない");

            string[] tags =
                [.. inner[0].Items.OfType<System.Windows.Controls.TabItem>()
                     .Select(t => t.Tag as string ?? "")];

            Assert(tags.All(t => t.Length > 0), "分類（Tag）の無い項目がある");

            // 同じ分類が離れて出てくると、メニューに同じ見出しが 2 度出る
            string[] order = [.. tags.Distinct()];

            Assert(order.Length == tags.Distinct().Count() && IsGrouped(tags),
                   $"同じ分類が離れて並んでいる: {string.Join(" / ", tags)}");
        });

        Check("タブ: まとめた先のタブへ移動でき、閉じれば止まる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            object? original = window!.MainTabs.SelectedItem;

            // 右クリックからの遷移と同じ道。まとめたタブの中にある画面を名指しで開く
            Views.MainWindow.Show(window.TraceTab);
            window.UpdateLayout();

            Assert(Views.MainWindow.IsShowing(window.TraceTab),
                   "まとめた先のタブへ Show しても、親タブが開かず画面が変わらない");

            // 開いたまま別のタブへ移ったら、もう「見えている」ことにしない。
            // ここが崩れると、見えていないタブが 60 秒ごとに traceroute を打ち続ける
            window.PingTab.IsSelected = true;
            window.UpdateLayout();

            Assert(!Views.MainWindow.IsShowing(window.TraceTab),
                   "まとめたタブを閉じたのに、中のタブが見えている扱いのままになっている");

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
        });

        Check("タブ: まとめたタブの見出しの件数が中身と合っている", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // 見出しの「8 ▾」は XAML に直書きなので、中身を増減すると黙ってずれる。
            // 数字が嘘をつくくらいなら書かない方がましなので、ここで突き合わせる。
            //
            // 数えるのは論理ツリー。視覚ツリーだと「選ばれているタブの中身」しか
            // 実体化していないので、1 枚ずつ開いて回らないと数えられない
            int grouped = 0;

            foreach (object? item in window!.MainTabs.Items)
            {
                if (item is not System.Windows.Controls.TabItem tab) continue;

                string header = tab.Header?.ToString() ?? "";
                System.Windows.Controls.TabControl[] inner = [.. FindInnerTabsInContent(tab.Content)];

                if (header.Contains('▾', StringComparison.Ordinal))
                {
                    Assert(inner.Length > 0, $"「{header}」は ▾ が付いているのに中身が無い");

                    int actual = inner[0].Items.Count;
                    string want = actual.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    Assert(header.Contains(want + " ▾", StringComparison.Ordinal),
                           $"「{header}」の見出しの件数が中身({actual} 本)と合っていない");

                    // 帯には「いま開いているもの」だけを出す決まり。
                    // 16 枚を素で並べると名前が読めない（ユーザー指示）
                    Assert(inner[0].ItemContainerStyle is { } style
                           && style.Setters.OfType<Setter>().Any(x =>
                                  x.Property == UIElement.VisibilityProperty
                                  && Equals(x.Value, Visibility.Collapsed)),
                           $"「{header}」の帯が、選んでいないものまで出す作りになっている");

                    // 見た目は主タブと揃える。BasedOn を外すとそこだけ別物に見える
                    Assert(inner[0].ItemContainerStyle.BasedOn is not null,
                           $"「{header}」の帯が主タブと同じ見た目になっていない");

                    grouped++;
                }
                else
                {
                    // 逆も守る。中に切り替えを持つのに ▾ が無いと、まとめていることに気づけない
                    Assert(inner.Length == 0,
                           $"「{header}」は中に切り替えを持つのに ▾ が付いていない");
                }
            }

            Assert(grouped == 1, $"まとめたタブが 1 枚のはずが {grouped} 枚になっている");
        });

        Check("試験: ひな型を自分で作って残せる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;
            int builtin = shell.Verify.Templates.Count;

            const string name = "自己診断のひな型";
            const string text = "名前が引ける,DNS,www.example.jp";

            try
            {
                shell.Verify.SaveTemplate(name, text);

                Assert(shell.Verify.Templates.Count == builtin + 1,
                       "残したひな型が選択肢に出てこない");
                Assert(shell.Verify.SelectedTemplate.Name == name,
                       "残した直後に、そのひな型が選ばれていない");
                Assert(shell.Verify.SelectedTemplate.IsMine,
                       "自作のひな型が組み込み扱いになっている");

                // 組み込みは消せないこと（消せると次の起動で戻ってきて混乱する）
                Assert(!shell.Verify.Templates[0].IsMine, "組み込みのひな型が自作扱いになっている");

                shell.Verify.DeleteTemplateCommand.Execute(null);

                Assert(shell.Verify.Templates.Count == builtin, "消したのに選択肢が減っていない");
            }
            finally
            {
                Settings.Current.VerifyTemplates.Remove(name);
            }
        });

        Check("試験: 放り込んだテキストから項目を読める", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;

            shell.Verify.LoadItemsFrom("社内ポータル,HTTP,http://portal.example.jp/", "落としたファイル");

            Assert(shell.Verify.Rows.Count == 1, $"1 件のはずが {shell.Verify.Rows.Count} 件");
            Assert(shell.Verify.Rows[0].Name == "社内ポータル", "項目名が読めていない");

            // 読めないものを渡したら、黙って空にしない
            shell.Verify.LoadItemsFrom("", "空のファイル");
            Assert(shell.Verify.Rows.Count == 1, "読めない内容で、いまの項目を消してしまっている");

            shell.Verify.Reset();
        });

        Check("放り込んだファイルの文字コードを見分ける", () =>
        {
            // 機器から落としたログは cp932 のことが多い。UTF-8 として読むと化ける
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes("ホスト名 の設定");

            Assert(Services.DroppedText.Decode(utf8) == "ホスト名 の設定", "UTF-8 を読めていない");

            // UTF-8 として通らないバイト列は cp932 とみなす（Linux では Latin1 に落ちる）
            byte[] broken = [0x83, 0x7A, 0x83, 0x58, 0x83, 0x67];

            Assert(Services.DroppedText.Decode(broken).Length > 0, "読めないバイト列で空になっている");
        });

        Check("試験: 結果を HTML の報告書にできる", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;

            // 実際には走らせない（CI のランナーは外に出られる）。
            // 組み立てだけを確かめたいので、作り物の結果を入れて回収する
            shell.Verify.Results.Add(new Core.Verify.CheckResult(
                "自己診断の項目", Core.Verify.CheckKind.Http, "https://example.jp/", "直接",
                Core.Verify.CheckVerdict.Fail, "接続できませんでした", 12));

            try
            {
                string html = Core.Reporting.HtmlReportWriter.Render(shell.Verify.BuildReport());

                Assert(html.Contains("自己診断の項目", StringComparison.Ordinal), "試験の項目が報告書に出ない");
                Assert(html.Contains("✕ 不合格", StringComparison.Ordinal), "合否が記号と文字で出ていない");

                // 測定が空なので、空の測定表を出さないこと
                Assert(!html.Contains("記録された宛先がありません", StringComparison.Ordinal),
                       "試験だけの報告書に、空の測定表が出ている");

                // 記録タブからも同じ結果が載ること（作業の証跡は 1 つにまとまる）
                Assert(shell.Report.BuildReportForSelfTest().Checks is { Count: > 0 },
                       "ファイルメニューからの書き出しに試験の結果が載っていない");
            }
            finally
            {
                shell.Verify.Reset();
            }
        });

        Check("収集: 宛先リストから備考も取り込む", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            var shell = (ViewModels.ShellViewModel)window!.DataContext;
            int before = shell.Collect.Rows.Count;

            shell.Collect.Import([("selftest-device.example.jp", "1階 EPS")]);

            ViewModels.CollectRowViewModel? added =
                shell.Collect.Rows.FirstOrDefault(r => r.Host == "selftest-device.example.jp");

            Assert(added is not null, "取り込んだ行が見つからない");
            Assert(added!.Memo == "1階 EPS", $"備考が引き継がれていない: 「{added.Memo}」");

            shell.Collect.RemoveDeviceCommand.Execute(added);
            Assert(shell.Collect.Rows.Count == before, "後始末で行数が戻っていない");
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
            string root = Path.Combine(Path.GetTempPath(), $"networktoys-ftp-{Guid.NewGuid():N}");
            using var server = new Services.FtpServer(root);
            server.Start(0);
            Assert(server.IsRunning, "FTP サーバが起動していない");
            server.Stop();
            Assert(!server.IsRunning, "FTP サーバが停止していない");
        });

        Check("TFTP サーバを起動して停止できる", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), $"networktoys-tftp-{Guid.NewGuid():N}");
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
            string root = Path.Combine(Path.GetTempPath(), $"networktoys-sftp-{Guid.NewGuid():N}");
            string hostKey = Path.Combine(Path.GetTempPath(), $"networktoys-sftp-key-{Guid.NewGuid():N}.txt");
            using var server = new Services.SftpServer(root, hostKey);
            server.Start(0);
            Assert(server.IsRunning, "SFTP サーバが起動していない");
            server.Stop();
            Assert(!server.IsRunning, "SFTP サーバが停止していない");
        });

        Check("FTP: 自分のサーバへ自分のクライアントで往復できる", () =>
        {
            // FTP のクライアントは自前なので、ここが唯一の実経路の検査になる。
            // 一覧・取得・送信・フォルダ作成・改名・削除を 1 往復。外へは出ない
            string root = Path.Combine(Path.GetTempPath(), $"networktoys-ftpc-{Guid.NewGuid():N}");
            string work = Path.Combine(Path.GetTempPath(), $"networktoys-ftpc-work-{Guid.NewGuid():N}");

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(work);

            const string body = "hostname RT01\nこんにちは\n";
            File.WriteAllText(Path.Combine(root, "startup-config"), body);

            int port = FreePort();

            try
            {
                using var server = new Services.FtpServer(root, "watcher", "pw");
                server.Start(port);

                using var client = new Services.FtpFileClient();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                Task.Run(async () =>
                {
                    await client.ConnectAsync("127.0.0.1", port, "watcher", "pw", stop.Token);

                    // 一覧（サーバは MLSD を持たないので LIST 経路が通る）
                    IReadOnlyList<Core.Files.RemoteEntry> listed = await client.ListAsync("/", stop.Token);
                    Core.Files.RemoteEntry found = listed.FirstOrDefault(e => e.Name == "startup-config");
                    Assert(found.Name == "startup-config", $"一覧に見つからない: {listed.Count} 件");
                    Assert(!found.IsDirectory, "ファイルがフォルダ扱いになっている");
                    Assert(found.Size > 0, "サイズが 0 のまま");

                    // 取得（中身まで一致すること）
                    string got = Path.Combine(work, "got.txt");
                    var seen = new List<Services.TransferProgress>();
                    await client.DownloadAsync("/startup-config", got,
                                               new Progress<Services.TransferProgress>(seen.Add), stop.Token);
                    Assert(File.ReadAllText(got) == body, "取得した中身が違う");

                    // 送信 → サーバ側の実体で確かめる
                    string put = Path.Combine(work, "put.txt");
                    File.WriteAllText(put, "interface Gi0/1\n");
                    await client.UploadAsync(put, "/uploaded.txt", null, stop.Token);
                    Assert(File.ReadAllText(Path.Combine(root, "uploaded.txt")) == "interface Gi0/1\n",
                           "送ったファイルの中身が違う");

                    // フォルダを作る → 改名 → 消す
                    await client.MakeDirectoryAsync("/backup", stop.Token);
                    Assert(Directory.Exists(Path.Combine(root, "backup")), "フォルダが作られていない");

                    await client.RenameAsync("/uploaded.txt", "/renamed.txt", stop.Token);
                    Assert(File.Exists(Path.Combine(root, "renamed.txt")), "改名されていない");

                    await client.DeleteAsync("/renamed.txt", isDirectory: false, stop.Token);
                    Assert(!File.Exists(Path.Combine(root, "renamed.txt")), "ファイルが消えていない");

                    await client.DeleteAsync("/backup", isDirectory: true, stop.Token);
                    Assert(!Directory.Exists(Path.Combine(root, "backup")), "フォルダが消えていない");

                    log.AppendLine($"        一覧 {listed.Count} 件 / 進捗 {seen.Count} 回");
                }).GetAwaiter().GetResult();
            }
            finally
            {
                TryDelete(root);
                TryDelete(work);
            }
        });

        Check("FTP: 誤ったパスワードは断られる", () =>
        {
            // 認証を通さずに一覧が見えてしまう作りになっていないこと
            string root = Path.Combine(Path.GetTempPath(), $"networktoys-ftpc-auth-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            int port = FreePort();

            try
            {
                using var server = new Services.FtpServer(root, "watcher", "pw");
                server.Start(port);

                using var client = new Services.FtpFileClient();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                bool refused = false;

                try
                {
                    client.ConnectAsync("127.0.0.1", port, "watcher", "ちがう", stop.Token)
                          .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    refused = true;
                }

                Assert(refused, "誤ったパスワードで繋がってしまった");
            }
            finally
            {
                TryDelete(root);
            }
        });

        Check("SFTP: 自分のサーバへ自分のクライアントで往復できる", () =>
        {
            // 偽の Cisco 機器・偽の STUN サーバと同じ手。アプリの中に SFTP サーバが
            // あるので、loopback で立てて自分のクライアントから繋ぐ。
            // 一覧・取得・送信・作成・改名・削除を 1 往復させて実経路を通す。
            // 外へは 1 バイトも出さない
            string root = Path.Combine(Path.GetTempPath(), $"networktoys-sftpc-{Guid.NewGuid():N}");
            string hostKey = Path.Combine(Path.GetTempPath(), $"networktoys-sftpc-key-{Guid.NewGuid():N}.txt");
            string work = Path.Combine(Path.GetTempPath(), $"networktoys-sftpc-work-{Guid.NewGuid():N}");

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(work);

            const string body = "hostname RT01\nこんにちは\n";
            File.WriteAllText(Path.Combine(root, "startup-config"), body);

            int port = FreePort();

            try
            {
                using var server = new Services.SftpServer(root, hostKey, "watcher", "pw");
                server.Start(port);

                using var client = new Services.SftpFileClient();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                Task.Run(async () =>
                {
                    await client.ConnectAsync("127.0.0.1", port, "watcher", "pw", stop.Token);

                    // 一覧
                    IReadOnlyList<Core.Files.RemoteEntry> listed = await client.ListAsync("/", stop.Token);
                    Core.Files.RemoteEntry found = listed.FirstOrDefault(e => e.Name == "startup-config");
                    Assert(found.Name == "startup-config", $"一覧に見つからない: {listed.Count} 件");
                    Assert(!found.IsDirectory, "ファイルがフォルダ扱いになっている");
                    Assert(found.Size > 0, "サイズが 0 のまま");

                    // 取得（中身まで一致すること。長さだけでは化けを見逃す）
                    string got = Path.Combine(work, "got.txt");
                    var seen = new List<Services.TransferProgress>();
                    await client.DownloadAsync("/startup-config", got,
                                               new Progress<Services.TransferProgress>(seen.Add), stop.Token);
                    Assert(File.ReadAllText(got) == body, "取得した中身が違う");

                    // 送信 → サーバ側の実体で確かめる
                    string put = Path.Combine(work, "put.txt");
                    File.WriteAllText(put, "interface Gi0/1\n");
                    await client.UploadAsync(put, "/uploaded.txt", null, stop.Token);
                    Assert(File.Exists(Path.Combine(root, "uploaded.txt")), "送ったファイルがサーバ側に無い");

                    // フォルダを作る → 改名 → 消す
                    await client.MakeDirectoryAsync("/backup", stop.Token);
                    Assert(Directory.Exists(Path.Combine(root, "backup")), "フォルダが作られていない");

                    await client.RenameAsync("/uploaded.txt", "/renamed.txt", stop.Token);
                    Assert(File.Exists(Path.Combine(root, "renamed.txt")), "改名されていない");

                    await client.DeleteAsync("/renamed.txt", isDirectory: false, stop.Token);
                    Assert(!File.Exists(Path.Combine(root, "renamed.txt")), "ファイルが消えていない");

                    await client.DeleteAsync("/backup", isDirectory: true, stop.Token);
                    Assert(!Directory.Exists(Path.Combine(root, "backup")), "フォルダが消えていない");

                    log.AppendLine($"        指紋 {client.Fingerprint} / 進捗 {seen.Count} 回");
                }).GetAwaiter().GetResult();

                // 指紋を控えていること（画面に出して人が見比べるためのもの）
                Assert(client.Fingerprint is { Length: > 0 }, "ホスト鍵の指紋を控えていない");
            }
            finally
            {
                // 使ったものは必ず後始末する
                TryDelete(root);
                TryDelete(work);
                try { File.Delete(hostKey); } catch (IOException) { /* 消せなくても検査は済んでいる */ }
            }
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
            string path = Path.Combine(Path.GetTempPath(), $"networktoys-selftest-{Guid.NewGuid():N}.json");
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
        Views.MainWindow window, System.Windows.Controls.TabControl tabs,
        Action<System.Windows.Controls.TabItem> onShown)
    {
        int visited = 0;
        object? original = tabs.SelectedItem;

        foreach (object? item in tabs.Items)
        {
            if (item is not System.Windows.Controls.TabItem tab) continue;
            if (ReferenceEquals(tab, window.WifiTab)) continue;

            tabs.SelectedItem = tab;
            window.UpdateLayout();

            visited++;
            onShown(tab);

            // 内側の TabControl は「選ばれているタブ」の中身として、
            // TabItem ではなく親 TabControl の ContentPresenter の下にぶら下がる。
            // TabItem を起点に探すと何も見つからない（見つからないことに気づけず、
            // 束ねたタブの中身が丸ごと検査から漏れていた）
            foreach (System.Windows.Controls.TabControl inner in FindInnerTabs(tabs))
                visited += VisitTabs(window, inner, onShown);
        }

        tabs.SelectedItem = original;
        window.UpdateLayout();

        return visited;
    }

    /// <summary>
    /// ⓘ の説明の書き方を確かめる。<b>読みやすさは黙って劣化する</b>ので、
    /// 形だけでも機械で守る（中身の良し悪しは人にしか見られない）。
    ///
    /// 守るのは 3 つ。①1 行目がタブ名 ②節と箇条書きで書かれている
    /// ③どの行も折り返さない幅に収まっている。
    /// </summary>
    private static IEnumerable<string> CheckHelpShape(string name, string help)
    {
        string[] lines = help.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length < 3)
            yield return $"{name}（1 段落のまま。節と箇条書きに分けること）";

        if (!help.Contains('・', StringComparison.Ordinal))
            yield return $"{name}（箇条書きが無い）";

        // 浮いた箱だけ見えている状態で、何の説明か分かるように。
        // 親を添える書き方（「Meraki ─ ネットワーク」）の方が親切なので、
        // 前方一致ではなく「含まれていること」で見る
        if (!lines[0].Contains(name, StringComparison.Ordinal))
            yield return $"{name}（1 行目にタブ名が無い: 「{lines[0]}」）";

        // 幅は ToolTip の MaxWidth 840px = 全角 68 字ぶん。
        // 文字数ではなく表示幅で数える（日本語と URL で基準が変わるため）
        foreach (string line in lines)
        {
            int width = Core.Reporting.TextWidth.Of(line);

            if (width > 136)
                yield return $"{name}（{width / 2} 字で折り返す: 「{line}」）";
        }
    }

    /// <summary>同じ分類がひと続きに並んでいるか。飛び飛びだとメニューの見出しが重複する。</summary>
    private static bool IsGrouped(IReadOnlyList<string> tags)
    {
        var seen = new HashSet<string>();
        string current = "";

        foreach (string tag in tags)
        {
            if (tag == current) continue;
            if (!seen.Add(tag)) return false;

            current = tag;
        }

        return true;
    }

    /// <summary>最上位から入れ子の先まで、すべてのタブ。論理ツリーなので実体化を待たない。</summary>
    private static IEnumerable<System.Windows.Controls.TabItem> AllTabs(System.Windows.Controls.TabControl tabs)
    {
        foreach (object? item in tabs.Items)
        {
            if (item is not System.Windows.Controls.TabItem tab) continue;

            yield return tab;

            foreach (System.Windows.Controls.TabControl inner in FindInnerTabsInContent(tab.Content))
            foreach (System.Windows.Controls.TabItem child in AllTabs(inner))
                yield return child;
        }
    }

    /// <summary>タブの中身から、その型の要素を論理ツリーで拾う。</summary>
    private static IEnumerable<T> FindIn<T>(object? content) where T : DependencyObject
    {
        if (content is not DependencyObject node) yield break;

        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is T hit) yield return hit;

            foreach (T found in FindIn<T>(child))
                yield return found;
        }
    }

    /// <summary>
    /// タブの中身にある内側の TabControl を<b>論理ツリーで</b>探す。
    ///
    /// 視覚ツリー版(<see cref="FindInnerTabs"/>)は「いま選ばれているタブの中身」しか
    /// 見つけられない。数を数えるだけなら、実体化を待たずに済むこちらが確実。
    /// </summary>
    private static IEnumerable<System.Windows.Controls.TabControl> FindInnerTabsInContent(object? content)
    {
        if (content is not DependencyObject node) yield break;

        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is System.Windows.Controls.TabControl inner)
            {
                yield return inner;
                continue;   // その中は数えない(入れ子は 2 段までの決まり)
            }

            foreach (System.Windows.Controls.TabControl found in FindInnerTabsInContent(child))
                yield return found;
        }
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

    private static IEnumerable<System.Windows.Controls.DockPanel> FindDockPanels(DependencyObject root)
    {
        if (root is System.Windows.Controls.DockPanel panel) yield return panel;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            foreach (System.Windows.Controls.DockPanel found in FindDockPanels(VisualTreeHelper.GetChild(root, i)))
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

    /// <summary>
    /// 偽の APIC。<b>ソケットを 1 本も開かない</b>（偽の Cisco 機器・偽の STUN と同じ手）。
    /// ログイン → 2 ページに分かれた取得 → ログアウトまでを、実機なしで通す。
    /// </summary>
    private sealed class FakeApic : System.Net.Http.HttpMessageHandler
    {
        private const string LoginJson =
            """{"totalCount":"1","imdata":[{"aaaLogin":{"attributes":{"token":"T1","refreshTimeoutSeconds":"600"}}}]}""";

        private const string Empty = """{"totalCount":"0","imdata":[]}""";

        /// <summary>テナントを枝ごと返す（設定の書き出し用）。子の並びは APIC 任せのつもりで逆順にしてある。</summary>
        private const string Tenant =
            """{"totalCount":"1","imdata":[{"fvTenant":{"attributes":{"dn":"uni/tn-Prod","name":"Prod"},"children":[{"fvAp":{"attributes":{"dn":"uni/tn-Prod/ap-Shop","name":"Shop"}}},{"fvBD":{"attributes":{"dn":"uni/tn-Prod/BD-Web","name":"BD-Web","modTs":"2026-08-17T09:00:00.000+09:00"}}}]}}]}""";

        /// <summary>
        /// 総数が 1 ページ(200 件)に収まらないと言う。＝2 ページ目を取りに行くはず。
        /// 中身の件数は検査に要るぶんだけ（実機は 1 ページ 200 件で返す）。
        /// </summary>
        private const string Page0 =
            """{"totalCount":"201","imdata":[{"faultInst":{"attributes":{"severity":"critical","code":"F1","dn":"a"}}},{"faultInst":{"attributes":{"severity":"minor","code":"F2","dn":"b"}}}]}""";

        private const string Page1 =
            """{"totalCount":"201","imdata":[{"faultInst":{"attributes":{"severity":"warning","code":"F3","dn":"c"}}}]}""";

        public List<string> Paths { get; } = [];

        public List<string> Cookies { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.PathAndQuery ?? "";

            Paths.Add(path);
            Cookies.Add(request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values)
                ? string.Join(";", values)
                : "");

            string body = path switch
            {
                "/api/aaaLogin.json" => LoginJson,
                "/api/aaaLogout.json" => Empty,
                _ when path.StartsWith("/api/mo/", StringComparison.Ordinal) => Tenant,
                _ when path.Contains("page=0", StringComparison.Ordinal) => Page0,
                _ when path.Contains("page=1", StringComparison.Ordinal) => Page1,
                _ => Empty,
            };

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// 偽の Catalyst 9800。RESTCONF は取得のたびに 1 本ずつ GET するだけなので、
    /// 見るのは「GET だけか」「Basic 認証と Accept が毎回付くか」「候補の 2 本目へ進むか」
    /// 「204 を 0 件として扱うか」の 4 つ。
    /// </summary>
    private sealed class FakeWlc : System.Net.Http.HttpMessageHandler
    {
        private const string Clients =
            """{"Cisco-IOS-XE-wireless-client-oper:common-oper-data":[{"client-mac":"aabb.ccdd.eeff","ap-name":"AP-1"}]}""";

        private const string Rrm =
            """{"Cisco-IOS-XE-wireless-rrm-global-oper:rrm-measurement":[{"wtp-mac":"00aa.bb11.2200"}]}""";

        public List<string> Paths { get; } = [];

        public List<string> Methods { get; } = [];

        public List<string> Authorizations { get; } = [];

        public List<string> Accepts { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";

            Paths.Add(path);
            Methods.Add(request.Method.Method);
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            Accepts.Add(string.Join(",", request.Headers.Accept.Select(a => a.MediaType)));

            // 1 本目の RRM は「このモジュールは無い」。2 本目へ進めるかを見る
            if (path.Contains("rrm-oper", StringComparison.Ordinal))
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            // 設定側の AP 一覧は空（タグを当てた AP が 1 台も無い現場がある）
            if (path.Contains("ap-cfg", StringComparison.Ordinal))
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent));

            string body = path.Contains("rrm", StringComparison.Ordinal) ? Rrm : Clients;

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/yang-data+json"),
            });
        }
    }

    /// <summary>
    /// 偽の Catalyst Center。見るのは「Basic はログインの 1 回だけで以後は X-Auth-Token か」
    /// 「entity_type をヘッダで渡しているか」「offset が 1 始まりで次のページへ進むか」
    /// 「候補の 2 本目へ進むか」「POST がトークンの取得だけか」。
    /// </summary>
    private sealed class FakeDnac : System.Net.Http.HttpMessageHandler
    {
        private const string TokenJson = """{"Token":"T1"}""";

        private const string EnrichmentJson =
            """{"response":[{"userDetails":{"hostMac":"AA:BB:CC:DD:EE:01","hostIpV4":"10.10.22.98","hostType":"WIRED","port":"Gi1/0/13","healthScore":[{"score":10}]},"connectedDevice":[{"deviceDetails":{"hostname":"sw-1"}}]}]}""";

        private const string HealthJson = """{"response":[{"id":"uuid-1","overallHealth":9}]}""";

        public List<string> Paths { get; } = [];

        public List<string> Methods { get; } = [];

        public List<string> Authorizations { get; } = [];

        public List<string> Tokens { get; } = [];

        public List<string> EntityTypes { get; } = [];

        public List<string> EntityValues { get; } = [];

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.PathAndQuery ?? "";

            Paths.Add(path);
            Methods.Add(request.Method.Method);
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            Tokens.Add(Header(request, "X-Auth-Token"));

            if (Header(request, "entity_type") is { Length: > 0 } type) EntityTypes.Add(type);
            if (Header(request, "entity_value") is { Length: > 0 } value) EntityValues.Add(value);

            // 1 本目の健全度は「この版には無い」。2 本目へ進めるかを見る
            if (path.StartsWith("/dna/data/api/v1/networkDevices", StringComparison.Ordinal))
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            string body = path switch
            {
                _ when path == Core.Assurance.DnacCatalog.TokenPath => TokenJson,
                _ when path.Contains("client-enrichment", StringComparison.Ordinal) => EnrichmentJson,
                _ when path.Contains("network-health", StringComparison.Ordinal) => HealthJson,
                _ when path.Contains("offset=1&", StringComparison.Ordinal) => Inventory(Services.DnacClient.PageSize),
                _ when path.Contains("network-device", StringComparison.Ordinal) => Inventory(3),
                _ => """{"response":[]}""",
            };

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>1 ページぶんの在庫。件数が 1 ページに満たなくなるまで取りに行くのを見る。</summary>
        private static string Inventory(int count)
        {
            string rows = string.Join(",", Enumerable.Range(0, count)
                .Select(i => "{\"id\":\"uuid-" + i + "\",\"hostname\":\"sw-" + i + "\"}"));

            return "{\"response\":[" + rows + "]}";
        }

        private static string Header(System.Net.Http.HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? string.Join(",", values)
                : "";
    }

    /// <summary>証明書で断られる相手（APIC でも WLC でも使う）。指紋が未受け入れの経路を踏む。</summary>
    private sealed class RefusingHost : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException(
                "TLS", new System.Security.Authentication.AuthenticationException("証明書を検証できません"));
    }
}
