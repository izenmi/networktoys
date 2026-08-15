using System.Collections.ObjectModel;
using System.Windows.Threading;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Net;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// 接続タブ。PC 上の TCP/UDP 接続をプロセスごとにまとめ、タブ表示中だけ
/// 2 秒間隔で更新する。一覧の反映は <see cref="OrderedListSync"/> の差分適用で、
/// 毎ティックの全再生成（ちらつき・選択消失）を避ける。
/// </summary>
public sealed class ConnectionsViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;
    private readonly ConnectionTableService _service = new();
    private readonly TrafficAggregator _aggregator = new();
    private readonly System.Diagnostics.Stopwatch _rateWatch = new();

    private NetTraceSession? _trace;
    private ConnectionRates? _rates;
    private const string AdminNotice =
        "⚠ 通信量の表示には管理者権限が必要です。管理者として起動し直すと接続ごとの送受信 B/秒 が出ます。";

    private ConnectionSnapshot _last = ConnectionSnapshot.Empty;
    private bool _tickBusy;
    private bool _isActive;

    // 既定は一時停止(ユーザー指示)。開いた瞬間の 1 枚だけ読んで止まり、
    // チェックを外している間だけ 2 秒ごとに追いかける
    private bool _isPaused = true;

    private string _filter = "";
    private string _status = "「一時停止」を外すと 2 秒ごとに更新します。";
    private string _rateNotice = NetTraceSession.IsAdministrator ? "" : AdminNotice;

    public ConnectionsViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<ConnectionListRow> Rows { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
                RebuildRows();   // 次のティックを待たず、手元のスナップショットから即絞り込む
        }
    }

    /// <summary>
    /// 更新の一時停止。既定でオン(開いた瞬間の 1 枚だけ表示)。
    /// 止まっている間は ETW セッションも回さない。
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!SetProperty(ref _isPaused, value))
                return;

            if (value)
            {
                _timer.Stop();
                StopTrace();
            }
            else if (_isActive)
            {
                StartTraceIfPossible();
                _timer.Start();
                _ = RefreshAsync();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>通信量が出せないときの案内。空なら畳む。</summary>
    public string RateNotice
    {
        get => _rateNotice;
        private set
        {
            if (SetProperty(ref _rateNotice, value))
                OnPropertyChanged(nameof(HasRateNotice));
        }
    }

    public bool HasRateNotice => _rateNotice.Length > 0;

    /// <summary>非管理者のときだけ「管理者として再起動」の導線を出す。</summary>
    public bool CanRelaunchAsAdmin => !NetTraceSession.IsAdministrator;

    /// <summary>タブが表示されたら動き、隠れたら止まる。ETW も同じ寿命にする。</summary>
    public void OnActivated()
    {
        _isActive = true;

        if (!IsPaused)
        {
            StartTraceIfPossible();
            _timer.Start();
        }

        // 一時停止中でも、開いた瞬間の 1 枚は出す(空のままだと壊れて見える)
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        _isActive = false;
        _timer.Stop();
        StopTrace();
    }

    public void Reset()
    {
        Filter = "";
        IsPaused = true;   // 既定へ戻す
    }

    private async Task RefreshAsync()
    {
        if (_tickBusy)
            return;   // 読み取りが間隔を超えても重ねない

        _tickBusy = true;
        try
        {
            _last = await Task.Run(_service.Read);

            if (_trace is { IsRunning: true })
            {
                // ETW の 1 窓ぶんを取り出して B/秒へ。窓幅は実測（一時停止をまたいでも平均になる）
                double elapsed = _rateWatch.Elapsed.TotalSeconds;
                _rateWatch.Restart();
                _rates = new ConnectionRates(_aggregator.Drain(), elapsed);
            }
            else
            {
                _rates = null;
            }

            RebuildRows();

            (int tcp, int udp, int processes) = ConnectionTableView.Count(_last.Rows);
            string suffix = IsPaused ? "・一時停止中" : "";
            Status = $"TCP {tcp} / UDP {udp} — {processes} プロセス（{DateTime.Now:HH:mm:ss} 時点{suffix}）";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "ConnectionsViewModel.RefreshAsync");
        }
        finally
        {
            _tickBusy = false;
        }
    }

    private void RebuildRows()
    {
        IReadOnlyList<ConnectionListRow> desired =
            ConnectionTableView.BuildRows(_last.Rows, _last.ProcessNames, Filter, _rates);
        OrderedListSync.Apply(Rows, desired, row => row.SortKey);
    }

    private void StopTrace()
    {
        // カーネルセッションはシステム全体のコストなので、見ていない間は止める
        _trace?.Dispose();
        _trace = null;
        _rates = null;
        _aggregator.Drain();
    }

    private void StartTraceIfPossible()
    {
        if (_trace is { IsRunning: true })
            return;

        if (!NetTraceSession.IsAdministrator)
        {
            RateNotice = AdminNotice;
            return;
        }

        _trace = new NetTraceSession(_aggregator);
        if (_trace.Start())
        {
            RateNotice = "";
            _rateWatch.Restart();
        }
        else
        {
            RateNotice = "⚠ " + (_trace.FailureMessage ?? "通信量の取得を開始できませんでした。");
            _trace.Dispose();
            _trace = null;
        }
    }
}
