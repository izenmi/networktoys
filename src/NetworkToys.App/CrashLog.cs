using System.IO;
using System.Text;

namespace NetworkToys.App;

/// <summary>
/// 未処理例外をファイルに残す。
///
/// 開発環境で exe を実行できないため、落ちたときに手掛かりが残らないと原因に辿り着けない。
/// CI では実行ディレクトリに出るので、ワークフローから内容を表示できる。
/// </summary>
internal static class CrashLog
{
    /// <summary>これを超えたら追記をやめて書き直す。例外の嵐で無限に育てない。</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();

    // Environment.CurrentDirectory は使わない。保存ダイアログを一度使うと
    // CWD が選択先フォルダに変わり、以降のクラッシュログが行方不明になる
    public static string Path => System.IO.Path.Combine(AppData.DirectoryForLogs, "crash.log");

    public static void Write(Exception? exception, string source)
    {
        var text = new StringBuilder();
        text.AppendLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] {source}");
        text.AppendLine($"  ランタイム: {Environment.Version} / OS: {Environment.OSVersion.VersionString}");
        text.AppendLine($"  対話セッション: {Environment.UserInteractive}");

        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            text.AppendLine($"  {ex.GetType().FullName}: {ex.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
                text.AppendLine(ex.StackTrace);
        }

        if (exception is null)
            text.AppendLine("  例外オブジェクトを取得できませんでした。");

        text.AppendLine();

        string rendered = text.ToString();
        Console.Error.WriteLine(rendered);

        try
        {
            lock (Gate)
            {
                string path = Path;
                var info = new FileInfo(path);

                if (info.Exists && info.Length > MaxBytes)
                    File.WriteAllText(path, rendered, Encoding.UTF8);
                else
                    File.AppendAllText(path, rendered, Encoding.UTF8);
            }
        }
        catch
        {
            // ログを書けないこと自体で落とさない
        }
    }
}
