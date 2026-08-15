using PingWatcher.App.Mvvm;
using PingWatcher.Core.Metrics;
using PingWatcher.Core.Models;
using PingWatcher.Core.Work;

namespace PingWatcher.App.ViewModels;

/// <summary>一覧の行に出す状態。色と記号はこれで決まる。</summary>
public enum RowState
{
    /// <summary>まだ測っていない。</summary>
    Pending,

    /// <summary>応答あり。</summary>
    Ok,

    /// <summary>応答はあるが遅い。</summary>
    Slow,

    /// <summary>応答なし。</summary>
    Down,

    /// <summary>
    /// TCP で接続を拒否された。ポートは閉じているが<b>ホストは生きている</b>ので、
    /// 無応答とは区別して見せる。
    /// </summary>
    Refused,

    /// <summary>名前を解決できない。</summary>
    Unresolved,

    /// <summary>
    /// 測定結果そのものが届かなくなった。
    /// <b>「応答が無い」とは別物。</b>測定が止まっているのに応答ありと出し続けたら、
    /// 変更作業の確認ツールとしては嘘をつくことになる。
    /// </summary>
    Stalled,
}

/// <summary>
/// 一覧 1 行分。
///
/// 測定結果はこのオブジェクトの<b>プロパティ更新</b>として流れる。
/// コレクション自体は宛先の追加・削除でしか変化しないので、
/// 毎秒のコレクション変更通知が発生しない（数百宛先でも描画が破綻しない理由）。
/// </summary>
public sealed class TargetRowViewModel : ObservableObject
{
    private readonly RingBuffer<ProbeSample> _history;
    private readonly MonitorSettings _settings;
    private readonly ProbeSample[] _scratch;

    /// <summary>
    /// 状態の判定はここが持つ。<b>サンプル 1 件ごとに</b>進めるので、
    /// 1 回の画面更新に複数件届いても数え落とさない。
    /// </summary>
    private readonly ProbeStateMachine _machine = new();

    /// <summary>作業開始以降だけを数える集計。前後比較の統計はこちらを使う。</summary>
    private WindowCounters _window;

    private RttStatistics _statistics = RttStatistics.Empty;
    private RowState _state = RowState.Pending;
    private bool _isDown;
    private string _address = "—";
    private string _latestRtt = "—";
    private string _averageRtt = "—";
    private string _loss = "—";
    private bool _isDirty;

    public TargetRowViewModel(Target target, MonitorSettings settings)
    {
        Target = target;
        _settings = settings;
        _history = new RingBuffer<ProbeSample>(settings.HistoryLength);
        _scratch = new ProbeSample[settings.HistoryLength];
    }

    public Target Target { get; }

    public string Id => Target.Id;
    public string Host => Target.Host;
    public string Comment => Target.Comment;
    public string Group => Target.Group;

    public RowState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>
    /// 2 回続けて応答が無かったか。一覧を「応答あり」「応答なし」に分ける基準。
    /// 1 回きりの取りこぼしで宛先が下段へ飛ぶと、かえって見づらくなる。
    /// </summary>
    public bool IsDown
    {
        get => _isDown;
        private set => SetProperty(ref _isDown, value);
    }

    /// <summary>一覧内の並び順。振り分け後も元の順序を保つために使う。</summary>
    internal int Order { get; set; }

    /// <summary>名前解決の結果。ホスト名で登録された宛先で意味を持つ。</summary>
    public string Address
    {
        get => _address;
        private set => SetProperty(ref _address, value);
    }

    public string LatestRtt
    {
        get => _latestRtt;
        private set => SetProperty(ref _latestRtt, value);
    }

    public string AverageRtt
    {
        get => _averageRtt;
        private set => SetProperty(ref _averageRtt, value);
    }

    public string Loss
    {
        get => _loss;
        private set => SetProperty(ref _loss, value);
    }

    /// <summary>スパークラインへ再描画を促す。UI スレッドからのみ発火する。</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>
    /// 測定結果を取り込む。表示用の文字列はここでは作らず、
    /// <see cref="Refresh"/> でまとめて更新する（1 ティックに複数件届くことがあるため）。
    /// </summary>
    public void Append(in ProbeSample sample, string? resolvedAddress)
    {
        _history.Add(sample);

        // 状態の判定はここで行う。表示のまとめ更新（Refresh）まで待つと、
        // 1 回の画面更新に 2 件以上届いたときに失敗を数え落とす
        _machine.Observe(sample);
        _window.Add(sample);
        _isDirty = true;

        if (!string.IsNullOrEmpty(resolvedAddress))
            Address = resolvedAddress;
    }

