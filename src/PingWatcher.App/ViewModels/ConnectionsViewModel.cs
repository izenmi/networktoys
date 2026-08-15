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
    private ConnectionSnapshot _last = ConnectionSnapshot.Empty;
    private bool _tickBusy;
    private bool _isActive;
    private bool _isPaused;
    private string _filter = "";
    private string _status = "タブを開くと 2 秒ごとに更新します。";
    private string _rateNotice = "";

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

    /// <summary>じっくり読みたい・コピーしたいとき用。更新だけ止める。</summary>
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
            }
            else if (_isActive)
            {
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

    /// <summary>タブが表示されたら回り、隠れたら止まる。ETW も同じ寿命にする。</summary>
    public void OnActivated()
    {
        _isActive = true;
        StartTraceIfPossible();

        if (IsPaused)
            return;

        _timer.Start();
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        _isActive = false;
        _timer.Stop();

        // カーネルセッションはシステム全体のコストなので、見ていない間は止める
        _trace?.Dispose();
        _trace = null;
        _rates = null;
        _aggregator.Drain();
    }

    public void Reset()
    {
        Filter = "";
        IsPaused = false;
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
            Status = $"TCP {tcp} / UDP {udp} — {processes} プロセス（{DateTime.Now:HH:mm:ss} 時点）";
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

    private void StartTraceIfPossible()
    {
        if (_trace is { IsRunning: true })
            return;

        if (!NetTraceSession.IsAdministrator)
        {
            RateNotice = "⚠ 通信量の表示には管理者権限が必要です。管理者として起動し直すと接続ごとの送受信 B/秒 が出ます。";
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
