namespace PingWatcher.Core.Work;

/// <summary>
/// 作業前・作業後それぞれで控えた 1 宛先分の状態。
/// </summary>
/// <param name="Key">Host|Kind|Port。<b>Target.Id は宛先テキストを読み直すたびに変わるので鍵にしない。</b></param>
/// <param name="Host">宛先。</param>
/// <param name="Kind">実際に測った方法（"ICMP" / "TCP:443"）。<b>登録上の種別ではなく実効の測り方を入れる。</b></param>
/// <param name="Address">名前解決の結果。</param>
/// <param name="Comment">備考。後から見て思い出すのに一番効く。</param>
/// <param name="Responded">応答があったか。</param>
/// <param name="Attempts">試行回数。少なすぎるベースラインを弾くのに使う。</param>
/// <param name="LossPercent">ロス率。</param>
/// <param name="AverageMs">平均 RTT。</param>
/// <param name="P95Ms">95 パーセンタイル RTT。平均は裾を隠すので併せて見る。</param>
/// <param name="MaxConsecutiveFailures">最も長く続いた連続失敗。</param>
public sealed record WorkEntry(
    string Key,
    string Host,
    string Kind,
    string Address,
    string Comment,
    bool Responded,
    int Attempts,
    double LossPercent,
    double AverageMs,
    double P95Ms,
    int MaxConsecutiveFailures);

/// <summary>作業前／作業後のひとまとまり。</summary>
/// <param name="CapturedAt">控えた時刻。時差や夏時間で意味が壊れないよう Offset 付きで持つ。</param>
/// <param name="Note">そのときのメモ。</param>
/// <param name="IntervalMs">測定間隔。</param>
/// <param name="Entries">宛先ごとの状態。</param>
public sealed record WorkSnapshot(
    DateTimeOffset CapturedAt,
    string Note,
    int IntervalMs,
    IReadOnlyList<WorkEntry> Entries);

/// <param name="At">打った時刻。</param>
/// <param name="Text">何をしたか。</param>
public sealed record WorkMarker(DateTimeOffset At, string Text);

/// <summary>
/// 1 回の変更作業。
///
/// <b>ベースラインを 1 つだけ持つ形にはしない。</b>「作業前として記録」の押し間違いで
/// 前の記録が消えると取り返しがつかないし、1 日に複数の作業をするなら、
/// 次の作業の「前」は前の作業の「後」ではない。
/// </summary>
public sealed class WorkSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? EndedAt { get; set; }

    public WorkSnapshot? Before { get; set; }

    public WorkSnapshot? After { get; set; }

    public List<WorkMarker> Markers { get; set; } = [];

    /// <summary>作業中に観測した不通。</summary>
    public List<OutageRecord> Outages { get; set; } = [];

    /// <summary>作業前後の環境（ipconfig / route / 近隣キャッシュ）。</summary>
    public EnvironmentSnapshot? EnvBefore { get; set; }

    public EnvironmentSnapshot? EnvAfter { get; set; }

    public bool HasBefore => Before is not null;

    public bool HasAfter => After is not null;
}

/// <param name="CapturedAt">取得時刻。</param>
/// <param name="IpConfig">ipconfig /all の出力。取得に失敗していれば null。</param>
/// <param name="Routes">route print -4 の出力。</param>
/// <param name="Neighbors">近隣キャッシュ（IP → MAC）。<c>arp -a</c> ではなく iphlpapi から取る。</param>
public sealed record EnvironmentSnapshot(
    DateTimeOffset CapturedAt,
    string? IpConfig,
    string? Routes,
    IReadOnlyDictionary<string, string> Neighbors);

/// <summary>sessions フォルダに置く 1 ファイル分。</summary>
public sealed class WorkSessionDocument
{
    public int Version { get; set; } = 1;

    public WorkSession Session { get; set; } = new();
}
