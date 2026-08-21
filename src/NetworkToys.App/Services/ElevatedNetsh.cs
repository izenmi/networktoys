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

    /// <summary>netsh のスクリプトを実行する(プロキシ設定用)。null = 成功。</summary>
    public static Task<string?> ApplyAsync(IReadOnlyList<string> scriptLines)
        // ANSI(日本語環境では cp932)で書く。UTF-8 だと日本語が netsh 側で一致せず、静かに失敗する
        => RunAsync("netsh", ".txt", AnsiEncoding(), scriptLines,
            path => $"netsh -f \"{path}\"", TimeSpan.FromSeconds(30));

    /// <summary>
    /// PowerShell(NetTCPIP)のスクリプトを実行する。<b>IP 設定はこちら</b> —
    /// netsh には DHCP の旗を直接切り替える命令が無く、旗と実アドレスが食い違った機械では
    /// 「すでに有効です」の断りから抜けられない(2026-08-21 実機)。
    /// スクリプトは BOM 付き UTF-8 で書く(PowerShell は BOM を正しく読むので環境依存が無い)。
    /// </summary>
    public static Task<string?> ApplyPowerShellAsync(IReadOnlyList<string> scriptLines)
        // 昇格した新しいセッションは NetTCPIP などのモジュール読込で netsh より立ち上がりが遅い
        => RunAsync("PowerShell", ".ps1",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), scriptLines,
            path => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{path}\"", TimeSpan.FromSeconds(90));

    /// <summary>実行する。null = 成功。それ以外はそのまま画面に出せる日本語メッセージ。</summary>
    private static async Task<string?> RunAsync(
        string tool, string extension, Encoding encoding,
        IReadOnlyList<string> scriptLines, Func<string, string> command, TimeSpan timeout)
    {
        string path = Path.Combine(Path.GetTempPath(), $"NetworkToys-netsh-{Guid.NewGuid():N}{extension}");
        string outPath = path + ".out";

        try
        {
            File.WriteAllLines(path, scriptLines, encoding);

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
                    Arguments = CommandArguments(command(path), outPath),
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
                return $"{tool} を起動できませんでした。";

            using (process)
            {
                using var cutoff = new CancellationTokenSource(timeout);
                try
                {
                    await process.WaitForExitAsync(cutoff.Token);
                }
                catch (OperationCanceledException)
                {
                    // 非昇格のこちらからは昇格した子を Kill できない(AccessDenied)。
                    // 放置して、現在値の確認を促す
                    return $"{tool} から {timeout.TotalSeconds:0} 秒応答がありません。現在値を確認してください。";
                }

                if (process.ExitCode != 0)
                    return $"{tool} がエラーを返しました(コード {process.ExitCode})。{ToolSays(tool, outPath)}";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            CrashLog.Write(ex, "ElevatedNetsh.RunAsync");
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
    internal static string CommandArguments(string command, string outputPath)
        => $"/S /C \"{command} > \"{outputPath}\" 2>&1\"";

    /// <summary>
    /// netsh が言ったことをそのまま返す(<b>解釈はしない</b> — 出力はロケール依存)。
    /// 読めなければ従来の一般論に落とす。
    /// </summary>
    private static string ToolSays(string tool, string outputPath)
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
            return $"{tool} の応答: {text}";
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
