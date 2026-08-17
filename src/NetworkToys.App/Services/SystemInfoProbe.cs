using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NetworkToys.App.Services;

/// <param name="Ok">取得できたか。</param>
/// <param name="Text">出力、または失敗した理由。</param>
/// <param name="CapturedAt">取得時刻。</param>
/// <remarks>
/// <b>成否を型で分けるのが肝。</b>失敗した理由を出力と同じ文字列で返すと、
/// 作業前後の差分に掛けたときに「成功した出力 vs エラー文言」という無意味な差分が
/// 「環境が激変した」ように見える。
/// </remarks>
internal sealed record CommandCapture(bool Ok, string Text, DateTimeOffset CapturedAt)
{
    public static CommandCapture Failed(string reason) => new(false, reason, DateTimeOffset.Now);
}

/// <summary>
/// OS のコマンド出力をそのまま持ち帰る。
///
/// .NET の API から同じ情報を組み立てることもできるが、現場のレポートには
/// <c>ipconfig /all</c> の見慣れた形のまま載っている方が読み手に伝わる。
/// </summary>
internal static class SystemInfoProbe
{
    private static bool _providerRegistered;

    public static Task<CommandCapture> GetIpConfigAsync(CancellationToken token)
        => RunAsync("ipconfig", "/all", token);

    /// <summary>
    /// 経路表。<b>IPv4 のみ</b>にする。IPv6 は一時アドレスの生成と失効で
    /// 作業と無関係に差分が出続けるため。
    /// </summary>
    public static Task<CommandCapture> GetRouteTableAsync(CancellationToken token)
        => RunAsync("route", "print -4", token);

    private static async Task<CommandCapture> RunAsync(string fileName, string arguments, CancellationToken token)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = GetConsoleEncoding(),
                StandardErrorEncoding = GetConsoleEncoding(),
            };

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
                return CommandCapture.Failed($"{fileName} を起動できませんでした。");

            // コマンドが固まっても待ち続けない
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            // 標準出力とエラーを同時に読む。片方だけ読むとバッファが詰まって止まりうる
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(timeout.Token);

            string output = outputTask.Result;

            if (process.ExitCode != 0)
            {
                string reason = errorTask.Result.Trim();
                return CommandCapture.Failed(
                    $"{fileName} {arguments} が終了コード {process.ExitCode} で終わりました。{reason}".TrimEnd());
            }

            return output.Trim().Length > 0
                ? new CommandCapture(true, output.ReplaceLineEndings("\n"), DateTimeOffset.Now)
                : CommandCapture.Failed($"{fileName} は何も出力しませんでした。");
        }
        catch (OperationCanceledException)
        {
            return CommandCapture.Failed($"{fileName} {arguments} の実行が時間内に終わりませんでした。");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, $"SystemInfoProbe({fileName})");
            return CommandCapture.Failed($"{fileName} {arguments} を実行できませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// コンソールの出力は OS のコードページ（日本語 Windows なら CP932）で返る。
    /// .NET Core は既定で UTF-8 として読むため、登録しないと日本語が化ける。
    /// </summary>
    private static Encoding GetConsoleEncoding()
    {
        try
        {
            if (!_providerRegistered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _providerRegistered = true;
            }

            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
