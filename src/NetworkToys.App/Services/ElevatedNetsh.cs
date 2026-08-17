using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetworkToys.App.Services;

/// <summary>
/// IP 設定の適用。アプリ自体は昇格せず、<b>適用の瞬間だけ</b>
/// <c>netsh -f 一時スクリプト</c> を UAC(runas)で起動する。
/// 複数コマンドを 1 ファイルにまとめるので UAC は 1 回で済む。
///
/// 昇格プロセスの出力は読めない(リダイレクトと ShellExecute は両立しない)し、
/// netsh の出力はロケール依存なのでどのみちパースしない。成否は ExitCode と、
/// 適用後の NetworkInterface 再読で確かめる。
/// </summary>
internal static class ElevatedNetsh
{
    private static bool _encodingRegistered;

    /// <summary>実行する。null = 成功。それ以外はそのまま画面に出せる日本語メッセージ。</summary>
    public static async Task<string?> ApplyAsync(IReadOnlyList<string> scriptLines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"NetworkToys-netsh-{Guid.NewGuid():N}.txt");

        try
        {
            // ANSI(日本語環境では cp932)で書く。UTF-8 だと「イーサネット」のような
            // 日本語アダプタ名が netsh 側で一致せず、静かに失敗する
            File.WriteAllLines(path, scriptLines, AnsiEncoding());

            Process? process;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"-f \"{path}\"",
                    UseShellExecute = true,     // UAC の昇格ダイアログを出すのに必須
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return "UAC で許可されなかったため、適用していません。";
            }

            if (process is null)
                return "netsh を起動できませんでした。";

            using (process)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    // 非昇格のこちらからは昇格した子を Kill できない(AccessDenied)。
                    // 放置して、現在値の確認を促す
                    return "netsh から 30 秒応答がありません。現在値を確認してください。";
                }

                if (process.ExitCode != 0)
                    return $"netsh がエラーを返しました(コード {process.ExitCode})。入力内容とアダプタ名を確かめてください。";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            CrashLog.Write(ex, "ElevatedNetsh.ApplyAsync");
            return $"適用に失敗しました: {ex.Message}";
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // タイムアウト時はまだ掴まれていることがある。%TEMP% なのでいずれ片付く
            }
        }
    }

    private static Encoding AnsiEncoding()
    {
        try
        {
            if (!_encodingRegistered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _encodingRegistered = true;
            }

            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }
}
