using System.Globalization;
using System.Text;

namespace NetworkToys.Core.Reporting;

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

        // 試験だけの記録では、空の見出し行だけを置かない
        if (data.HasRows || !data.HasChecks)
        {
            AppendRow(csv,
                "宛先", "IP", "種別", "備考",
                "試行", "成功", "ロス率(%)",
                "最小(ms)", "平均(ms)", "最大(ms)", "95%(ms)", "ジッタ(ms)");
        }

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
                Number(row.Statistics.P95Ms),
                Number(row.Statistics.JitterMs));
        }

        AppendOutages(csv, data);
        AppendChecks(csv, data);

        return csv.ToString();
    }

    /// <summary>
    /// 業務確認試験の結果を<b>もう 1 つの表として</b>続ける。
    ///
    /// 列の意味が測定とは全く違うので、空行で区切って別の表にする
    /// （不通の記録と同じ扱い）。試験成績書へはこの表をそのまま貼れる。
    /// </summary>
    private static void AppendChecks(StringBuilder csv, ReportData data)
    {
        if (data.Checks is not { Count: > 0 } checks) return;

        // 先頭の表が無いときは、余計な空行で始めない
        if (csv.Length > 0) csv.Append("\r\n");

        AppendRow(csv, "[業務確認試験]");

        Work.CsvTable table = Verify.CheckReport.ToCsv(checks);

        AppendRow(csv, [.. table.Headers]);

        foreach (string[] row in table.Rows)
            AppendRow(csv, row);
    }

    /// <summary>
    /// 不通の記録を<b>2 つ目の表として</b>続ける。
    ///
    /// 宛先ごとの統計とは行の意味が違う（1 宛先に何回も起きる）ので、
    /// 空行で区切って別の表にする。Excel はこの形をそのまま読める。
    ///
    /// いつ落ちていたかは平均値からは読み取れない。機器の再起動やケーブルの
    /// 抜き差しと突き合わせるのに要るので、<b>表計算で扱えるのはむしろこちら</b>。
    /// HTML とテキストには前から入っていたが、CSV だけ抜けていた。
    /// </summary>
    private static void AppendOutages(StringBuilder csv, ReportData data)
    {
        if (data.Outages is not { Count: > 0 } outages) return;

        csv.Append("\r\n");
        AppendRow(csv, "[不通の記録]");
        AppendRow(csv, "宛先", "始まり", "終わり", "続いた秒数", "失敗回数", "状態");

        foreach (Work.OutageRecord outage in outages.OrderBy(o => o.StartedAtTicks))
        {
            AppendRow(csv,
                outage.Host,
                outage.StartUnknown ? "測定開始より前" : Time(outage.StartedAt),
                outage.EndedAt is { } ended ? Time(ended) : "継続中",
                outage.EndedAt is { } end
                    ? ((end - outage.StartedAt).TotalSeconds).ToString("0", CultureInfo.InvariantCulture)
                    : "",
                outage.MissedProbes.ToString(CultureInfo.InvariantCulture),
                outage.DominantStatus.ToString());
        }
    }

    private static string Time(DateTime at)
        => at.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

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

    /// <summary>
    /// カンマ・引用符・改行を含む値だけ囲む。
    ///
    /// あわせて、数式として解釈される先頭文字を <c>'</c> で無害化する。
    /// 宛先名と備考はユーザー入力（配布されたリストのこともある）で、
    /// <c>=HYPERLINK(...)</c> や <c>=cmd|...</c> をそのまま出すと、
    /// この CSV を開いた側の Excel で評価されてしまう。
    /// </summary>
    internal static string Quote(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;

        if (field[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            field = "'" + field;

        bool needsQuotes = field.Contains(',', StringComparison.Ordinal)
                           || field.Contains('"', StringComparison.Ordinal)
                           || field.Contains('\n', StringComparison.Ordinal)
                           || field.Contains('\r', StringComparison.Ordinal);

        if (!needsQuotes) return field;

        return '"' + field.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
