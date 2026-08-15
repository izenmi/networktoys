using System.Globalization;
using System.Text;
using PingWatcher.Core.Metrics;
using PingWatcher.Core.Models;
using PingWatcher.Core.Work;

namespace PingWatcher.Core.Reporting;

/// <summary>
/// 測定結果を 1 ファイル完結の HTML にする。
///
/// 外部 CSS も JavaScript も CDN も使わない。客先のオフライン環境でそのまま開け、
/// メールに添付しても崩れないことを優先する。グラフはインライン SVG。
/// 純粋な文字列生成なので、CI でテストできる。
/// </summary>
public static class HtmlReportWriter
{
    public static string Render(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var html = new StringBuilder(16 * 1024);

        html.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(Escape(data.Title)).Append("</title>\n");
        html.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        html.Append("<h1>").Append(Escape(data.Title)).Append("</h1>\n");

        html.Append("<p class=\"meta\">出力 ")
            .Append(data.GeneratedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture));

        if (data.StartedAt is { } startedAt)
        {
            html.Append(" ／ 測定開始 ")
                .Append(startedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        html.Append(" ／ 間隔 ").Append(data.IntervalMs).Append(" ms</p>\n");

        if (!string.IsNullOrWhiteSpace(data.Note))
            html.Append("<p class=\"note\">").Append(Escape(data.Note)).Append("</p>\n");

        // 判定は最初に出す。開いた瞬間に「合格だったのか、何が引っかかったのか」が分かること
        if (data.Work?.Summary is { } summary)
            AppendVerdict(html, data.Work, summary);

        // 接続環境
        if (data.Environment.Count > 0)
        {
            html.Append("<h2>測定した環境</h2>\n<table class=\"env\">\n");
            foreach ((string label, string value) in data.Environment)
            {
                html.Append("<tr><th>").Append(Escape(label)).Append("</th><td>")
                    .Append(Escape(value)).Append("</td></tr>\n");
            }
            html.Append("</table>\n");
        }

        // 宛先ごとの結果
        html.Append("<h2>測定結果</h2>\n");

        if (data.Rows.Count == 0)
        {
            html.Append("<p class=\"note\">記録された宛先がありません。</p>\n");
        }
        else
        {
            html.Append("<table class=\"result\">\n<thead><tr>")
                .Append("<th>宛先</th><th>IP</th><th>種別</th>")
                .Append("<th class=\"num\">試行</th><th class=\"num\">ロス</th>")
                .Append("<th class=\"num\">最小</th><th class=\"num\">平均</th><th class=\"num\">最大</th>")
                .Append("<th class=\"num\">ジッタ</th>")
                .Append("<th>推移</th><th>備考</th>")
                .Append("</tr></thead>\n<tbody>\n");

            foreach (ReportRow row in data.Rows)
            {
                RttStatistics stats = row.Statistics;
                string lossClass = stats.LossPercent switch
                {
                    <= 0 => "ok",
                    < 5 => "warn",
                    _ => "bad",
                };

                html.Append("<tr>")
                    .Append("<td>").Append(Escape(row.Host)).Append("</td>")
                    .Append("<td class=\"mono\">").Append(Escape(row.Address)).Append("</td>")
                    .Append("<td>").Append(Escape(row.Kind)).Append("</td>")
                    .Append("<td class=\"num\">").Append(stats.Attempts).Append("</td>")
                    .Append("<td class=\"num ").Append(lossClass).Append("\">")
                    .Append(FormatPercent(stats.LossPercent)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMs(stats.MinMs)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMs(stats.AverageMs)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMs(stats.MaxMs)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMs(stats.JitterMs)).Append("</td>")
                    .Append("<td class=\"spark-cell\">").Append(SvgSparkline.Render(row.Samples)).Append("</td>")
                    .Append("<td>").Append(Escape(row.Comment)).Append("</td>")
                    .Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        // いつ落ちていたかは統計の平均値からは読み取れないので、時刻順に別で出す。
        // テキスト版には前からあった節で、HTML だけ欠けていた
        AppendOutages(html, data);

        AppendWireless(html, data);

        if (data.Work is { } work)
            AppendWorkSections(html, work);

        // ipconfig /all の出力をそのまま載せる
        if (!string.IsNullOrWhiteSpace(data.IpConfig))
        {
            html.Append("<h2>ipconfig /all</h2>\n<pre class=\"console\">")
                .Append(Escape(data.IpConfig))
                .Append("</pre>\n");
        }

        html.Append("<p class=\"footnote\">PingWatcher で出力しました。</p>\n");
        html.Append("</body>\n</html>\n");

        return html.ToString();
    }

    /// <summary>不通の記録。機器の再起動やケーブルの抜き差しと突き合わせるため時刻順。</summary>
    private static void AppendOutages(StringBuilder html, ReportData data)
    {
        if (data.Outages is not { Count: > 0 } outages) return;

        html.Append("<h2>不通の記録</h2>\n<table class=\"result\">\n<thead><tr>")
            .Append("<th>宛先</th><th>開始</th><th>終了</th><th>継続</th><th>主な状態</th>")
            .Append("</tr></thead>\n<tbody>\n");

        foreach (OutageRecord outage in outages.OrderBy(o => o.StartedAtTicks))
        {
            string start = outage.StartUnknown
                ? "（開始時刻不明）"
                : outage.StartedAt.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

            string end = outage.EndedAt is { } ended
                ? ended.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "継続中";

            html.Append("<tr>")
                .Append("<td>").Append(Escape(outage.Host)).Append("</td>")
                .Append("<td class=\"mono\">").Append(Escape(start)).Append("</td>")
                .Append("<td class=\"mono\">").Append(Escape(end)).Append("</td>")
                .Append("<td>").Append(Escape(outage.DurationText)).Append("</td>")
                .Append("<td>").Append(Escape(outage.DominantStatus.Describe())).Append("</td>")
                .Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n");
    }

    /// <summary>無線 LAN の情報。テキスト版と同じく、無い理由も書き残す。</summary>
    private static void AppendWireless(StringBuilder html, ReportData data)
    {
        if (data.Wireless is { Count: > 0 } wireless)
        {
            html.Append("<h2>無線 LAN</h2>\n<table class=\"env\">\n");

            foreach ((string label, string value) in wireless)
            {
                html.Append("<tr><th>").Append(Escape(label)).Append("</th><td>")
                    .Append(Escape(value)).Append("</td></tr>\n");
            }

            html.Append("</table>\n");

            if (data.WirelessAccessPoints is { Count: > 0 } accessPoints)
            {
                html.Append("<h2>周辺のアクセスポイント</h2>\n<table class=\"result\">\n<thead><tr>")
                    .Append("<th></th><th>SSID</th><th>BSSID</th>")
                    .Append("<th class=\"num\">信号</th><th class=\"num\">品質</th><th class=\"num\">ch</th>")
                    .Append("<th>帯域</th><th>メーカー</th></tr></thead>\n<tbody>\n");

                foreach (WirelessAccessPoint ap in accessPoints)
                {
                    html.Append("<tr><td>").Append(ap.IsConnected ? "接続中" : "").Append("</td><td>")
                        .Append(Escape(ap.Ssid)).Append("</td><td>")
                        .Append(Escape(ap.Bssid)).Append("</td><td class=\"num\">")
                        .Append(Escape(ap.Rssi)).Append("</td><td class=\"num\">")
                        .Append(Escape(ap.Quality)).Append("</td><td class=\"num\">")
                        .Append(Escape(ap.Channel)).Append("</td><td>")
                        .Append(Escape(ap.Band)).Append("</td><td>")
                        .Append(Escape(ap.Vendor)).Append("</td></tr>\n");
                }

                html.Append("</tbody></table>\n");
            }
        }
        else if (data.WirelessNote is { Length: > 0 } note)
        {
            html.Append("<h2>無線 LAN</h2>\n<p class=\"note\">").Append(Escape(note)).Append("</p>\n");
        }
    }

    /// <summary>合否を最初に、大きく出す。</summary>
    private static void AppendVerdict(StringBuilder html, WorkSection work, WorkSummary summary)
    {
        string cssClass = summary.Failures > 0 ? "bad"
            : summary.Unknowns > 0 ? "unknown"
            : summary.Warnings > 0 ? "warn"
            : "ok";

        html.Append("<div class=\"verdict ").Append(cssClass).Append("\">")
            .Append("<div class=\"verdict-title\">").Append(Escape(summary.Verdict)).Append("</div>")
            .Append("<div class=\"verdict-body\">").Append(Escape(summary.Headline)).Append("</div>");

        html.Append("<div class=\"verdict-counts\">")
            .Append($"対象 {summary.Total} 件")
            .Append($" ／ 問題 {summary.Failures} 件")
            .Append($" ／ 要確認 {summary.Warnings} 件")
            .Append($" ／ 未判定 {summary.Unknowns} 件")
            .Append($" ／ 作業中の不通 {summary.Outages} 件")
            .Append("</div>");

        if (!string.IsNullOrWhiteSpace(work.SessionName))
            html.Append("<div class=\"verdict-counts\">作業: ").Append(Escape(work.SessionName)).Append("</div>");

        html.Append("</div>\n");
    }

    private static void AppendWorkSections(StringBuilder html, WorkSection work)
    {
        if (work.Comparisons.Count > 0)
        {
            html.Append("<h2>作業前後の比較</h2>\n<table class=\"result\">\n<thead><tr>")
                .Append("<th>判定</th><th>宛先</th><th>内容</th>")
                .Append("<th class=\"num\">前 ロス</th><th class=\"num\">後 ロス</th>")
                .Append("<th class=\"num\">前 平均</th><th class=\"num\">後 平均</th><th>備考</th>")
                .Append("</tr></thead>\n<tbody>\n");

            foreach (WorkComparison row in work.Comparisons)
            {
                string level = row.Level switch
                {
                    VerdictLevel.Failure => "bad",
                    VerdictLevel.Warning => "warn",
                    VerdictLevel.Unknown => "unknown",
                    _ => string.Empty,
                };

                html.Append("<tr>")
                    .Append("<td class=\"").Append(level).Append("\">")
                    .Append(Escape(BaselineComparer.Label(row.Verdict))).Append("</td>")
                    .Append("<td class=\"mono\">").Append(Escape(row.Host)).Append("</td>")
                    .Append("<td>").Append(Escape(row.Detail)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatPercentOrDash(row.Before?.LossPercent)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatPercentOrDash(row.After?.LossPercent)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMsOrDash(row.Before?.AverageMs)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMsOrDash(row.After?.AverageMs)).Append("</td>")
                    .Append("<td>").Append(Escape(row.Comment)).Append("</td>")
                    .Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        if (work.Timeline.Count > 0)
        {
            html.Append("<h2>作業中の記録</h2>\n<table class=\"result\">\n<thead><tr>")
                .Append("<th>時刻</th><th>種別</th><th>内容</th></tr></thead>\n<tbody>\n");

            foreach (WorkTimelineItem item in work.Timeline)
            {
                html.Append("<tr>")
                    .Append("<td class=\"mono\">").Append(item.At.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("</td>")
                    .Append("<td>").Append(Escape(item.Label)).Append("</td>")
                    .Append("<td>").Append(Escape(item.Text)).Append("</td>")
                    .Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        if (work.NeighborChanges.Count > 0)
        {
            html.Append("<h2>近隣キャッシュの変化</h2>\n<ul class=\"plain\">\n");

            foreach (NeighborChange change in work.NeighborChanges)
            {
                html.Append("<li")
                    .Append(change.IsGateway ? " class=\"warn\"" : string.Empty)
                    .Append('>')
                    .Append(Escape(change.Description))
                    .Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        AppendDiff(html, "ipconfig /all の差分", work.IpConfigDiff);
        AppendDiff(html, "経路表の差分", work.RouteDiff);
    }

    private static void AppendDiff(StringBuilder html, string title, TextDiffResult? diff)
    {
        if (diff is null) return;

        html.Append("<h2>").Append(Escape(title)).Append("</h2>\n");

        if (diff.TooLarge)
        {
            html.Append("<p class=\"note\">出力が大きすぎるため差分を取れませんでした。</p>\n");
            return;
        }

        if (!diff.HasChanges)
        {
            html.Append("<p class=\"note\">変化はありません")
                .Append(diff.IgnoredLines > 0 ? $"（変動しやすい {diff.IgnoredLines} 行は除いています）" : string.Empty)
                .Append("。</p>\n");
            return;
        }

        html.Append("<pre class=\"console diff\">");

        foreach (DiffLine line in diff.Lines)
        {
            string prefix = line.Kind switch
            {
                DiffKind.Added => "+ ",
                DiffKind.Removed => "- ",
                _ => "  ",
            };

            string cssClass = line.Kind switch
            {
                DiffKind.Added => "added",
                DiffKind.Removed => "removed",
                _ => string.Empty,
            };

            if (cssClass.Length > 0)
                html.Append("<span class=\"").Append(cssClass).Append("\">");

            html.Append(Escape(prefix + line.Text)).Append('\n');

            if (cssClass.Length > 0)
                html.Append("</span>");
        }

        html.Append("</pre>\n");
    }

    private static string FormatMsOrDash(double? value) => value is { } v ? FormatMs(v) : "—";

    private static string FormatPercentOrDash(double? value) => value is { } v ? FormatPercent(v) : "—";

    private static string FormatMs(double value) => value <= 0
        ? "—"
        : value < 10
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + " ms"
            : value.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string FormatPercent(double percent) => percent switch
    {
        <= 0 => "0%",
        < 1 => "&lt;1%",
        _ => percent.ToString("0", CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>HTML に流し込む前の無害化。備考には何が書かれるか分からない。</summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length + 16);

        foreach (char c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    // アプリの配色をそのまま持ち込む。淡い地に濃い文字のペアを崩さない
    private const string Css = """
        :root {
          --bg: #FDFBF7; --surface: #FFFFFF; --alt: #F7F3FB; --border: #EFE9F4;
          --text: #4A4458; --muted: #8B839B;
          --ok-bg: #A8E6CF; --ok-fg: #2E7D5B;
          --warn-bg: #FFE0B2; --warn-fg: #A9631B;
          --bad-bg: #FFC9C6; --bad-fg: #B8433C;
          --accent-bg: #C7CEEA; --accent-fg: #4A54A0;
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; padding: 28px 24px 40px;
          background: var(--bg); color: var(--text);
          font-family: "Yu Gothic UI", "Meiryo UI", "Hiragino Sans", sans-serif;
          font-size: 13px; line-height: 1.6;
        }
        h1 { font-size: 19px; margin: 0 0 4px; }
        h2 { font-size: 15px; margin: 26px 0 8px; }
        .meta, .note, .footnote { color: var(--muted); font-size: 12px; margin: 0 0 4px; }
        .note { color: var(--text); background: var(--alt); padding: 8px 10px; border-radius: 8px; margin: 10px 0; }
        .footnote { margin-top: 18px; }
        table { border-collapse: collapse; background: var(--surface); border-radius: 8px; overflow: hidden; }
        table.env th { text-align: left; color: var(--muted); font-weight: normal; padding: 4px 14px 4px 0; white-space: nowrap; }
        table.env td { padding: 4px 0; font-family: Consolas, monospace; }
        table.result { width: 100%; border: 1px solid var(--border); }
        table.result th, table.result td { padding: 5px 8px; border-bottom: 1px solid var(--border); text-align: left; }
        table.result thead th { background: var(--alt); color: var(--muted); font-weight: 600; white-space: nowrap; }
        table.result tbody tr:last-child td { border-bottom: none; }
        .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        .mono { font-family: Consolas, monospace; }
        .ok { color: var(--ok-fg); }
        .warn { color: var(--warn-fg); }
        .bad { color: var(--bad-fg); font-weight: 600; }
        .grade { display: block; font-size: 10px; color: var(--muted); }
        .spark-cell { width: 250px; }
        .spark { display: block; width: 240px; height: 40px; }
        pre.console {
          background: var(--surface); border: 1px solid var(--border); border-radius: 8px;
          padding: 12px 14px; overflow-x: auto; white-space: pre;
          font-family: Consolas, "BIZ UDGothic", monospace; font-size: 12px; line-height: 1.45;
        }
        pre.diff .added { background: #E7F6EE; color: var(--ok-fg); display: block; }
        pre.diff .removed { background: #FBE9E7; color: var(--bad-fg); display: block; }
        .verdict {
          border-radius: 10px; padding: 14px 16px; margin: 14px 0 6px;
          border: 1px solid var(--border);
        }
        .verdict-title { font-size: 20px; font-weight: 700; }
        .verdict-body { font-size: 14px; margin-top: 2px; }
        .verdict-counts { font-size: 12px; margin-top: 6px; opacity: 0.85; }
        .verdict.ok { background: var(--ok-bg); color: var(--ok-fg); }
        .verdict.warn { background: var(--warn-bg); color: var(--warn-fg); }
        .verdict.bad { background: var(--bad-bg); color: var(--bad-fg); }
        .verdict.unknown { background: var(--alt); color: var(--text); }
        td.unknown, li.warn { color: var(--warn-fg); }
        td.unknown { font-weight: 600; }
        ul.plain { margin: 6px 0; padding-left: 20px; }
        ul.plain li { margin-bottom: 3px; }
        @media print {
          body { background: #fff; padding: 0; }
          table.result { border-color: #ddd; }
        }
        """;
}
