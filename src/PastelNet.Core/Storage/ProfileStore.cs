using System.Text.Json;
using PastelNet.Core.Models;

namespace PastelNet.Core.Storage;

/// <summary>profiles.json の中身。</summary>
public sealed class ProfileDocument
{
    public int Version { get; set; } = 1;

    public List<Profile> Profiles { get; set; } = [];
}

/// <summary>
/// プロファイルの読み書き。宛先リストと同じく、書き込みは置き換えで行う。
/// </summary>
public static class ProfileStore
{
    public static ProfileDocument Load(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
            return new ProfileDocument();

        try
        {
            string json = File.ReadAllText(path);
            ProfileDocument? document = JsonSerializer.Deserialize(json, PastelNetJsonContext.Default.ProfileDocument);

            if (document is null)
            {
                error = "プロファイルの内容が空でした。";
                return new ProfileDocument();
            }

            document.Profiles.RemoveAll(p => !p.IsValid());
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"プロファイルを読み込めませんでした: {ex.Message}";
            return new ProfileDocument();
        }
    }

    public static ProfileDocument Load(string path) => Load(path, out _);

    public static void Save(string path, ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(document, PastelNetJsonContext.Default.ProfileDocument);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }
}