    /// <summary>
    /// 測定が止まっていないかを確かめる。止まっていれば表示を変える。
    /// 結果が届かなくなった宛先は Refresh が呼ばれないので、外から定期的に叩く。
    /// </summary>
    public void CheckStalled(long nowTicks)
    {
        // サンプルの実効周期は「間隔」と「タイムアウト」の長い方。応答しない宛先は
        // タイムアウトまで 1 回分の枠を使うので、間隔だけを基準にすると
        // 正常に測れているのに「止まった」と誤判定する
        int cadenceMs = Math.Max(
            Target.IntervalMs ?? _settings.IntervalMs,
            Target.TimeoutMs ?? _settings.TimeoutMs);

        if (_machine.CheckStalled(nowTicks, cadenceMs))
            _isDirty = true;
    }

    /// <summary>取り込んだ結果を表示へ反映する。変化が無ければ何もせず false を返す。</summary>
    public bool Refresh()
    {
        if (!_isDirty) return false;
        _isDirty = false;

        int count = _history.CopyTo(_scratch);
        RttStatistics stats = RttStatistics.Compute(_scratch.AsSpan(0, count));
        ProbeSample latest = _machine.Latest;

        // 一覧には出さない詳しい統計。選択された行だけ画面下に出す
        // （列を増やすと一覧性が落ちるため）
        _statistics = stats;

        State = _machine.State switch
        {
            LinkState.Stalled => RowState.Stalled,
            LinkState.Pending => RowState.Pending,
            LinkState.Down when latest.Status == ProbeStatus.DnsFailure => RowState.Unresolved,
            LinkState.Down => RowState.Down,
            _ => latest.Status switch
            {
                ProbeStatus.Refused => RowState.Refused,
                ProbeStatus.Success when latest.RttMs >= _settings.SlowThresholdMs => RowState.Slow,
                _ => RowState.Ok,
            },
        };

        // 「応答なし」欄への振り分け。測定が詰まって Stalled になっても、
        // 不達だった宛先を「応答あり」欄へ戻さない（最後に分かっていた到達性で振り分ける）
        IsDown = _machine.State == LinkState.Down
                 || (_machine.State == LinkState.Stalled && _machine.StateBeforeStall == LinkState.Down);

        // 拒否のときも RTT は意味を持つ（そこまで届いている証拠なので）
        LatestRtt = latest.Status is ProbeStatus.Success or ProbeStatus.Refused
            ? FormatMilliseconds(latest.RttMs)
            : "—";
        AverageRtt = stats.Successes > 0 ? FormatMilliseconds(stats.AverageMs) : "—";
        Loss = stats.Attempts > 0 ? FormatLoss(stats.LossPercent) : "—";

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// ここから先だけを数え直す。作業の区切りで呼ぶ。
    /// 履歴全体の統計は作業前のサンプルに薄められるので、前後比較には使えない。
    /// </summary>
    public void StartWindow(long nowTicks) => _window.Start(nowTicks);

    /// <summary>作業開始以降の集計。前後比較はこちらの値で行う。</summary>
    internal WindowCounters Window => _window;

    /// <summary>状態機械。不通の判定や連続失敗の回数を見るのに使う。</summary>
    internal ProbeStateMachine Machine => _machine;

    /// <summary>直近の統計。詳細表示に使う。</summary>
    internal RttStatistics Statistics => _statistics;

    /// <summary>
    /// 備考だけが書き換わったときに使う。宛先が同じなら測定も履歴も続けたいので、
    /// 行を作り直さずに文言だけ差し替える。
    /// </summary>
    internal void UpdateComment(string comment)
    {
        if (string.Equals(Target.Comment, comment, StringComparison.Ordinal))
            return;

        Target.Comment = comment;
        OnPropertyChanged(nameof(Comment));
    }

    /// <summary>スパークライン描画用に履歴を書き出す。</summary>
    public int CopyHistory(Span<ProbeSample> destination)
        => _history.CopyLatestTo(destination, destination.Length);

    public void Reset()
    {
        _history.Clear();
        _machine.Reset();
        _window.Start(DateTime.Now.Ticks);
        _isDirty = false;
        IsDown = false;
        State = RowState.Pending;
        LatestRtt = "—";
        AverageRtt = "—";
        Loss = "—";
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>1ms 未満まで見えると桁が揺れて読みにくいので、値域で桁数を変える。</summary>
    internal static string FormatMilliseconds(double value) => value switch
    {
        < 10 => $"{value:0.0} ms",
        < 1000 => $"{value:0} ms",
        _ => $"{value / 1000:0.0} s",
    };

    /// <summary>0.4% を「0%」と出すと「ロスなし」に見えてしまうので区別する。</summary>
    internal static string FormatLoss(double percent) => percent switch
    {
        <= 0 => "0%",
        < 1 => "<1%",
        >= 100 => "100%",
        _ => $"{percent:0}%",
    };
}
