using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NetworkToys.Core.Net;

namespace NetworkToys.App.Services;

/// <summary>
/// Tera Term で機器へつなぐ。
///
/// 端末は同梱しない。現場で使い慣れているものを開くのが目的なので、
/// 場所は<b>既定の導入先から探し、見つからなければ 1 度だけ選んでもらって覚える</b>
/// （settings.json に持つ）。引数の組み立ては Core 側で検証してある。
/// </summary>
internal static class TeraTerm
{
    /// <summary>実行ファイルの場所。見つからなければ null。</summary>
    public static string? Locate()
    {
        string remembered = Settings.Current.TeraTermPath;
        if (remembered.Length > 0 && File.Exists(remembered))
            return remembered;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (string candidate in TeraTermCommand.WellKnownPaths(programFiles, programFilesX86))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath();
    }

    /// <summary>選んでもらった場所を覚える。書けなくても今回の接続は続ける。</summary>
    public static void Remember(string path)
    {
        try
        {
            Settings.Current.TeraTermPath = path;
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても動作には影響しない
        }
    }

    /// <summary>つなぐ。起動できなければ画面に出せる日本語を返す。</summary>
    public static string? Connect(string exePath, string host, int port, bool ssh)
    {
        string arguments = TeraTermCommand.Build(host, port, ssh);
        if (arguments.Length == 0) return "接続先が空です。";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,

                // Tera Term は作業フォルダに設定ファイルを探しにいくので、
                // exe と同じ場所で起動する（既定の設定が読まれるように）
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                UseShellExecute = true,
            });

            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return $"Tera Term を起動できませんでした: {ex.Message}";
        }
    }

    private static string? FindOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            try
            {
                string candidate = Path.Combine(directory, "ttermpro.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH に不正な文字が混ざっていることがある。その要素は飛ばす
            }
        }

        return null;
    }
}
