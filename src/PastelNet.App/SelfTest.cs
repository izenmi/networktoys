using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PastelNet.Core.Addressing;

namespace PastelNet.App;

/// <summary>
/// <c>PastelNet.exe --selftest</c> で走る自己診断。
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

        log.AppendLine($"PastelNet selftest  ({DateTime.Now:yyyy/MM/dd HH:mm:ss})");
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
            ];
            foreach (string key in keys)
                Assert(Application.Current.TryFindResource(key) is SolidColorBrush, $"{key} が SolidColorBrush として引けない");
        });

        Check("リソース: Controls.xaml のスタイルが解決できる", () =>
        {
            string[] keys = ["Card", "Badge", "Badge.Text", "Heading", "Caption", "Mono", "Button.Subtle", "ScrollBar.Thumb"];
            foreach (string key in keys)
                Assert(Application.Current.TryFindResource(key) is Style, $"{key} が Style として引けない");
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
            window.Close();
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

        Check("宛先リストを保存して読み戻せる", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"pastelnet-selftest-{Guid.NewGuid():N}.json");
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
}
