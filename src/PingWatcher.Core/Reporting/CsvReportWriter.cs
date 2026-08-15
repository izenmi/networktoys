using System.Globalization;
using System.Text;

namespace PingWatcher.Core.Reporting;

/// <summary>
/// Excel で開く前提の CSV。
///
/// <b>BOM 付き UTF-8 と CRLF が必須。</b>BOM が無いと日本語版 Excel で文字化けする。
/// 書き出し側で <c>new UTF8Encoding(true)</c> を使うこと。
/// </summary>
public static class CsvReportWriter
{
    public static string Render(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var csv = new StringBuilder();

        AppendRow(csv,
            "宛先", "IP", "種別", "備考",
            "試行", "成功", "ロス率(%)",
            "最小(ms)", "平均(ms)", "最大(ms)", "ジッタ(ms)");

        foreach (ReportRow row in data.Rows)
        {
            AppendRow(csv,
                row.Host,
                row.Address,
                row.Kind,
                row.Comment,
                row.Statistics.Attempts.ToString(CultureInfo.InvariantCulture),
                row.Statistics.Successes.ToString(CultureInfo.InvariantCulture),
                Number(row.Statistics.LossPercent),
                Number(row.Statistics.MinMs),
                Number(row.Statistics.AverageMs),
                Number(row.Statistics.MaxMs),
                Number(row.Statistics.JitterMs));
        }

        return csv.ToString();
    }

    private static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static void AppendRow(StringBuilder csv, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) csv.Append(',');
            csv.Append(Quote(fields[i]));
        }

        // Excel 向けなので改行は CRLF に揃える
        csv.Append("\r\n");
    }

    /// <summary>カンマ・引用符・改行を含む値だけ囲む。</summary>
    internal static string Quote(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;

        bool needsQuotes = field.Contains(',', StringComparison.Ordinal)
                           || field.Contains('"', StringComparison.Ordinal)
                           || field.Contains('\n', StringComparison.Ordinal)
                           || field.Contains('\r', StringComparison.Ordinal);

        if (!needsQuotes) return field;

        return '"' + field.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
