using System.IO;
using System.Text;
using PastelNet.App.ViewModels;
using PastelNet.Core.Metrics;
using PastelNet.Core.Models;
using PastelNet.Core.Quality;
using PastelNet.Core.Reporting;

namespace PastelNet.App.Services;

/// <summary>測定結果をレポートの形に組み立てて書き出す。</summary>
internal static class ReportService
{
    /// <summary>レポートのスパークラインに描く点数。多すぎても紙の上では読めない。</summary>
    private const int SparklinePoints = 60;

    public static ReportData Build(
        string title,
        string note,
        IEnumerable<TargetRowViewModel> rows,
        NetworkSnapshot network,
        DateTime? startedAt,
        int intervalMs,
        string? ipConfig = null)
    {
        var reportRows = new List<ReportRow>();
        var buffer = new ProbeSample[SparklinePoints];

        foreach (TargetRowViewModel row in rows)
        {
            int count = row.CopyHistory(buffer);

            var samples = new double[count];
            for (int i = 0; i < count; i++)
            {
                // 届かなかった点は 0 に落とす。谷として見える方が異常が伝わる
                samples[i] = buffer[i].Status.IsReachable() ? buffer[i].RttMs : 0;
            }

            RttStatistics stats = row.Statistics;
            VoiceQuality quality = MosCalculator.Estimate(stats.AverageMs, stats.JitterMs, stats.LossPercent);

            reportRows.Add(new ReportRow(
                row.Host,
                row.Address,
                row.Comment,
                DescribeKind(row.Target),
                stats,
                quality.Mos,
                quality.Grade,
                samples));
        }

        var environment = new List<(string, string)>();

        if (network.InterfaceName is { } name)
            environment.Add(("インターフェース", name));

        if (network.LocalAddress is { } local)
        {
            environment.Add(("IP アドレス",
                network.PrefixLength > 0 ? $"{local}/{network.PrefixLength}" : local.ToString()));
        }

        if (network.Gateway is { } gateway)
            environment.Add(("既定ゲートウェイ", gateway.ToString()));

        if (network.DnsServers.Count > 0)
            environment.Add(("DNS サーバ", string.Join(", ", network.DnsServers.Select(a => a.ToString()))));

        environment.Add(("測定した端末", Environment.MachineName));

        return new ReportData(
            string.IsNullOrWhiteSpace(title) ? "ネットワーク測定結果" : title.Trim(),
            DateTime.Now,
            note.Trim(),
            startedAt,
            intervalMs,
            environment,
            reportRows,
            ipConfig);
    }

    private static string DescribeKind(Target target)
        => target.Kind == ProbeKind.Tcp ? $"TCP:{target.Port}" : "ICMP";

    /// <summary>HTML は meta charset を持つので BOM は付けない。</summary>
    public static void SaveHtml(string path, ReportData data)
        => File.WriteAllText(path, HtmlReportWriter.Render(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>CSV は BOM を付ける。無いと日本語版 Excel で文字化けする。</summary>
    public static void SaveCsv(string path, ReportData data)
        => File.WriteAllText(path, CsvReportWriter.Render(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>保存ダイアログの既定ファイル名。</summary>
    public static string SuggestFileName(string extension)
        => $"PastelNet-{DateTime.Now:yyyyMMdd-HHmm}.{extension}";
}
