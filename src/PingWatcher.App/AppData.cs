using System.IO;

namespace PingWatcher.App;

/// <summary>
/// 設定と記録の置き場所。
///
/// <b>exe と同じフォルダに置く。</b>USB メモリに入れて現場へ持ち出したり、
/// 共有フォルダから直接動かしたりする使い方に合わせている。
/// 端末を移っても、フォルダごと持っていけば宛先リストがそのまま付いてくる。
///
/// ただし Program Files のような書き込めない場所へ置かれることもあるので、
/// そのときだけ %APPDATA% へ逃がす。黙って保存に失敗するよりはよい。
///
/// 以前の置き場所（%APPDATA%\PingWatcher\、さらに前は %APPDATA%\PastelNet\）に
/// 中身があれば、初回に一度だけ引き継ぐ。
/// </summary>
internal static class AppData
{
    private const string FolderName = "PingWatcher";
    private const string PreviousFolderName = "PastelNet";

    private static readonly object Gate = new();
    private static string? _resolved;

    /// <summary>
    /// このアプリが作るもの。「もう使い始めているか」の判定に使う。
    ///
    /// exe の横は exe 自体や DLL が必ず居るので、<b>「ファイルがあるか」では判定できない</b>。
    /// 自分が書いたものだけを見る。
    /// </summary>
    private static readonly string[] DataEntries =
    [
        "targets.json",
        "tcp-targets.json",
        "theme.txt",
        "sessions",
        "surveys",
    ];

    /// <summary>
    /// 引き継ぎ元として順に見る場所。手前から探し、最初に見つかったものを使う。
    /// </summary>
    private static IEnumerable<string> LegacyDirectories()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        yield return Path.Combine(roaming, FolderName);
        yield return Path.Combine(roaming, PreviousFolderName);
    }

    /// <summary>設定を置くフォルダ。初回の呼び出しで場所を決め、必要なら引き継ぐ。</summary>
    public static string Directory()
    {
        if (_resolved is not null) return _resolved;

        lock (Gate)
        {
            return _resolved ??= Resolve();
        }
    }

    /// <summary>フォルダ内のファイルへの絶対パス。</summary>
    public static string PathOf(string fileName) => Path.Combine(Directory(), fileName);

    /// <summary>
    /// クラッシュログの置き場所。場所の解決は失敗したこと自体をログに書くので、
    /// ここから <see cref="Directory"/> を呼ぶと再帰する。未解決の間は exe の横へ出す
    /// （CI が読むのも exe の横）。ロックは要らない（参照の読み取りは不可分）。
    /// </summary>
    public static string DirectoryForLogs => _resolved ?? ExecutableDirectory();

    /// <summary>
    /// いま使っている場所が exe の横かどうか。逃がした事実を画面や記録に出すのに使う。
    /// </summary>
    public static bool IsBesideExecutable
        => string.Equals(Directory(), ExecutableDirectory(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// exe の置かれているフォルダ。
    ///
    /// 単一ファイル発行では <c>Assembly.Location</c> が空文字を返すので使えない。
    /// <c>Environment.ProcessPath</c> から取り、それも駄目なら
    /// <c>AppContext.BaseDirectory</c> に落とす。
    /// </summary>
    private static string ExecutableDirectory()
    {
        if (Environment.ProcessPath is { Length: > 0 } path &&
            Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            return directory;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string Resolve()
    {
        string beside = ExecutableDirectory();

        if (CanWriteTo(beside))
        {
            MigrateInto(beside);
            return beside;
        }

        // Program Files などに置かれた場合。ここだけ従来の場所へ逃がす
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName);

        try
        {
            System.IO.Directory.CreateDirectory(fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex, "AppData.Resolve");
        }

        MigrateInto(fallback);
        return fallback;
    }

    /// <summary>
    /// 実際に書けるかを試す。属性だけでは分からない（読み取り専用の共有フォルダ、
    /// Program Files の仮想化など）ので、1 ファイル作って消す。
    /// </summary>
    private static bool CanWriteTo(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 古い置き場所から引き継ぐ。
    ///
    /// <b>移動ではなく複製にしている。</b>引き継ぎに失敗しても、古い方を残しておけば
    /// 手で拾い直せる。すでに新しい方に中身があれば、そちらを優先して何もしない。
    /// </summary>
    private static void MigrateInto(string current)
    {
        try
        {
            if (HasData(current)) return;

            string? source = LegacyDirectories()
                .FirstOrDefault(d => !string.Equals(d, current, StringComparison.OrdinalIgnoreCase) && HasData(d));

            if (source is null) return;

            System.IO.Directory.CreateDirectory(current);

            foreach (string file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                // 1 ファイルの失敗でループごと諦めない。途中で抜けると
                // 「一部だけコピー済み」の状態で HasData が真になり、
                // 次回以降は引き継ぎ自体が二度と試されなくなる
                try
                {
                    string destination = Path.Combine(current, Path.GetRelativePath(source, file));

                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CrashLog.Write(ex, $"AppData.MigrateInto({Path.GetFileName(file)})");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 引き継げなくても起動は止めない。古い方は残っているので手で拾える
            CrashLog.Write(ex, "AppData.MigrateInto");
        }
    }

    /// <summary>このアプリの設定や記録が既に置かれているか。</summary>
    private static bool HasData(string directory)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory)) return false;

            return DataEntries.Any(name =>
            {
                string path = Path.Combine(directory, name);
                return File.Exists(path) || System.IO.Directory.Exists(path);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
