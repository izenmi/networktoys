using System.Globalization;
using System.Text;
using NetworkToys.Core.Metrics;
using NetworkToys.Core.Models;
using NetworkToys.Core.Verify;
using NetworkToys.Core.Work;   // OutageRecord

namespace NetworkToys.Core.Reporting;

/// <summary>
/// 測定結果と業務確認試験の結果を、1 ファイル完結の HTML にする。
///
/// 外部 CSS も JavaScript も CDN も使わない。客先のオフライン環境でそのまま開け、
/// メールに添付しても崩れないことを優先する。グラフはインライン SVG。
/// 純粋な文字列生成なので、CI でテストできる。
///
/// <b>先頭に結論を出す。</b>報告書として人に渡すものなので、長い表を読ませる前に
/// 「確認すべきものが何件あるか」が分かる形にする。
/// </summary>
public static class HtmlReportWriter
{
    public static string Render(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var html = new StringBuilder(24 * 1024);

        html.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(Escape(data.Title)).Append("</title>\n");
        html.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        AppendHero(html, data);

        html.Append("<main>\n");

        if (!string.IsNullOrWhiteSpace(data.Note))
            html.Append("<p class=\"note\">").Append(Escape(data.Note)).Append("</p>\n");

        AppendChecks(html, data);
        AppendRows(html, data);

        // いつ落ちていたかは統計の平均値からは読み取れないので、時刻順に別で出す
        AppendOutages(html, data);

        AppendEnvironment(html, data);
        AppendWireless(html, data);

        // ipconfig /all の出力をそのまま載せる
        if (!string.IsNullOrWhiteSpace(data.IpConfig))
        {
            html.Append("<section>\n<h2>ipconfig /all</h2>\n<pre class=\"console\">")
                .Append(Escape(data.IpConfig))
                .Append("</pre>\n</section>\n");
        }

        html.Append("</main>\n");
        html.Append("<footer>NetworkToys で出力しました。</footer>\n");
        html.Append("</body>\n</html>\n");

        return html.ToString();
    }

    // ===== 先頭（表題・出力条件・結論） =====

    private static void AppendHero(StringBuilder html, ReportData data)
    {
        html.Append("<header class=\"hero\">\n<div class=\"hero-head\">\n");
        html.Append("<h1>").Append(Escape(data.Title)).Append("</h1>\n<p class=\"chips\">");

        Chip(html, "出力", data.GeneratedAt.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture));

