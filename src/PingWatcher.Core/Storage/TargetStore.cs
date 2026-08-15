using System.Text.Json;

namespace PingWatcher.Core.Storage;

/// <summary>
/// targets.json の読み書き。
///
/// 設定ファイルが壊れると宛先リストを失うので、書き込みは一時ファイル経由の
/// 置き換えにして、途中で電源が落ちても元のファイルが残るようにする。
/// </summary>
public static class TargetStore
{
    /// <summary>
    /// 読み込む。ファイルが無ければ空のドキュメントを返す。
    /// 壊れていた場合は <paramref name="error"/> に理由を入れ、空のドキュメントを返す
    /// （起動できなくなる方が困るので、例外は投げない）。
    /// </summary>
    public static TargetDocument Load(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
            return new TargetDocument();

        try
        {
            string json = File.ReadAllText(path);
            TargetDocument? document = JsonSerializer.Deserialize(json, PingWatcherJsonContext.Default.TargetDocument);

            if (document is null)
            {
                error = "設定ファイルの内容が空でした。";
                return new TargetDocument();
            }

            document.Settings.Normalize();
            document.Targets.RemoveAll(t => !t.IsValid());
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"設定ファイルを読み込めませんでした: {ex.Message}";
            return new TargetDocument();
        }
    }

    public static TargetDocument Load(string path) => Load(path, out _);

    /// <summary>書き込む。既存ファイルは書き込みが完全に終わってから置き換える。</summary>
    public static void Save(string path, TargetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(document, PingWatcherJsonContext.Default.TargetDocument);

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
}
