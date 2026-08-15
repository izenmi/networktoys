using System.Globalization;
using System.Text;
using PastelNet.Core.Metrics;

namespace PastelNet.Core.Reporting;

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
                .Append("<th class=\"num\">p95</th><th class=\"num\">ジッタ</th>")
                .Append("<th class=\"num\">MOS</th><th>推移</th><th>備考</th>")
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
                    .Append("<td class=\"num\">").Append(FormatMs(stats.P95Ms)).Append("</td>")
                    .Append("<td class=\"num\">").Append(FormatMs(stats.JitterMs)).Append("</td>")
                    .Append("<td class=\"num\">").Append(row.Mos.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append("<span class=\"grade\">").Append(Escape(row.MosGrade)).Append("</span></td>")
                    .Append("<td class=\"spark-cell\">").Append(SvgSparkline.Render(row.Samples)).Append("</td>")
                    .Append("<td>").Append(Escape(row.Comment)).Append("</td>")
                    .Append("</tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("<p class=\"footnote\">MOS 値は遅延・ジッタ・損失から算出した<strong>推定値</strong>です")
            .Append("（ITU-T G.107 の E-model の簡易版）。実際の音声品質はコーデックや機器にも左右されます。</p>\n");

        html.Append("<p class=\"footnote\">PastelNet で出力しました。</p>\n");
        html.Append("</body>\n</html>\n");

        return html.ToString();
    }

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
        @media print {
          body { background: #fff; padding: 0; }
          table.result { border-color: #ddd; }
        }
        """;
}
