using System.Globalization;
using System.Text;
using PastelNet.Core.Metrics;
using PastelNet.Core.Models;
using PastelNet.Core.Work;

namespace PastelNet.Core.Reporting;

/// <summary>
/// 記録をテキストで書き出す。
///
/// HTML は見栄えがするが、報告書に貼る・メールの本文に入れる・チケットに残す、
/// といった使い方にはテキストの方が早い。等幅で開く前提で桁を揃える。
///
/// <b>状態の記号は ASCII に寄せている。</b>●▲✕ の類は環境によって幅が変わり、
/// 表が崩れる。[NG] のような書き方なら幅が動かず、後から grep もできる。
/// </summary>
public static class TextReportWriter
{
    private const string Rule = "================================================================================";
    private const string ThinRule = "--------------------------------------------------------------------------------";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Render(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var text = new StringBuilder();

        WriteHeader(text, data);
        WriteVerdict(text, data);
        WriteDownRows(text, data);
        WriteOutages(text, data);
        WriteRows(text, data);
        WriteEnvironment(text, data);
        WriteWireless(text, data);
        WriteWork(text, data);
        WriteIpConfig(text, data);

        return text.ToString();
    }

    private static void WriteHeader(StringBuilder text, ReportData data)
    {
        text.AppendLine(Rule);
        text.AppendLine($" {data.Title}");
        text.AppendLine(Rule);
        text.AppendLine($" 出力日時 : {data.GeneratedAt:yyyy/MM/dd HH:mm:ss}");

        if (data.StartedAt is { } started)
            text.AppendLine($" 測定開始 : {started:yyyy/MM/dd HH:mm:ss}（{data.IntervalMs} ms 間隔）");

        if (data.Note.Length > 0)
        {
            text.AppendLine(" メモ     :");
            foreach (string line in SplitLines(data.Note))
                text.AppendLine($"   {line}");
        }

        text.AppendLine();
    }

    /// <summary>
    /// まず結論を出す。長い表を読ませる前に、確認すべき件数が分かるようにする。
    /// </summary>
    private static void WriteVerdict(StringBuilder text, ReportData data)
    {
        int down = data.DownRows.Count;
        int lossy = data.LossyRows.Count;

        text.AppendLine("[判定]");
        text.AppendLine(ThinRule);
        text.AppendLine(down > 0
            ? $"  ★ 応答なし {down} 件 — 確認が必要です"
            : "  応答なし 0 件");
        text.AppendLine(lossy > 0
            ? $"  ! 途中で応答が途切れた宛先 {lossy} 件"
            : "  途中で応答が途切れた宛先 0 件");
        text.AppendLine($"  正常 {data.HealthyCount} 件 / 全 {data.Rows.Count} 件");
        text.AppendLine();
    }

    /// <summary>応答が無かった宛先だけを、探さずに済む場所へ抜き出す。</summary>
    private static void WriteDownRows(StringBuilder text, ReportData data)
    {
        if (data.DownRows.Count == 0 && data.LossyRows.Count == 0)
            return;

        text.AppendLine("[応答に問題があった宛先]");
        text.AppendLine(ThinRule);

        foreach (ReportRow row in data.DownRows)
            WriteProblemRow(text, row, "[NG]");

        foreach (ReportRow row in data.LossyRows)
            WriteProblemRow(text, row, "[!!]");

        text.AppendLine();
    }

    private static void WriteProblemRow(StringBuilder text, ReportRow row, string mark)
    {
        RttStatisticsView stats = Describe(row);

        text.Append("  ").Append(mark).Append(' ')
            .Append(TextWidth.Cell(row.Host, 24)).Append(' ')
            .Append(TextWidth.Cell(row.Address, 16)).Append(' ')
            .Append(TextWidth.PadLeft(stats.Loss, 8)).Append("  ")
            .Append($"成功 {stats.Successes} / 試行 {stats.Attempts}");

        if (row.Comment.Length > 0)
            text.Append("  ").Append(row.Comment);

        text.AppendLine();
    }

    /// <summary>
    /// いつ落ちていたか。統計の平均値からは読み取れないので別に並べる。
    /// 時刻順に出すのは、機器の再起動やケーブルの抜き差しと突き合わせるため。
    /// </summary>
    private static void WriteOutages(StringBuilder text, ReportData data)
    {
        if (data.Outages is not { Count: > 0 } outages)
            return;

        text.AppendLine("[不通の記録]");
        text.AppendLine(ThinRule);

        foreach (OutageRecord outage in outages.OrderBy(o => o.StartedAtTicks))
        {
            string start = outage.StartUnknown
                ? "（開始時刻不明）"
                : outage.StartedAt.ToString("HH:mm:ss", Culture);

            string end = outage.EndedAt is { } ended
                ? ended.ToString("HH:mm:ss", Culture)
                : "継続中";

            text.Append("  ")
                .Append(TextWidth.Cell(outage.Host, 24)).Append(' ')
                .Append(TextWidth.Pad($"{start} → {end}", 24)).Append(' ')
                .Append(TextWidth.Pad(outage.DurationText, 22)).Append(' ')
                .AppendLine(DescribeStatus(outage.DominantStatus));
        }

        text.AppendLine();
    }

