using PingWatcher.Core.Metrics;
using PingWatcher.Core.Work;

namespace PingWatcher.Core.Reporting;

/// <param name="Host">宛先。</param>
/// <param name="Address">解決した IP。</param>
/// <param name="Comment">備考。</param>
/// <param name="Kind">ICMP / TCP:ポート の別。</param>
/// <param name="Statistics">RTT の統計。</param>
/// <param name="Samples">スパークラインに描く RTT の列。</param>
/// <param name="State">
/// 出力した時点の状態（応答／不達 など）。統計だけでは「いま落ちているのか、
/// 途中で一度落ちただけなのか」が読み取れないため、別に持たせる。
/// </param>
/// <param name="IsDown">応答なしと判定されているか。記録では真っ先に見たい。</param>
public sealed record ReportRow(
    string Host,
    string Address,
    string Comment,
    string Kind,
    RttStatistics Statistics,
    IReadOnlyList<double> Samples,
    string State = "",
    bool IsDown = false)
{
    /// <summary>一度でも失敗しているか。応答は戻っていても、記録には残す価値がある。</summary>
    public bool HasLoss => Statistics.Attempts > 0 && Statistics.Successes < Statistics.Attempts;
}

/// <param name="Title">レポートの見出し。</param>
/// <param name="GeneratedAt">出力時刻。</param>
/// <param name="Note">実施者のメモ。</param>
/// <param name="StartedAt">測定開始時刻。null なら出さない。</param>
/// <param name="IntervalMs">測定間隔。</param>
/// <param name="Environment">接続環境（項目名と値の並び）。</param>
/// <param name="Rows">宛先ごとの結果。</param>
/// <param name="IpConfig">
/// <c>ipconfig /all</c> の出力そのまま。現場のレポートには見慣れた形で
/// 載っている方が読み手に伝わるので、整形せずに載せる。
/// </param>
/// <param name="Work">変更作業の記録。作業前後の比較を伴わない測定では null。</param>
/// <param name="Wireless">
/// 無線 LAN の情報（項目名と値の並び）。有線環境や、無線画面を一度も開いて
/// いない場合は null。位置情報の同意が要るため、こちらから勝手には取りに行かない。
/// </param>
/// <param name="Outages">
/// 不通の記録。いつ・どこが落ちていたかは、統計の平均値からは読み取れない。
/// </param>
/// <param name="WirelessNote">無線の情報が無いときの理由。</param>
public sealed record ReportData(
    string Title,
    DateTime GeneratedAt,
    string Note,
    DateTime? StartedAt,
    int IntervalMs,
    IReadOnlyList<(string Label, string Value)> Environment,
    IReadOnlyList<ReportRow> Rows,
    string? IpConfig = null,
    WorkSection? Work = null,
    IReadOnlyList<(string Label, string Value)>? Wireless = null,
    IReadOnlyList<OutageRecord>? Outages = null,
    string? WirelessNote = null)
{
    /// <summary>応答が無いままの宛先。記録の先頭に出す。</summary>
    public IReadOnlyList<ReportRow> DownRows => [.. Rows.Where(r => r.IsDown)];

    /// <summary>応答は戻っているが、途中で落ちていた宛先。</summary>
    public IReadOnlyList<ReportRow> LossyRows => [.. Rows.Where(r => !r.IsDown && r.HasLoss)];

    /// <summary>一度も失敗していない宛先の数。</summary>
    public int HealthyCount => Rows.Count - DownRows.Count - LossyRows.Count;
}

/// <summary>
/// 変更作業の前後で確認した内容。
/// レポートの引数を増やしすぎないよう、ひとまとめにして省略可能な引数として渡す。
/// </summary>
/// <param name="SessionName">作業の名前。</param>
/// <param name="Summary">合否。</param>
/// <param name="Comparisons">宛先ごとの前後比較。</param>
/// <param name="Timeline">マーカーと不通を時刻順に混ぜたもの。</param>
/// <param name="IpConfigDiff">ipconfig の差分。</param>
/// <param name="RouteDiff">経路表の差分。</param>
/// <param name="NeighborChanges">近隣キャッシュの変化。</param>
public sealed record WorkSection(
    string SessionName,
    WorkSummary? Summary,
    IReadOnlyList<WorkComparison> Comparisons,
    IReadOnlyList<WorkTimelineItem> Timeline,
    TextDiffResult? IpConfigDiff,
    TextDiffResult? RouteDiff,
    IReadOnlyList<NeighborChange> NeighborChanges);

/// <param name="At">時刻。</param>
/// <param name="Label">種別（作業／不通）。</param>
/// <param name="Text">内容。</param>
public sealed record WorkTimelineItem(DateTimeOffset At, string Label, string Text);
