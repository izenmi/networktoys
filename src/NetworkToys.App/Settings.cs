using System.Globalization;
using System.IO;
using NetworkToys.Core.Storage;

namespace NetworkToys.App;

/// <summary>
/// アプリ設定(settings.json)の入り口。起動時に <see cref="Initialize"/> で読み、
/// 以降は <see cref="Current"/> を書き換えて <see cref="Save"/> を呼ぶ。
///
/// 以前は targets.json / tcp-targets.json / theme.txt / columns.txt に
/// 分かれていた(ユーザー指示で 1 ファイルへ統合)。初回起動時に旧ファイルが
/// あれば取り込み、統合ファイルを書けたら旧ファイルは片付ける。
/// </summary>
internal static class Settings
{
    private const string FileName = "settings.json";

    private static readonly object Gate = new();

    public static AppSettingsDocument Current { get; private set; } = new();

    /// <summary>起動時に読めなかった理由。宛先画面が一度だけ表示する。</summary>
    public static string? LoadError { get; private set; }

    /// <summary>ウィンドウ生成・配色適用より前に一度だけ呼ぶ。</summary>
    public static void Initialize()
    {
        string path = AppData.PathOf(FileName);

        if (File.Exists(path))
        {
            Current = AppSettingsStore.Load(path, out string? error);
            LoadError = error;
            return;
        }

        Current = MigrateFromLegacyFiles();
    }

    /// <summary>まとめて書き出す。書けなくても落とさない(呼び出し側が状態表示する)。</summary>
    public static void Save()
    {
        lock (Gate)
        {
            AppSettingsStore.Save(AppData.PathOf(FileName), Current);
        }
    }

    /// <summary>
    /// 旧構成(4 ファイル)からの引き継ぎ。取り込めたら settings.json を書き、
    /// 成功したときだけ旧ファイルを消す(書けない環境で元データを失わないため)。
    /// </summary>
    private static AppSettingsDocument MigrateFromLegacyFiles()
    {
        var document = new AppSettingsDocument();
        var migrated = new List<string>();

        string targetsPath = AppData.PathOf("targets.json");
        if (File.Exists(targetsPath))
        {
            document.Ping = TargetStore.Load(targetsPath, out _);
            migrated.Add(targetsPath);
        }

        string tcpPath = AppData.PathOf("tcp-targets.json");
        if (File.Exists(tcpPath))
        {
            document.Tcp = TargetStore.Load(tcpPath, out _);
            migrated.Add(tcpPath);
        }

        string themePath = AppData.PathOf("theme.txt");
        if (File.Exists(themePath))
        {
            try
            {
                string theme = File.ReadAllText(themePath).Trim().ToLowerInvariant();
                if (theme is "dark" or "light")
                    document.Theme = theme;
                migrated.Add(themePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めなければ既定の配色で続ける
            }
        }

        // 旧 columns.txt は列幅だけのファイル。列幅は保存しなくなったので、
        // 読まずに片付ける（2026-08-18 ユーザー指示）
        string columnsPath = AppData.PathOf("columns.txt");
        if (File.Exists(columnsPath))
            migrated.Add(columnsPath);

        Current = document;

        try
        {
            Save();   // ここで書けなければ例外 → 旧ファイルは消さずに残る

            foreach (string path in migrated)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 消せなくても実害はない(次回以降は settings.json を読む)
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = $"設定ファイルを書き込めませんでした: {ex.Message}";
        }

        return document;
    }
}