    private static void WriteRows(StringBuilder text, ReportData data)
    {
        text.AppendLine("[宛先ごとの結果]");
        text.AppendLine(ThinRule);
        text.AppendLine(
            "  状態 " +
            TextWidth.Cell("宛先", 24) + " " +
            TextWidth.Cell("IP", 16) + " " +
            TextWidth.Cell("方法", 10) + " " +
            TextWidth.PadLeft("ロス", 8) + " " +
            TextWidth.PadLeft("平均", 9) + " " +
            TextWidth.PadLeft("最小", 9) + " " +
            TextWidth.PadLeft("最大", 9) + " " +
            TextWidth.PadLeft("ゆらぎ", 9) + "  備考");

        foreach (ReportRow row in data.Rows)
        {
            RttStatisticsView stats = Describe(row);

            text.Append("  ")
                .Append(MarkOf(row)).Append(' ')
                .Append(TextWidth.Cell(row.Host, 24)).Append(' ')
                .Append(TextWidth.Cell(row.Address, 16)).Append(' ')
                .Append(TextWidth.Cell(row.Kind, 10)).Append(' ')
                .Append(TextWidth.PadLeft(stats.Loss, 8)).Append(' ')
                .Append(TextWidth.PadLeft(stats.Average, 9)).Append(' ')
                .Append(TextWidth.PadLeft(stats.Min, 9)).Append(' ')
                .Append(TextWidth.PadLeft(stats.Max, 9)).Append(' ')
                .Append(TextWidth.PadLeft(stats.Jitter, 9)).Append("  ")
                .AppendLine(row.Comment);
        }

        text.AppendLine();
    }

    private static void WriteEnvironment(StringBuilder text, ReportData data)
    {
        if (data.Environment.Count == 0) return;

        text.AppendLine("[測定した端末の接続環境]");
        text.AppendLine(ThinRule);

        foreach ((string label, string value) in data.Environment)
            text.AppendLine($"  {TextWidth.Cell(label, 20)} {value}");

        text.AppendLine();
    }

    private static void WriteWireless(StringBuilder text, ReportData data)
    {
        if (data.Wireless is not { Count: > 0 } wireless)
        {
            if (data.WirelessNote is { Length: > 0 } note)
            {
                text.AppendLine("[無線 LAN]");
                text.AppendLine(ThinRule);
                text.AppendLine($"  {note}");
                text.AppendLine();
            }

            return;
        }

        text.AppendLine("[無線 LAN]");
        text.AppendLine(ThinRule);

        foreach ((string label, string value) in wireless)
            text.AppendLine($"  {TextWidth.Cell(label, 20)} {value}");

        text.AppendLine();
    }

    private static void WriteWork(StringBuilder text, ReportData data)
    {
        if (data.Work is not { } work) return;

        text.AppendLine("[変更作業の記録]");
        text.AppendLine(ThinRule);

        if (work.SessionName.Length > 0)
            text.AppendLine($"  作業名 : {work.SessionName}");

        if (work.Summary is { } summary)
        {
            text.AppendLine($"  判定   : {summary.Verdict}");
            text.AppendLine($"  内訳   : 異常 {summary.Failures} / 警告 {summary.Warnings} / 不明 {summary.Unknowns}");
        }

        if (work.Comparisons.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  作業前後の比較");

            foreach (WorkComparison comparison in work.Comparisons)
            {
                text.Append("    ")
                    .Append(TextWidth.Cell(comparison.Host, 24)).Append(' ')
                    .Append(TextWidth.Cell(comparison.Label, 14)).Append(' ')
                    .AppendLine(comparison.Detail);
            }
        }

        if (work.Timeline.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  時系列");

            foreach (WorkTimelineItem item in work.Timeline)
            {
                text.Append("    ")
                    .Append(item.At.ToString("HH:mm:ss", Culture)).Append(' ')
                    .Append(TextWidth.Cell(item.Label, 8)).Append(' ')
                    .AppendLine(item.Text);
            }
        }

        text.AppendLine();
    }

    /// <summary>
    /// <c>ipconfig /all</c> はそのまま載せる。読み手が見慣れた形であることに意味があるので、
    /// 整形して崩さない。
    /// </summary>
    private static void WriteIpConfig(StringBuilder text, ReportData data)
    {
        if (data.IpConfig is not { Length: > 0 } ipConfig) return;

        text.AppendLine("[ipconfig /all]");
        text.AppendLine(ThinRule);

        foreach (string line in SplitLines(ipConfig))
            text.AppendLine(line);

        text.AppendLine();
    }

    /// <summary>
    /// 状態の記号。ASCII に寄せているのは、幅が動かないことと、
    /// 後から NG だけを拾えるようにするため。
    /// </summary>
    private static string MarkOf(ReportRow row)
    {
        if (row.IsDown) return "[NG]";
        if (row.HasLoss) return "[!!]";
        if (row.Statistics.Attempts == 0) return "[--]";

        return "[OK]";
    }

    private static string DescribeStatus(ProbeStatus status) => status switch
    {
        ProbeStatus.TimedOut => "無応答",
        ProbeStatus.DnsFailure => "名前を引けない",
        ProbeStatus.Refused => "接続拒否",
        ProbeStatus.Unreachable => "到達不能",
        _ => status.ToString(),
    };

    private static RttStatisticsView Describe(ReportRow row)
    {
        RttStatistics s = row.Statistics;

        return new RttStatisticsView(
            s.Attempts,
            s.Successes,
            s.Attempts == 0 ? "—" : s.LossPercent.ToString("0.0", Culture) + "%",
            s.Successes == 0 ? "—" : s.AverageMs.ToString("0.00", Culture),
            s.Successes == 0 ? "—" : s.MinMs.ToString("0.00", Culture),
            s.Successes == 0 ? "—" : s.MaxMs.ToString("0.00", Culture),
            s.Successes == 0 ? "—" : s.JitterMs.ToString("0.00", Culture));
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
               .Replace('\r', '\n')
               .Split('\n');

    private sealed record RttStatisticsView(
        int Attempts,
        int Successes,
        string Loss,
        string Average,
        string Min,
        string Max,
        string Jitter);
}
