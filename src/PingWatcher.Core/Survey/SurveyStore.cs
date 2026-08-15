using System.Text.Json;
using PingWatcher.Core.Storage;
using PingWatcher.Core.Work;

namespace PingWatcher.Core.Survey;

/// <summary>
/// サーベイの読み書き。<b>1 サーベイ＝1 ファイル</b>（sessions と同じ流儀）。
/// ファイル名の組み立てと一覧は <see cref="WorkSessionStore"/> の実装が
/// ドキュメントに依存しないので、そのまま借りる。
/// </summary>
public static class SurveyStore
{
    public static SurveyDocument? Load(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            SurveyDocument? document = JsonSerializer.Deserialize(json, PingWatcherJsonContext.Default.SurveyDocument);

            if (document is null)
            {
                error = "サーベイの内容が空でした。";
                return null;
            }

            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"サーベイを読み込めませんでした: {ex.Message}";
            return null;
        }
    }

    public static void Save(string path, SurveyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(document, PingWatcherJsonContext.Default.SurveyDocument);

        string temporary = path + ".tmp";

        // rename の前にディスクへ届いたことを確かめる（WorkSessionStore と同じ理由。
        // NTFS は rename とデータ書き込みの順序を保証しない）
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>保存先のファイル名。日時を先頭に置いて並び順で追えるようにする。</summary>
    public static string BuildFileName(DateTimeOffset createdAt, string name)
        => WorkSessionStore.BuildFileName(createdAt, name);

    /// <summary>保存済みのサーベイを新しい順に並べる。</summary>
    public static IReadOnlyList<string> ListFiles(string directory)
        => WorkSessionStore.ListFiles(directory);
}
