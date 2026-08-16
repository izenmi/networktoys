using System.Globalization;
using System.Text;
using PingWatcher.Core.Metrics;
using PingWatcher.Core.Models;
using PingWatcher.Core.Work;   // OutageRecord

namespace PingWatcher.Core.Reporting;

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
        WriteIpConfig(text, data);

        return text.ToString();
    }

    private static void WriteHeader(StringBuilder text, ReportData data)
    {
        text.AppendLine(Rule);
        text.AppendLine($" {data.Title}");
        text.AppendLine(Rule);
        // 書式は必ず Culture を通す。既定カルチャ任せにすると、和暦や仏暦を
        // 既定にしている環境で年が「0008」「2569」になる
        text.AppendLine($" 出力日時 : {data.GeneratedAt.ToString("yyyy/MM/dd HH:mm:ss", Culture)}");

        if (data.StartedAt is { } started)
            text.AppendLine($" 測定開始 : {started.ToString("yyyy/MM/dd HH:mm:ss", Culture)}（{data.IntervalMs} ms 間隔）");

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
                .Append(TextWidth.Pad($"{start} -> {end}", 24)).Append(' ')
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

        if (data.WirelessAccessPoints is { Count: > 0 } accessPoints)
        {
            text.AppendLine();
            text.AppendLine("  周辺のアクセスポイント (* = 接続中):");
            WriteAccessPointTable(text, accessPoints, "  ");
        }

        text.AppendLine();
    }

    /// <summary>
    /// 周辺 AP の表。無線タブの「保存」(WifiSnapshotWriter)と共用する。
    /// </summary>
    internal static void WriteAccessPointTable(StringBuilder text, IReadOnlyList<WirelessAccessPoint> accessPoints, string indent)
    {
        text.AppendLine(
            indent + "  " +
            TextWidth.Cell("SSID", 24) + " " +
            TextWidth.Cell("BSSID", 18) + " " +
            TextWidth.PadLeft("信号", 8) + " " +
            TextWidth.PadLeft("品質", 6) + " " +
            TextWidth.PadLeft("ch", 4) + " " +
            TextWidth.Cell("帯域", 8) + " " +
            "メーカー");

        foreach (WirelessAccessPoint ap in accessPoints)
        {
            text.Append(indent)
                .Append(ap.IsConnected ? "* " : "  ")
                .Append(TextWidth.Cell(ap.Ssid, 24)).Append(' ')
                .Append(TextWidth.Cell(ap.Bssid, 18)).Append(' ')
                .Append(TextWidth.PadLeft(ap.Rssi, 8)).Append(' ')
                .Append(TextWidth.PadLeft(ap.Quality, 6)).Append(' ')
                .Append(TextWidth.PadLeft(ap.Channel, 4)).Append(' ')
                .Append(TextWidth.Cell(ap.Band, 8)).Append(' ')
                .AppendLine(ap.Vendor);
        }
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

    private static string DescribeStatus(ProbeStatus status) => status.Describe();

    private static RttStatisticsView Describe(ReportRow row)
    {
        RttStatistics s = row.Statistics;

        return new RttStatisticsView(
            s.Attempts,
            s.Successes,
            s.Attempts == 0 ? "-" : s.LossPercent.ToString("0.0", Culture) + "%",
            s.Successes == 0 ? "-" : s.AverageMs.ToString("0.00", Culture),
            s.Successes == 0 ? "-" : s.MinMs.ToString("0.00", Culture),
            s.Successes == 0 ? "-" : s.MaxMs.ToString("0.00", Culture),
            s.Successes == 0 ? "-" : s.JitterMs.ToString("0.00", Culture));
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
