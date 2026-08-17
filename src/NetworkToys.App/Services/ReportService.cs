using System.IO;
using System.Text;
using NetworkToys.App.ViewModels;
using NetworkToys.Core.Metrics;
using NetworkToys.Core.Models;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Work;

namespace NetworkToys.App.Services;

/// <summary>測定結果をレポートの形に組み立てて書き出す。</summary>
internal static class ReportService
{
    /// <summary>レポートのスパークラインに描く点数。多すぎても紙の上では読めない。</summary>
    private const int SparklinePoints = 60;

    /// <summary>
    /// 書き出す測定の 1 まとまり。
    ///
    /// <b>Ping と TCP は別の画面で、種別の言い表し方も別に持っている。</b>
    /// 1 つの記録に混ぜるには組ごとに受け取るしかない
    /// （<see cref="MonitorViewModel.DescribeEffectiveKind"/> は画面ごとに答えが違う）。
    /// </summary>
    /// <param name="Rows">その画面の行。</param>
    /// <param name="DescribeKind">その画面での種別の言い表し方。</param>
    internal sealed record ReportSource(
        IEnumerable<TargetRowViewModel> Rows,
        Func<Target, string>? DescribeKind = null);

    public static ReportData Build(
        string title,
        string note,
        IReadOnlyList<ReportSource> sources,
        NetworkSnapshot network,
        DateTime? startedAt,
        int intervalMs,
        string? ipConfig = null,
        IReadOnlyList<(string Label, string Value)>? wireless = null,
        IReadOnlyList<OutageRecord>? outages = null,
        string? wirelessNote = null,
        IReadOnlyList<WirelessAccessPoint>? wirelessAccessPoints = null,
        IReadOnlyList<NetworkToys.Core.Verify.CheckResult>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var reportRows = new List<ReportRow>();
        var buffer = new ProbeSample[SparklinePoints];

        foreach ((IEnumerable<TargetRowViewModel> rows, Func<Target, string>? describeKind) in sources)
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

            reportRows.Add(new ReportRow(
                row.Host,
                row.Address,
                row.Comment,
                DescribeKind(row.Target, describeKind),
                stats,
                samples,
                DescribeState(row.State),
                row.IsDown));
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
            ipConfig,
            wireless,
            outages,
            wirelessNote,
            wirelessAccessPoints,
            checks);
    }

    /// <summary>
    /// 実際に測っている方法。呼び出し側から渡してもらう。
    /// <c>Target.Kind</c> を直接見ると、TCP Ping で測っている最中も「ICMP」と出てしまう。
    /// </summary>
    /// <summary>状態の言い換え。画面と記録で同じ言葉を使う。</summary>
    private static string DescribeState(RowState state) => state switch
    {
        RowState.Ok => "応答",
        RowState.Down => "不通",
        RowState.Refused => "拒否",
        RowState.Unresolved => "名前を引けない",
        RowState.Stalled => "測定停止中",
        _ => "未測定",
    };

    private static string DescribeKind(Target target, Func<Target, string>? describe)
        => describe?.Invoke(target) ?? (target.Kind == ProbeKind.Tcp ? $"TCP:{target.Port}" : "ICMP");

    /// <summary>HTML は meta charset を持つので BOM は付けない。</summary>
    public static void SaveHtml(string path, ReportData data)
        => File.WriteAllText(path, HtmlReportWriter.Render(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>CSV は BOM を付ける。無いと日本語版 Excel で文字化けする。</summary>
    public static void SaveCsv(string path, ReportData data)
        => File.WriteAllText(path, CsvReportWriter.Render(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>
    /// テキストも BOM を付ける。メモ帳で開いたときに日本語が化けないようにする。
    /// 改行は CRLF にする。UNIX 改行のままだと古いメモ帳で 1 行に潰れる。
    /// </summary>
    public static void SaveText(string path, ReportData data)
    {
        string text = TextReportWriter.Render(data).ReplaceLineEndings("\r\n");

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>保存ダイアログの既定ファイル名。</summary>
    public static string SuggestFileName(string extension)
        => $"NetworkToys-{DateTime.Now:yyyyMMdd-HHmm}.{extension}";
}
