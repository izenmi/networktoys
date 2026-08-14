using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows.Threading;
using PastelNet.App.Mvvm;
using PastelNet.App.Services;
using PastelNet.Core.Models;
using PastelNet.Core.Storage;

namespace PastelNet.App.ViewModels;

/// <summary>監視画面。宛先リストの管理と、測定結果の UI への反映を受け持つ。</summary>
public sealed class MonitorViewModel : ObservableObject
{
    // 測定は 1 秒間隔なので、UI の取り込みは 10Hz あれば十分。
    // 結果 1 件ごとに Dispatcher を叩くと数百宛先で描画が破綻する。
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

    private readonly MonitorEngine _engine = new();
    private readonly Dictionary<string, TargetRowViewModel> _rowsById = [];
    private readonly HashSet<TargetRowViewModel> _touched = [];
    private readonly DispatcherTimer _pump;
    private readonly string _storePath;

    private MonitorSettings _settings;
    private bool _isRunning;
    private string _newHost = string.Empty;
    private string _newComment = string.Empty;
    private TargetRowViewModel? _selectedRow;
    private string _statusMessage = string.Empty;

    public MonitorViewModel()
    {
        _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PastelNet",
            "targets.json");

        TargetDocument document = TargetStore.Load(_storePath, out string? error);
        _settings = document.Settings;

        if (error is not null)
            StatusMessage = error;

        if (document.Targets.Count == 0)
            document.Targets.AddRange(CreateStarterTargets());

        foreach (Target target in document.Targets)
            AddRow(target);

        NetworkInfo = NetworkEnvironment.Current();

        StartCommand = new RelayCommand(Start, () => !IsRunning && Rows.Count > 0);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsRunning);
        AddCommand = new RelayCommand(AddTarget, () => !string.IsNullOrWhiteSpace(NewHost));
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedRow is not null);
        ClearHistoryCommand = new RelayCommand(ClearHistory);

        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = PumpInterval };
        _pump.Tick += OnPump;
    }

    public ObservableCollection<TargetRowViewModel> Rows { get; } = [];

    /// <summary>
    /// 接続環境。System.Environment と紛らわしくならない名前にしている。
    /// XAML からは下の文字列プロパティ経由で参照するので internal でよい。
    /// </summary>
    internal NetworkSnapshot NetworkInfo { get; }

    public string InterfaceText => NetworkInfo.InterfaceName ?? "—";

    public string LocalAddressText => NetworkInfo.LocalAddress is { } address
        ? (NetworkInfo.PrefixLength > 0 ? $"{address}/{NetworkInfo.PrefixLength}" : address.ToString())
        : "—";

    public string GatewayText => NetworkInfo.Gateway?.ToString() ?? "—";

    public string DnsText => NetworkInfo.DnsServers.Count > 0
        ? string.Join(", ", NetworkInfo.DnsServers.Select(a => a.ToString()))
        : "—";

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;

            OnPropertyChanged(nameof(RunButtonLabel));
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public string RunButtonLabel => IsRunning ? "測定中" : "測定を開始";

    public string NewHost
    {
        get => _newHost;
        set
        {
            if (SetProperty(ref _newHost, value))
                AddCommand.RaiseCanExecuteChanged();
        }
    }

    public string NewComment
    {
        get => _newComment;
        set => SetProperty(ref _newComment, value);
    }

    public TargetRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
                RemoveCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int IntervalMs => _settings.IntervalMs;

    private void Start()
    {
        if (IsRunning) return;

        _engine.Start([.. Rows.Select(r => r.Target)], _settings);
        _pump.Start();
        IsRunning = true;
        StatusMessage = $"{_engine.ActiveCount} 件を {_settings.IntervalMs} ms 間隔で測定しています。";
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _pump.Stop();
        await _engine.StopAsync();
        OnPump(this, EventArgs.Empty);   // 残っている結果を取りこぼさない
        IsRunning = false;
        StatusMessage = "測定を停止しました。";
    }

    /// <summary>
    /// 溜まった結果をまとめて取り込む。行の追加・削除は起きないので
    /// コレクション変更通知は発生せず、既存行のプロパティ更新だけが流れる。
    /// </summary>
    private void OnPump(object? sender, EventArgs e)
    {
        _touched.Clear();

        while (_engine.Results.TryRead(out ProbeResult result))
        {
            if (_rowsById.TryGetValue(result.TargetId, out TargetRowViewModel? row))
            {
                row.Append(result.Sample, result.ResolvedAddress);
                _touched.Add(row);
            }
        }

        foreach (TargetRowViewModel row in _touched)
            row.Refresh();
    }

    private void AddTarget()
    {
        string host = NewHost.Trim();
        if (host.Length == 0) return;

        var target = new Target
        {
            Host = host,
            Comment = NewComment.Trim(),
        };

        if (!target.IsValid())
        {
            StatusMessage = "宛先を追加できませんでした。ホスト名か IP アドレスを入力してください。";
            return;
        }

        AddRow(target);
        NewHost = string.Empty;
        NewComment = string.Empty;
        Save();

        StatusMessage = IsRunning
            ? $"{host} を追加しました。測定を開始し直すと反映されます。"
            : $"{host} を追加しました。";

        StartCommand.RaiseCanExecuteChanged();
    }

    private void RemoveSelected()
    {
        if (SelectedRow is not { } row) return;

        Rows.Remove(row);
        _rowsById.Remove(row.Id);
        SelectedRow = null;
        Save();

        StatusMessage = IsRunning
            ? $"{row.Host} を削除しました。測定を開始し直すと反映されます。"
            : $"{row.Host} を削除しました。";

        StartCommand.RaiseCanExecuteChanged();
    }

    private void ClearHistory()
    {
        foreach (TargetRowViewModel row in Rows)
            row.Reset();

        StatusMessage = "履歴を消去しました。";
    }

    private void AddRow(Target target)
    {
        var row = new TargetRowViewModel(target, _settings);
        Rows.Add(row);
        _rowsById[target.Id] = row;
    }

    private void Save()
    {
        try
        {
            TargetStore.Save(_storePath, new TargetDocument
            {
                Targets = [.. Rows.Select(r => r.Target)],
                Settings = _settings,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"宛先リストを保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>初回起動時の宛先。何も無い画面より、すぐ測れる方が親切。</summary>
    private static IEnumerable<Target> CreateStarterTargets()
    {
        NetworkSnapshot snapshot = NetworkEnvironment.Current();

        if (snapshot.Gateway is not null)
            yield return new Target { Host = snapshot.Gateway.ToString(), Comment = "既定ゲートウェイ" };

        foreach (IPAddress dns in snapshot.DnsServers.Take(1))
            yield return new Target { Host = dns.ToString(), Comment = "DNS サーバ" };

        yield return new Target { Host = "8.8.8.8", Comment = "外部疎通の基準" };
    }
}
