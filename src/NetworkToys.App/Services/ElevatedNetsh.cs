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
        string outPath = path + ".out";

        try
        {
            // ANSI(日本語環境では cp932)で書く。UTF-8 だと「イーサネット」のような
            // 日本語アダプタ名が netsh 側で一致せず、静かに失敗する
            File.WriteAllLines(path, scriptLines, AnsiEncoding());

            Process? process;
            try
            {
                // cmd /S /C 経由で netsh の出力をファイルへ落とす(リダイレクトと ShellExecute は
                // 両立しないため)。失敗時にそのまま画面へ出す — ロケール依存なので解釈はしない。
                // UAC の確認は「Windows コマンド プロセッサ」名義になるが、原因の見えない
                // 「コード 1」よりよい(2026-08-21 ユーザー報告)
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = CommandArguments(path, outPath),
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
                    return $"netsh がエラーを返しました(コード {process.ExitCode})。{NetshSays(outPath)}";
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
                File.Delete(outPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // タイムアウト時はまだ掴まれていることがある。%TEMP% なのでいずれ片付く
            }
        }
    }

    /// <summary>
    /// cmd に渡す引数。/S を付けると cmd は<b>最初と最後の引用符だけ</b>を剥がすので、
    /// 途中の引用符付きパス(空白入りの %TEMP% など)がそのまま残る。
    /// </summary>
    internal static string CommandArguments(string scriptPath, string outputPath)
        => $"/S /C \"netsh -f \"{scriptPath}\" > \"{outputPath}\" 2>&1\"";

    /// <summary>
    /// netsh が言ったことをそのまま返す(<b>解釈はしない</b> — 出力はロケール依存)。
    /// 読めなければ従来の一般論に落とす。
    /// </summary>
    private static string NetshSays(string outputPath)
    {
        const string fallback = "入力内容とアダプタ名を確かめてください。";

        try
        {
            string[] lines = [.. DecodeConsoleOutput(File.ReadAllBytes(outputPath))
                .Split('\n')
                .Select(l => l.Trim('\r', ' ', '\t'))
                .Where(l => l.Length > 0)];

            if (lines.Length == 0) return fallback;

            string text = string.Join(" / ", lines.Take(3));
            return $"netsh の応答: {text}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// リダイレクトされた netsh の出力を文字にする。<b>コードページを決め打ちしない</b> —
    /// netsh はリダイレクト先へ UTF-16 で書くことがあり、cp932 で読むと化ける
    /// (2026-08-21 に実機で化けた)。BOM か NUL バイトの有無で UTF-16LE を見分け、
    /// それ以外は UTF-8 厳密 → cp932 の順(ファイル読み込みと同じ判定)。
    /// </summary>
    internal static string DecodeConsoleOutput(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        // cp932/UTF-8 のテキストに NUL は現れない。UTF-16 なら改行(0A 00)や
        // 空白(20 00)で必ず入るので、1 つでもあれば UTF-16LE とみなす
        // (日本語主体の文は「奇数位置の NUL が多い」では拾えない — 漢字の上位バイトは NUL でない)
        if (bytes.Length >= 4 && bytes.Length % 2 == 0 && Array.IndexOf(bytes, (byte)0) >= 0)
            return Encoding.Unicode.GetString(bytes);

        return DroppedText.Decode(bytes);
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
