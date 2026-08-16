using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using PingWatcher.App.Interop;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Net;
using PingWatcher.Core.Work;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// Windows Filtering Platform が落とした通信の一覧。
///
/// 読み取りにも管理者権限が要る（このアプリで 2 つ目の管理者限定機能）。
/// さらに Windows は既定でイベントを記録していないので、記録が無効なら
/// 「記録を有効にする」でシステム設定を立てる — <b>立てたら終了時に必ず戻す</b>
/// （システム全体に効く設定で、他のアプリも同じものを見ている）。
/// </summary>
public sealed class WfpViewModel : ObservableObject
{
    // WFP の列挙は接続一覧より重い(毎回エンジンを開いて閉じる)ので間隔を長めに取る
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private const int MaxEvents = 5000;

    private const string AdminNotice =
        "⚠ 遮断されたイベントの取得には管理者権限が必要です。管理者として起動し直すと一覧が出ます。";

    private const string CollectNotice =
        "⚠ Windows がネットワークイベントを記録していないため、遮断の履歴がありません。"
        + "「記録を有効にする」を押すと記録を始めます（システム全体に影響します。アプリを閉じると元に戻します）。";

    private readonly DispatcherTimer _timer;

    private IReadOnlyList<WfpBlockedEvent> _events = [];
    private bool _tickBusy;
    private bool _isActive;

    // 既定は一時停止。開いた瞬間の 1 枚だけ読んで止まる(接続タブと同じ)
    private bool _isPaused = true;

    // 自分で記録を立てたときだけ true。終了時に戻すのはこの場合だけで、
    // もともと有効だった環境の設定には触らない
    private bool _collectEnabledByUs;

    private bool _isCollecting;
    private string _filter = "";
    private string _status = "「一時停止」を外すと 5 秒ごとに更新します。";
    private string _notice = NetTraceSession.IsAdministrator ? "" : AdminNotice;
    private string _sortColumn = "";
    private bool _sortDescending;

    public WfpViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        SaveCommand = new RelayCommand(Save, () => Rows.Count > 0);
    }

    /// <summary>記録を有効にしてよいか確かめてもらう（システム設定を変えるため）。</summary>
    public event Func<bool>? ConfirmEnableCollection;

    public ObservableCollection<WfpBlockedRow> Rows { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!SetProperty(ref _isPaused, value)) return;

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

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
                RebuildRows();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>恒常的な案内（管理者でない・記録が無効）。Status と違って上書きで消えない。</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public bool CanRelaunchAsAdmin => !NetTraceSession.IsAdministrator;

    /// <summary>Windows が記録している状態か。ボタンの出し分けに使う。</summary>
    public bool IsCollecting
    {
        get => _isCollecting;
        private set
        {
            if (SetProperty(ref _isCollecting, value))
                OnPropertyChanged(nameof(CanEnableCollection));
        }
    }

    public bool CanEnableCollection => NetTraceSession.IsAdministrator && !_isCollecting;

    public string TimeHeader => HeaderLabel("", "時刻");
    public string ProtocolHeader => HeaderLabel("Protocol", "プロトコル");
    public string RemoteHeader => HeaderLabel("Remote", "宛先");
    public string ProcessHeader => HeaderLabel("Process", "プロセス");
    public string CountHeader => HeaderLabel("Count", "件数");

    private string HeaderLabel(string column, string title)
        => _sortColumn == column ? $"{title} {(_sortDescending ? "▼" : "▲")}" : title;

    public void SortBy(string column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            // 件数は多い順から見たい
            _sortDescending = column is "Count";
        }

        foreach (string name in new[]
                 { nameof(TimeHeader), nameof(ProtocolHeader), nameof(RemoteHeader),
                   nameof(ProcessHeader), nameof(CountHeader) })
            OnPropertyChanged(name);

        RebuildRows();
    }

    public void OnActivated()
    {
        _isActive = true;

        if (!IsPaused)
            _timer.Start();

        // 一時停止中でも開いた瞬間の 1 枚は出す(空のままだと壊れて見える)
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
        IsPaused = true;
        _events = [];
        Rows.Clear();
        SaveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 記録を有効にする。押されたときだけ呼ばれる。
    /// 立てたことを覚えておき、<see cref="RestoreCollectionSetting"/> で戻す。
    /// </summary>
    public void EnableCollection()
    {
        if (!CanEnableCollection) return;

        if (ConfirmEnableCollection is { } confirm && !confirm())
            return;

        string? error = WfpNativeMethods.SetCollectNetEvents(true);
        if (error is not null)
        {
            Status = error;
            return;
        }

        _collectEnabledByUs = true;
        IsCollecting = true;
        Notice = "";
        Status = "記録を始めました。遮断が起きると数秒で一覧に出ます。";

        _ = RefreshAsync();
    }

    /// <summary>
    /// 自分で立てた記録を元に戻す。アプリ終了時に呼ぶ。
    /// もともと有効だった環境では何もしない（他のアプリが見ている設定なので）。
    /// </summary>
    public void RestoreCollectionSetting()
    {
        if (!_collectEnabledByUs) return;

        _collectEnabledByUs = false;
        WfpNativeMethods.SetCollectNetEvents(false);
    }

    private async Task RefreshAsync()
    {
        if (_tickBusy) return;

        _tickBusy = true;
        try
        {
            if (!NetTraceSession.IsAdministrator)
            {
                Notice = AdminNotice;
                Status = "";
                return;
            }

            // 同期 RPC なので UI スレッドで回さない
            WfpReadResult result = await Task.Run(() => WfpNativeMethods.Read(MaxEvents));

            IsCollecting = result.CollectOption != 0;

            if (result.Error is not null)
            {
                Status = result.Error;
                return;
            }

            _events = result.Events;
            RebuildRows();

            string suffix = IsPaused ? "・一時停止中" : "";
            Status = $"遮断 {result.Events.Count:N0} 件 / 全イベント {result.TotalSeen:N0} 件"
                   + $"（{DateTime.Now:HH:mm:ss} 時点{suffix}）";

            // 記録が無効なら 0 件が正常。その旨を案内に出さないと「壊れている」に見える
            Notice = IsCollecting ? "" : CollectNotice;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WfpViewModel.RefreshAsync");
            Status = $"取得に失敗しました: {ex.Message}";
        }
        finally
        {
            _tickBusy = false;
        }
    }

    private void RebuildRows()
    {
        IReadOnlyList<WfpBlockedRow> desired =
            WfpEventView.Group(_events, Filter, _sortColumn, _sortDescending);

        // 全消し＋全追加にすると選択とスクロール位置が毎回飛ぶ
        OrderedListSync.Apply(Rows, desired, row => row.SortKey);
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"wfp-blocked-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = "csv",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            CsvTable table = WfpEventView.ToCsv([.. Rows]);

            // BOM が無いと日本語版 Excel が文字化けする
            File.WriteAllText(dialog.FileName, table.ToCsv(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました（{Rows.Count} 件）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }
}
