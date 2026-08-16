namespace PingWatcher.Core.Logging;

/// <param name="Facility">ファシリティ（0〜23）。PRI から。</param>
/// <param name="Severity">シビリティ（0=emerg 〜 7=debug）。</param>
/// <param name="SeverityName">シビリティの短い名前。</param>
/// <param name="Message">本文（PRI やヘッダを除いた残り。読めない形は全文）。</param>
/// <param name="HasPriority">先頭に &lt;PRI&gt; があったか。</param>
public readonly record struct SyslogMessage(
    int Facility,
    int Severity,
    string SeverityName,
    string Message,
    bool HasPriority);

/// <summary>
/// syslog（RFC3164/5424）の 1 行をゆるく解釈する。
///
/// <b>解析は例外を投げない。</b>機器ごとに書式がまちまちなので、先頭の
/// <c>&lt;PRI&gt;</c> だけ確実に取り、残りは本文としてそのまま見せる方針。
/// PRI が無ければ全文を本文にし、シビリティは既定（notice 相当）にする。
/// </summary>
public static class SyslogParser
{
    private static readonly string[] SeverityNames =
        ["emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"];

    public static SyslogMessage Parse(string? line)
    {
        string text = (line ?? string.Empty).Trim('\r', '\n');

        // <PRI> は最大 3 桁の数字。無ければそのまま本文
        if (text.StartsWith('<'))
        {
            int close = text.IndexOf('>');
            if (close is > 1 and <= 4
                && int.TryParse(text.AsSpan(1, close - 1), out int pri)
                && pri is >= 0 and <= 191)
            {
                int facility = pri / 8;
                int severity = pri % 8;
                return new SyslogMessage(facility, severity, SeverityNames[severity], text[(close + 1)..].TrimStart(), true);
            }
        }

        // PRI 無し。severity は notice(5) を既定にして、色分けは通常扱いにする
        return new SyslogMessage(1, 5, SeverityNames[5], text, false);
    }

    /// <summary>シビリティの短い名前。範囲外は空文字。</summary>
    public static string NameOf(int severity)
        => severity is >= 0 and < 8 ? SeverityNames[severity] : string.Empty;

    /// <summary>err(3) 以下は「重い」。行の色分けに使う。</summary>
    public static bool IsSevere(int severity) => severity <= 3;

    /// <summary>warning(4) は「注意」。</summary>
    public static bool IsWarning(int severity) => severity == 4;
}
