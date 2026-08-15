using System.IO;

namespace PingWatcher.App;

/// <summary>
/// 設定と記録の置き場所。
///
/// 以前はこのアプリを PastelNet と呼んでいた。改名しただけで宛先リストや作業の記録が
/// 消えるのは事故なので、<b>初回に一度だけ旧フォルダの中身を引き継ぐ</b>。
/// </summary>
internal static class AppData
{
    private const string FolderName = "PingWatcher";
    private const string PreviousFolderName = "PastelNet";

    private static bool _migrated;

    /// <summary>
    /// 設定を置くフォルダ。初回の呼び出しで、必要なら旧フォルダから引き継ぐ。
    /// </summary>
    public static string Directory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string current = Path.Combine(root, FolderName);

        MigrateOnce(Path.Combine(root, PreviousFolderName), current);

        return current;
    }

    /// <summary>フォルダ内のファイルへの絶対パス。</summary>
    public static string PathOf(string fileName) => Path.Combine(Directory(), fileName);

    /// <summary>
    /// 旧フォルダから引き継ぐ。
    ///
    /// <b>移動ではなく複製にしている。</b>引き継ぎに失敗しても、古い方を残しておけば
    /// 手で拾い直せる。すでに新しい方にファイルがあれば、そちらを優先して上書きしない。
    /// </summary>
    private static void MigrateOnce(string previous, string current)
    {
        if (_migrated) return;
        _migrated = true;

        try
        {
            if (!System.IO.Directory.Exists(previous)) return;

            // 一度でも使い始めていれば引き継がない。上書きすると新しい方が消える
            if (System.IO.Directory.Exists(current) &&
                System.IO.Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            System.IO.Directory.CreateDirectory(current);

            foreach (string source in System.IO.Directory.EnumerateFiles(previous, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(previous, source);
                string destination = Path.Combine(current, relative);

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 引き継げなくても起動は止めない。旧フォルダは残っているので手で拾える
            CrashLog.Write(ex, "AppData.MigrateOnce");
        }
    }
}
