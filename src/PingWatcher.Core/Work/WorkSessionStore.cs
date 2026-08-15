using System.Globalization;
using System.Text.Json;
using PingWatcher.Core.Storage;

namespace PingWatcher.Core.Work;

/// <summary>
/// 作業セッションの読み書き。
///
/// <b>1 セッション＝1 ファイル</b>にする。マーカーを打つたびに全履歴を書き直さずに済み、
/// 1 件壊れても他のセッションが生き残る。整理もファイルを消すだけで済む。
/// </summary>
public static class WorkSessionStore
{
    /// <summary>ファイル名に使えない文字を落とす。</summary>
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static WorkSession? Load(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            WorkSessionDocument? document = JsonSerializer.Deserialize(json, PingWatcherJsonContext.Default.WorkSessionDocument);

            if (document is null)
            {
                error = "作業記録の内容が空でした。";
                return null;
            }

            return document.Session;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"作業記録を読み込めませんでした: {ex.Message}";
            return null;
        }
    }

    public static void Save(string path, WorkSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(
            new WorkSessionDocument { Session = session },
            PingWatcherJsonContext.Default.WorkSessionDocument);

        string temporary = path + ".tmp";

        // rename の前にディスクへ届いたことを確かめる。NTFS は rename と
        // データ書き込みの順序を保証しないため、Flush 無しだと電源断の直後に
        // 「置き換えは済んだのに中身が 0 バイト」のファイルが残りうる
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>保存先のファイル名を組み立てる。日時を先頭に置いて並び順で追えるようにする。</summary>
    public static string BuildFileName(DateTimeOffset startedAt, string name)
    {
        string safe = new([.. (name ?? string.Empty).Select(c => Invalid.Contains(c) ? '_' : c)]);
        safe = safe.Trim();

        if (safe.Length > 40)
            safe = safe[..40];

        // ファイル名の日時は必ず InvariantCulture で組む。既定カルチャの暦（和暦など）で
        // 組むと、Ordinal の並べ替えで「新しい順」が成り立たなくなる
        string stamp = startedAt.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        return safe.Length > 0 ? $"{stamp}-{safe}.json" : $"{stamp}.json";
    }

    /// <summary>保存済みのセッションを新しい順に並べる。</summary>
    public static IReadOnlyList<string> ListFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return [.. Directory.EnumerateFiles(directory, "*.json")
                .OrderByDescending(f => f, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