        if (data.StartedAt is { } startedAt)
            Chip(html, "測定開始", startedAt.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture));

        // 試験だけの記録では間隔に意味が無い。0 ms と書くと誤解させる
        if (data.IntervalMs > 0)
            Chip(html, "間隔", data.IntervalMs.ToString(CultureInfo.InvariantCulture) + " ms");

        html.Append("</p>\n</div>\n");

        AppendScores(html, data);

        html.Append("</header>\n");
    }

    /// <summary>結論。件数だけを大きく出して、詳細は下の表に譲る。</summary>
    private static void AppendScores(StringBuilder html, ReportData data)
    {
        html.Append("<div class=\"scores\">\n");

        if (data.HasRows)
        {
            Score(html, "応答なし", data.DownRows.Count, data.DownRows.Count > 0 ? "bad" : "ok");
            Score(html, "途切れた", data.LossyRows.Count, data.LossyRows.Count > 0 ? "warn" : "ok");
            Score(html, "問題なし", data.HealthyCount, "ok");
        }

        if (data.Checks is { Count: > 0 } checks)
        {
            int fail = checks.Count(c => c.IsFail);
            int warn = checks.Count(c => c.IsWarn);

            Score(html, "試験 不合格", fail, fail > 0 ? "bad" : "ok");
            Score(html, "試験 注意", warn, warn > 0 ? "warn" : "ok");
            Score(html, "試験 合格", checks.Count(c => c.IsPass), "ok");
        }

        html.Append("</div>\n");
    }

    private static void Chip(StringBuilder html, string label, string value)
        => html.Append("<span class=\"chip\"><b>").Append(Escape(label)).Append("</b>")
               .Append(Escape(value)).Append("</span>");

    private static void Score(StringBuilder html, string label, int count, string tone)
        => html.Append("<div class=\"score ").Append(tone).Append("\"><span class=\"n\">")
               .Append(count.ToString(CultureInfo.InvariantCulture))
               .Append("</span><span class=\"l\">").Append(Escape(label)).Append("</span></div>\n");

    // ===== 業務確認試験 =====

    /// <summary>
    /// 試験の結果。<b>合否は記号と文字で書く</b>（色だけで表さない決まりは
    /// 印刷物でも同じで、白黒で刷られると色は消える）。
    /// </summary>
    private static void AppendChecks(StringBuilder html, ReportData data)
    {
        if (data.Checks is not { Count: > 0 } checks) return;

        html.Append("<section>\n<h2>業務確認試験</h2>\n");
        html.Append("<p class=\"lead\">").Append(Escape(CheckReport.Summarize(checks))).Append("</p>\n");

        html.Append("<div class=\"scroll\"><table class=\"result\">\n<thead><tr>")
            .Append("<th>項目</th><th>種類</th><th>宛先</th><th>プロキシ</th>")
            .Append("<th>合否</th><th class=\"num\">所要</th><th>詳細</th>")
            .Append("</tr></thead>\n<tbody>\n");

        // 不合格を上に集める。件数が多いとき、下まで探させない
        foreach (CheckResult check in checks.OrderBy(SortKey))
        {
            html.Append("<tr>")
                .Append("<td>").Append(Escape(check.Name)).Append("</td>")
                .Append("<td>").Append(Escape(CheckListParser.NameOf(check.Kind))).Append("</td>")
                .Append("<td class=\"mono wrap\">").Append(Escape(check.Target)).Append("</td>")
                .Append("<td>").Append(Escape(check.ProxyText)).Append("</td>")
                .Append("<td><span class=\"pill ").Append(ToneOf(check.Verdict)).Append("\">")
                .Append(Escape(check.VerdictText)).Append("</span></td>")
                .Append("<td class=\"num\">").Append(Escape(check.ElapsedText)).Append("</td>")
                .Append("<td class=\"wrap\">").Append(Escape(check.Detail)).Append("</td>")
                .Append("</tr>\n");
        }

        html.Append("</tbody>\n</table></div>\n</section>\n");
    }

    /// <summary>不合格 → 注意 → 目視待ち → 合格 → その他 の順。</summary>
    private static int SortKey(CheckResult check) => check.Verdict switch
    {
        CheckVerdict.Fail => 0,
        CheckVerdict.Warn => 1,
        CheckVerdict.AwaitingPerson => 2,
        CheckVerdict.Pass => 3,
        _ => 4,
    };

    private static string ToneOf(CheckVerdict verdict) => verdict switch
    {
        CheckVerdict.Pass => "ok",
        CheckVerdict.Fail => "bad",
        CheckVerdict.Warn => "warn",
        CheckVerdict.AwaitingPerson => "info",
        _ => "muted",
    };

    // ===== 測定結果 =====

    private static void AppendRows(StringBuilder html, ReportData data)
    {
        // 試験だけの記録では、空の測定表を出さない（抜けに見える）
        if (!data.HasRows)
        {
            if (!data.HasChecks)
                html.Append("<section>\n<h2>測定結果</h2>\n<p class=\"note\">記録された宛先がありません。</p>\n</section>\n");

            return;
        }

        html.Append("<section>\n<h2>測定結果</h2>\n");
        html.Append("<div class=\"scroll\"><table class=\"result\">\n<thead><tr>")
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

            // 落ちている宛先は行ごと目立たせる。表の中で埋もれさせない
            html.Append(row.IsDown ? "<tr class=\"row-bad\">" : "<tr>")
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
                .Append("<td class=\"wrap\">").Append(Escape(row.Comment)).Append("</td>")
                .Append("</tr>\n");
        }

        html.Append("</tbody>\n</table></div>\n</section>\n");
    }

    /// <summary>不通の記録。機器の再起動やケーブルの抜き差しと突き合わせるため時刻順。</summary>
    private static void AppendOutages(StringBuilder html, ReportData data)
    {
        if (data.Outages is not { Count: > 0 } outages) return;

        html.Append("<section>\n<h2>不通の記録</h2>\n")
            .Append("<div class=\"scroll\"><table class=\"result\">\n<thead><tr>")
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

        html.Append("</tbody>\n</table></div>\n</section>\n");
    }

    private static void AppendEnvironment(StringBuilder html, ReportData data)
    {
        if (data.Environment.Count == 0) return;

        html.Append("<section>\n<h2>測定した環境</h2>\n<table class=\"env\">\n");

        foreach ((string label, string value) in data.Environment)
        {
            html.Append("<tr><th>").Append(Escape(label)).Append("</th><td>")
                .Append(Escape(value)).Append("</td></tr>\n");
        }

        html.Append("</table>\n</section>\n");
    }

    /// <summary>無線 LAN の情報。テキスト版と同じく、無い理由も書き残す。</summary>
    private static void AppendWireless(StringBuilder html, ReportData data)
    {
        if (data.Wireless is { Count: > 0 } wireless)
        {
            html.Append("<section>\n<h2>無線 LAN</h2>\n<table class=\"env\">\n");

            foreach ((string label, string value) in wireless)
            {
                html.Append("<tr><th>").Append(Escape(label)).Append("</th><td>")
                    .Append(Escape(value)).Append("</td></tr>\n");
            }

            html.Append("</table>\n</section>\n");

            if (data.WirelessAccessPoints is { Count: > 0 } accessPoints)
            {
                html.Append("<section>\n<h2>周辺のアクセスポイント</h2>\n")
                    .Append("<div class=\"scroll\"><table class=\"result\">\n<thead><tr>")
                    .Append("<th></th><th>SSID</th><th>BSSID</th>")
                    .Append("<th class=\"num\">信号</th><th class=\"num\">品質</th><th class=\"num\">ch</th>")
                    .Append("<th>帯域</th><th>メーカー</th></tr></thead>\n<tbody>\n");

                foreach (WirelessAccessPoint ap in accessPoints)
                {
                    html.Append("<tr><td>")
                        .Append(ap.IsConnected ? "<span class=\"pill info\">接続中</span>" : "")
                        .Append("</td><td>")
                        .Append(Escape(ap.Ssid)).Append("</td><td class=\"mono\">")
                        .Append(Escape(ap.Bssid)).Append("</td><td class=\"num\">")
                        .Append(Escape(ap.Rssi)).Append("</td><td class=\"num\">")
                        .Append(Escape(ap.Quality)).Append("</td><td class=\"num\">")
                        .Append(Escape(ap.Channel)).Append("</td><td>")
                        .Append(Escape(ap.Band)).Append("</td><td>")
                        .Append(Escape(ap.Vendor)).Append("</td></tr>\n");
                }

                html.Append("</tbody></table></div>\n</section>\n");
            }
        }
        else if (data.WirelessNote is { Length: > 0 } note)
        {
            html.Append("<section>\n<h2>無線 LAN</h2>\n<p class=\"note\">")
                .Append(Escape(note)).Append("</p>\n</section>\n");
        }
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

    // アプリの配色をそのまま持ち込む。淡い地に濃い文字のペアを崩さない。
    // 暗い配色で開かれることも増えたので、色は変数にして prefers-color-scheme で差し替える
    private const string Css = """
        :root {
          --bg: #F6F3EF; --surface: #FFFFFF; --alt: #F7F3FB; --border: #E7E0EC;
          --text: #3D3849; --muted: #837C93; --line: #F0EBF5;
          --ok-bg: #E4F6EC; --ok-fg: #237552;
          --warn-bg: #FDF0DC; --warn-fg: #9A5A14;
          --bad-bg: #FDE6E4; --bad-fg: #AF3B34;
          --info-bg: #E7EAF8; --info-fg: #414B96;
          --hero-a: #4A54A0; --hero-b: #7C6BA8;
          --shadow: 0 1px 2px rgba(60, 50, 80, .06), 0 8px 24px rgba(60, 50, 80, .06);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #191722; --surface: #221F2E; --alt: #282438; --border: #362F49;
            --text: #E6E1F0; --muted: #A69EBC; --line: #2E293E;
            --ok-bg: #1D3A2C; --ok-fg: #7FD9AC;
            --warn-bg: #3E2F19; --warn-fg: #F0BC72;
            --bad-bg: #3D2224; --bad-fg: #F2A09A;
            --info-bg: #262A4A; --info-fg: #A9B2EE;
            --hero-a: #3A3F7A; --hero-b: #5A4E80;
            --shadow: 0 1px 2px rgba(0, 0, 0, .3), 0 8px 24px rgba(0, 0, 0, .25);
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; padding: 0 0 48px;
          background: var(--bg); color: var(--text);
          font-family: "Yu Gothic UI", "Meiryo UI", "Hiragino Sans", sans-serif;
          font-size: 13px; line-height: 1.65;
          -webkit-font-smoothing: antialiased;
        }
        main, .hero { max-width: 1180px; margin: 0 auto; padding: 0 24px; }

        /* 先頭。表題と結論をひとまとまりにして、最初の 1 画面で読み切れるようにする */
        .hero {
          max-width: none; padding: 26px 24px 22px;
          background: linear-gradient(120deg, var(--hero-a), var(--hero-b));
          color: #fff; margin-bottom: 24px;
        }
        .hero-head { max-width: 1180px; margin: 0 auto 16px; }
        .hero h1 { font-size: 22px; margin: 0 0 8px; letter-spacing: .02em; font-weight: 600; }
        .chips { margin: 0; display: flex; flex-wrap: wrap; gap: 6px; }
        .chip {
          background: rgba(255, 255, 255, .16); border-radius: 999px;
          padding: 2px 11px; font-size: 11.5px; white-space: nowrap;
        }
        .chip b { font-weight: 600; opacity: .78; margin-right: 6px; }

        .scores {
          max-width: 1180px; margin: 0 auto;
          display: flex; flex-wrap: wrap; gap: 10px;
        }
        .score {
          flex: 1 1 110px; min-width: 110px;
          background: var(--surface); border-radius: 12px;
          padding: 10px 14px; box-shadow: var(--shadow);
          border-left: 4px solid var(--border);
        }
        .score .n { display: block; font-size: 24px; font-weight: 600; line-height: 1.2;
                    font-variant-numeric: tabular-nums; color: var(--text); }
        .score .l { display: block; font-size: 11.5px; color: var(--muted); }
        .score.ok { border-left-color: var(--ok-fg); }
        .score.warn { border-left-color: var(--warn-fg); }
        .score.bad { border-left-color: var(--bad-fg); }
        .score.bad .n { color: var(--bad-fg); }
        .score.warn .n { color: var(--warn-fg); }

        section { margin: 0 0 26px; }
        h2 {
          font-size: 14px; margin: 0 0 10px; font-weight: 600;
          text-transform: none; letter-spacing: .04em;
          padding-left: 10px; border-left: 3px solid var(--hero-a);
        }
        .lead { margin: 0 0 10px; color: var(--muted); }
        .note {
          background: var(--alt); border: 1px solid var(--border);
          padding: 9px 12px; border-radius: 10px; margin: 10px 0;
        }
        footer {
          max-width: 1180px; margin: 26px auto 0; padding: 0 24px;
          color: var(--muted); font-size: 11.5px;
        }

        /* 幅の広い表は表の中だけ横に流す。本文ごと横スクロールさせない */
        .scroll { overflow-x: auto; border-radius: 12px; box-shadow: var(--shadow); }
        table { border-collapse: collapse; background: var(--surface); }
        table.env {
          border-radius: 12px; overflow: hidden; box-shadow: var(--shadow);
          padding: 4px 0;
        }
        table.env th {
          text-align: left; color: var(--muted); font-weight: normal;
          padding: 5px 18px 5px 14px; white-space: nowrap;
        }
        table.env td { padding: 5px 14px 5px 0; font-family: Consolas, monospace; }
        table.result { width: 100%; min-width: 620px; }
        table.result th, table.result td {
          padding: 7px 10px; border-bottom: 1px solid var(--line); text-align: left;
          vertical-align: top;
        }
        table.result thead th {
          background: var(--alt); color: var(--muted); font-weight: 600;
          white-space: nowrap; position: sticky; top: 0;
          border-bottom: 1px solid var(--border);
        }
        table.result tbody tr:last-child td { border-bottom: none; }
        table.result tbody tr:hover { background: var(--alt); }
        tr.row-bad td:first-child { box-shadow: inset 3px 0 0 var(--bad-fg); }
        .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        .mono { font-family: Consolas, monospace; }
        .wrap { word-break: break-word; }
        .ok { color: var(--ok-fg); }
        .warn { color: var(--warn-fg); }
        .bad { color: var(--bad-fg); font-weight: 600; }

        /* 合否のバッジ。色は補助で、記号と文字を必ず併記する（白黒で刷られても読める） */
        .pill {
          display: inline-block; white-space: nowrap; border-radius: 999px;
          padding: 1px 10px; font-size: 11.5px; font-weight: 600;
        }
        .pill.ok { background: var(--ok-bg); color: var(--ok-fg); }
        .pill.warn { background: var(--warn-bg); color: var(--warn-fg); }
        .pill.bad { background: var(--bad-bg); color: var(--bad-fg); }
        .pill.info { background: var(--info-bg); color: var(--info-fg); }
        .pill.muted { background: var(--alt); color: var(--muted); }

        .spark-cell { width: 250px; }
        .spark { display: block; width: 240px; height: 40px; }
        pre.console {
          background: var(--surface); border: 1px solid var(--border); border-radius: 12px;
          padding: 13px 15px; overflow-x: auto; white-space: pre; box-shadow: var(--shadow);
          font-family: Consolas, "BIZ UDGothic", monospace; font-size: 12px; line-height: 1.5;
        }
        ul.plain { margin: 6px 0; padding-left: 20px; }
        ul.plain li { margin-bottom: 3px; }

        @media print {
          body { background: #fff; color: #000; font-size: 11px; }
          .hero { background: none; color: #000; border-bottom: 2px solid #000; padding: 0 0 10px; margin-bottom: 14px; }
          .chip { background: none; border: 1px solid #999; }
          .score, .scroll, table.env, pre.console { box-shadow: none; border: 1px solid #bbb; }
          .scroll { overflow: visible; }
          table.result { min-width: 0; }
          table.result thead th { position: static; }
          section { break-inside: avoid; }
          tr { break-inside: avoid; }
        }
        """;
}
