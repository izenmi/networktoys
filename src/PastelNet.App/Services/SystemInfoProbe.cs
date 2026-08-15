using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PastelNet.App.Services;

/// <summary>
/// OS のコマンド出力をそのまま持ち帰る。
///
/// .NET の API から同じ情報を組み立てることもできるが、現場のレポートには
/// <c>ipconfig /all</c> の見慣れた形のまま載っている方が読み手に伝わる。
/// </summary>
internal static class SystemInfoProbe
{
    private static bool _providerRegistered;

    public static Task<string> GetIpConfigAsync(CancellationToken token)
        => RunAsync("ipconfig", "/all", token);

    private static async Task<string> RunAsync(string fileName, string arguments, CancellationToken token)
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
            };

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
                return $"{fileName} を起動できませんでした。";

            // コマンドが固まっても待ち続けない
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return output.Trim().Length > 0
                ? output.ReplaceLineEndings("\n")
                : $"{fileName} は何も出力しませんでした。";
        }
        catch (OperationCanceledException)
        {
            return $"{fileName} {arguments} の実行が時間内に終わりませんでした。";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, $"SystemInfoProbe({fileName})");
            return $"{fileName} {arguments} を実行できませんでした: {ex.Message}";
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
