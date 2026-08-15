using System.IO;
using System.Net;
using System.Text;
using System.Windows;
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
                "Brush.Chip.Edge", "Brush.Scroll.Thumb", "Brush.Scroll.Thumb.Hover", "Brush.Scroll.Track",
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
                "Button.Subtle", "Button.Icon", "ScrollBar.Thumb",
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
                typeof(System.Windows.Controls.MenuItem),
                typeof(System.Windows.Controls.ToolTip),
                typeof(System.Windows.Controls.CheckBox),
                typeof(System.Windows.Controls.ComboBox),
                typeof(System.Windows.Controls.ComboBoxItem),
                typeof(System.Windows.Controls.TabItem),
                typeof(System.Windows.Controls.Button),
                typeof(System.Windows.Controls.TextBox),
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

        Check("すべてのタブを表示できる(遅延生成される中身の検査)", () =>
        {
            Assert(window is not null, "ウィンドウが生成されていないため確認できない");

            // TabControl は選ばれたタブしか実体化しないので、既定タブの表示だけでは
            // 他のタブのテンプレート適用時のエラー(リソースキーの打ち間違いなど)を見逃す
            object? original = window!.MainTabs.SelectedItem;

            foreach (object? item in window.MainTabs.Items)
            {
                // 無線タブは選んだ時点で WLAN API に触れる作り(位置情報の同意の
                // タイミングを人の操作に合わせるため)なので、ここでは選ばない
                if (ReferenceEquals(item, window.WifiTab)) continue;

                window.MainTabs.SelectedItem = item;
                window.UpdateLayout();
            }

            window.MainTabs.SelectedItem = original;
            window.UpdateLayout();
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

        Check("作業セッションを保存して読み戻せる", () =>
        {
            // ソースジェネレータへの登録漏れはコンパイルを通り抜けて実行時に落ちる
            string path = Path.Combine(Path.GetTempPath(), $"pingwatcher-worksession-{Guid.NewGuid():N}.json");
            try
            {
                var session = new Core.Work.WorkSession
                {
                    Name = "自己診断",
                    Before = new Core.Work.WorkSnapshot(
                        DateTimeOffset.Now, "作業前", 1000,
                        [new Core.Work.WorkEntry("127.0.0.1|Icmp|0", "127.0.0.1", "ICMP", "127.0.0.1", "備考", true, 30, 0, 1, 2, 0)]),
                };

                Core.Work.WorkSessionStore.Save(path, session);
                Core.Work.WorkSession? loaded = Core.Work.WorkSessionStore.Load(path, out string? storeError);

                Assert(storeError is null, $"読み込みに失敗: {storeError}");
                Assert(loaded is not null, "セッションを読み戻せません");
                Assert(loaded!.Before!.Entries.Count == 1, "宛先の件数が合わない");
                Assert(loaded.Before.Entries[0].Comment == "備考", "日本語が壊れている");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
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
                    [], "不達", true)],
                IpConfig: "Windows IP 構成",
                Wireless: [("SSID", "office")]);

            string text = Core.Reporting.TextReportWriter.Render(data);

            Assert(text.Contains("[NG]", StringComparison.Ordinal), "応答なしの印が出ていない");
            Assert(text.Contains("1階 EPS", StringComparison.Ordinal), "日本語の備考が落ちている");
            Assert(text.Contains("[ipconfig /all]", StringComparison.Ordinal), "ipconfig の節が無い");
            Assert(text.Contains("[無線 LAN]", StringComparison.Ordinal), "無線の節が無い");

            log.AppendLine($"        テキスト記録: {text.Split('\n').Length} 行");
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
    private static HashSet<string> KeysOf(string relativeSource)
    {
        var dictionary = new ResourceDictionary { Source = new Uri(relativeSource, UriKind.Relative) };

        return [.. dictionary.Keys.OfType<string>()];
    }
}
