using PastelNet.Core.Metrics;

namespace PastelNet.Core.Reporting;

/// <param name="Host">宛先。</param>
/// <param name="Address">解決した IP。</param>
/// <param name="Comment">備考。</param>
/// <param name="Kind">ICMP / TCP:ポート の別。</param>
/// <param name="Statistics">RTT の統計。</param>
/// <param name="Mos">推定 MOS 値。</param>
/// <param name="MosGrade">MOS の言い換え。</param>
/// <param name="Samples">スパークラインに描く RTT の列。</param>
public sealed record ReportRow(
    string Host,
    string Address,
    string Comment,
    string Kind,
    RttStatistics Statistics,
    double Mos,
    string MosGrade,
    IReadOnlyList<double> Samples);

/// <param name="Title">レポートの見出し。</param>
/// <param name="GeneratedAt">出力時刻。</param>
/// <param name="Note">実施者のメモ。</param>
/// <param name="StartedAt">測定開始時刻。null なら出さない。</param>
/// <param name="IntervalMs">測定間隔。</param>
/// <param name="Environment">接続環境（項目名と値の並び）。</param>
/// <param name="Rows">宛先ごとの結果。</param>
public sealed record ReportData(
    string Title,
    DateTime GeneratedAt,
    string Note,
    DateTime? StartedAt,
    int IntervalMs,
    IReadOnlyList<(string Label, string Value)> Environment,
    IReadOnlyList<ReportRow> Rows);
