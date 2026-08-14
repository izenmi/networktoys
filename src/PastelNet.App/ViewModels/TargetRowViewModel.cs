using PastelNet.App.Mvvm;
using PastelNet.Core.Metrics;
using PastelNet.Core.Models;

namespace PastelNet.App.ViewModels;

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

    /// <summary>名前を解決できない。</summary>
    Unresolved,
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

    private RowState _state = RowState.Pending;
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
        _isDirty = true;

        if (!string.IsNullOrEmpty(resolvedAddress))
            Address = resolvedAddress;
    }

    /// <summary>取り込んだ結果を表示へ反映する。変化が無ければ何もしない。</summary>
    public void Refresh()
    {
        if (!_isDirty) return;
        _isDirty = false;

        int count = _history.CopyTo(_scratch);
        RttStatistics stats = RttStatistics.Compute(_scratch.AsSpan(0, count));
        ProbeSample latest = _history.Latest;

        State = latest.Status switch
        {
            ProbeStatus.Success when latest.RttMs >= _settings.SlowThresholdMs => RowState.Slow,
            ProbeStatus.Success => RowState.Ok,
            ProbeStatus.DnsFailure => RowState.Unresolved,
            ProbeStatus.Pending => RowState.Pending,
            _ => RowState.Down,
        };

        LatestRtt = latest.Status.IsReachable() ? FormatMilliseconds(latest.RttMs) : "—";
        AverageRtt = stats.Successes > 0 ? FormatMilliseconds(stats.AverageMs) : "—";
        Loss = stats.Attempts > 0 ? FormatLoss(stats.LossPercent) : "—";

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>スパークライン描画用に履歴を書き出す。</summary>
    public int CopyHistory(Span<ProbeSample> destination)
        => _history.CopyLatestTo(destination, destination.Length);

    public void Reset()
    {
        _history.Clear();
        _isDirty = false;
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
