using System.Text.Json;

namespace NetworkToys.Core.Storage;

/// <summary>
/// settings.json の読み書き。壊れても起動は止めない・書き込みは
/// 一時ファイル経由の置き換え、という規則は TargetStore と同じ。
/// </summary>
public static class AppSettingsStore
{
    public static AppSettingsDocument Load(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
            return new AppSettingsDocument();

        try
        {
            string json = File.ReadAllText(path);
            AppSettingsDocument? document = JsonSerializer.Deserialize(json, NetworkToysJsonContext.Default.AppSettingsDocument);

            if (document is null)
            {
                error = "設定ファイルの内容が空でした。";
                return new AppSettingsDocument();
            }

            Normalize(document);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"設定ファイルを読み込めませんでした: {ex.Message}";
            return new AppSettingsDocument();
        }
    }

    /// <summary>書き込む。既存ファイルは書き込みが完全に終わってから置き換える。</summary>
    public static void Save(string path, AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(document, NetworkToysJsonContext.Default.AppSettingsDocument);

        string temporary = path + ".tmp";

        // rename の前にディスクへ届いたことを確かめる（TargetStore と同じ理由。
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

    private static void Normalize(AppSettingsDocument document)
    {
        document.Ping.Settings.Normalize();
        document.Ping.Targets.RemoveAll(t => !t.IsValid());
        document.Tcp.Settings.Normalize();
        document.Tcp.Targets.RemoveAll(t => !t.IsValid());

        document.IpPresets.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name));

        if (document.Theme is not ("dark" or "light"))
            document.Theme = "light";
    }
}
