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
/// 収集結果を 1 台 1 ファイルのテキストにする。
///
/// <b>認証情報はここに一切入れない。</b>入れ物すら作らない
/// （<see cref="DeviceCollectionResult"/> にパスワードのプロパティが無いのはそのため）。
/// </summary>
public static class DeviceReport
{
    private const string Rule = "================================================================";

    public static string Render(DeviceCollectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();
        TimeSpan elapsed = result.FinishedAt - result.StartedAt;

        int done = result.Commands.Count(c => c.Problem is null);

        text.AppendLine(Rule);
        text.AppendLine("NetworkToys 収集ログ");
        text.AppendLine($"機器      : {result.LearnedHostname ?? "(不明)"}  ({result.Host}:{result.Port})");
        text.AppendLine($"接続      : {(result.UsedSsh ? "SSH" : "Telnet")} / ユーザー {Or(result.UserName, "(なし)")}");

        if (result.HostKeyFingerprint is { Length: > 0 })
            text.AppendLine($"ホスト鍵  : {result.HostKeyFingerprint}");

        text.AppendLine($"開始      : {result.StartedAt:yyyy/MM/dd HH:mm:ss}");
        text.AppendLine($"終了      : {result.FinishedAt:yyyy/MM/dd HH:mm:ss}  ({Describe(elapsed)})");
        text.AppendLine(result.ReachedEnable
            ? "特権      : ● 特権モード(#)"
            : "特権      : ◌ ユーザーモード(>)　※ show running-config などの特権コマンドは断られます"
              + "（enable のパスワードを確かめてください）");
        text.AppendLine($"ページャ  : {result.PagerNote}");
        text.AppendLine($"結果      : {result.Commands.Count} 本中 {done} 本成功");

        if (result.FailureMessage is { Length: > 0 })
            text.AppendLine($"失敗      : {result.FailureMessage}");

        text.AppendLine(Rule);
        text.AppendLine();

        foreach (CommandResult command in result.Commands)
        {
            string head = $"===[ {command.Index}/{result.Commands.Count} ] {command.Command} ";
            text.AppendLine(head.PadRight(Rule.Length, '='));

            if (command.Problem is { Length: > 0 })
                text.AppendLine($"[{command.Problem}]");

            // 出力が空でも区画は出す(「打ったが何も返らなかった」ことが情報になる)
            if (command.Output.Length > 0)
            {
                text.AppendLine(command.Output.TrimEnd('\n'));
            }

            int lines = command.Output.Length == 0
                ? 0
                : command.Output.TrimEnd('\n').Split('\n').Length;

            text.AppendLine($"（{command.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒 / {lines} 行）");
            text.AppendLine();
        }

        // 生の会話を最後に付ける。区画のずれや取りこぼしは、これが無いと切り分けられない
        // （表の元になった文字を残す。WLC の「取得した出力」と同じ考え方）
        if (result.Transcript is { Length: > 0 })
        {
            text.AppendLine(Rule);
            text.AppendLine("生ログ（機器から届いた文字そのもの。ずれの切り分け用）");
            text.AppendLine(Rule);
            text.AppendLine(result.Transcript.TrimEnd('\n'));
            text.AppendLine();
        }

        // Excel やメモ帳で開くので改行は CRLF に揃える
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)
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

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Describe(TimeSpan elapsed)
        => elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒";
}
