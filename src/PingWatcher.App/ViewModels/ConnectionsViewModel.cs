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

    private ConnectionSnapshot _last = ConnectionSnapshot.Empty;
    private bool _tickBusy;
    private bool _isActive;
    private bool _isPaused;
    private string _filter = "";
    private string _status = "タブを開くと 2 秒ごとに更新します。";

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

    /// <summary>タブが表示されたら回り、隠れたら止まる。</summary>
    public void OnActivated()
    {
        _isActive = true;
        if (IsPaused)
            return;

        _timer.Start();
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        _isActive = false;
        _timer.Stop();
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
            ConnectionTableView.BuildRows(_last.Rows, _last.ProcessNames, Filter, rates: null);
        OrderedListSync.Apply(Rows, desired, row => row.SortKey);
    }
}
