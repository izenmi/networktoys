using System.Globalization;
using System.Text;

namespace NetworkToys.Core.Terminal;

/// <summary>コマンド 1 本の結果。</summary>
public sealed record CommandResult(
    int Index,
    string Command,
    string Output,
    TimeSpan Elapsed,
    string? Problem);

/// <summary>1 台分の収集結果。</summary>
public sealed record DeviceCollectionResult(
    string Host,
    int Port,
    string? LearnedHostname,
    string UserName,
    bool UsedSsh,
    string? HostKeyFingerprint,
    DateTime StartedAt,
    DateTime FinishedAt,
    bool ReachedEnable,
    string PagerNote,
    IReadOnlyList<CommandResult> Commands,
    string? FailureMessage,
    string Transcript = "");

/// <summary>
/// 収集結果から 1 台 1 ファイルのテキストを作る。
///
/// <b>認証情報はここに一切入れない。</b>入れ物すら作らない
/// （<see cref="DeviceCollectionResult"/> にパスワードのプロパティが無いのはそのため）。
/// 機器から届いた生のログ（<see cref="DeviceCollectionResult.Transcript"/>）だけを保存する。
/// </summary>
public static class DeviceReport
{
    public static string Render(DeviceCollectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.Transcript))
            return "";

        // メモ帳などで開くので改行は CRLF に揃える
        return result.Transcript
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// 保存するファイル名。
    ///
    /// <b><c>Path.GetInvalidFileNameChars()</c> を使わない。</b>
    /// Linux では <c>/</c> と NUL しか返さないので、開発機(Linux)と CI(Windows)で
    /// テストの結果が変わってしまう。Core は Windows 非依存が建前なので明示列挙する。
    /// </summary>
    public static string FileName(DeviceCollectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string name = Sanitize(result.LearnedHostname ?? "");
        string host = Sanitize(result.Host);

        if (name.Length == 0) name = host;
        if (name.Length == 0) name = "device";

        string stamp = result.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        return host.Length > 0 && !string.Equals(name, host, StringComparison.OrdinalIgnoreCase)
            ? $"{name}_{host}_{stamp}.txt"
            : $"{name}_{stamp}.txt";
    }

    /// <summary>
    /// Windows のファイル名に使えない要素を落とす。収集の保存名と、
    /// 一覧の CSV 書き出し(<c>trace-192.168.1.1</c> のように宛先を混ぜる)で共有する。
    ///
    /// <b><c>Path.GetInvalidFileNameChars()</c> は使わない</b> — Linux では
    /// <c>/</c> と NUL しか返さず、開発機と CI でテストの結果が変わる。
    /// </summary>
    public static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') continue;
            if (c < ' ') continue;

            builder.Append(c);
        }

        // 末尾の空白とドットは Windows で作れない
        string text = builder.ToString().TrimEnd(' ', '.');

        if (text.Length > 60) text = text[..60].TrimEnd(' ', '.');

        // 予約名はそのままだと作れない
        foreach (string reserved in Reserved)
        {
            if (string.Equals(text, reserved, StringComparison.OrdinalIgnoreCase))
                return text + "_";
        }

        return text;
    }

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
}
