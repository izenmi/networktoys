using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Threading;
using PastelNet.App.Mvvm;
using PastelNet.App.Services;
using PastelNet.Core.Metrics;
using PastelNet.Core.Models;
using PastelNet.Core.Quality;
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

    private readonly MonitorSettings _settings;
    private bool _isRunning;
    private string _targetListText = string.Empty;
    private string _listSummary = string.Empty;
    private TargetRowViewModel? _selectedRow;
    private string _statusMessage = string.Empty;
    private string _detailText = "行を選ぶと、その宛先の詳しい統計が出ます。";

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

        _targetListText = TargetListParser.Format(document.Targets);
        NetworkInfo = NetworkEnvironment.Current();

        StartCommand = new RelayCommand(Start, () => !IsRunning && Rows.Count > 0);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsRunning);
        ApplyListCommand = new RelayCommand(() => _ = ApplyListAsync());
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

    /// <summary>DNS 画面の比較対象の既定値に使う。</summary>
    public IReadOnlyList<IPAddress> SystemDnsServers => NetworkInfo.DnsServers;

    /// <summary>自分が今いるサブネット。スキャン範囲の既定値に使う。</summary>
    public string? SubnetCidr => NetworkInfo.SubnetCidr;

    /// <summary>
    /// スキャン結果などを宛先リストの末尾に書き足す。
    /// 反映は手動（「宛先」タブの反映ボタン）にしてある。勝手に測定対象が
    /// 増えると事故になるので、内容を確かめる機会を挟む。
    /// </summary>
    public void AppendToTargetList(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        string addition = string.Join('\n', lines);
        if (addition.Length == 0) return;

        string current = TargetListText.TrimEnd('\n', '\r');
        TargetListText = current.Length == 0
            ? addition + "\n"
            : $"{current}\n{addition}\n";
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ApplyListCommand { get; }
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

    /// <summary>宛先タブで編集するテキスト。書式は EXPing に合わせている。</summary>
    public string TargetListText
    {
        get => _targetListText;
        set => SetProperty(ref _targetListText, value);
    }

    /// <summary>反映結果の要約とエラー。</summary>
    public string ListSummary
    {
        get => _listSummary;
        private set => SetProperty(ref _listSummary, value);
    }

    public TargetRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
                UpdateDetail();
        }
    }

    /// <summary>
    /// 選択した行の詳しい統計。一覧に列を増やすと見通しが悪くなるので、
    /// ジッタや MOS のような「必要なときだけ見たい値」はここに出す。
    /// </summary>
    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CountText => $"{Rows.Count} 件";

    /// <summary>測定間隔。レポートに載せる。</summary>
    public int IntervalMs => _settings.IntervalMs;

    /// <summary>最初に測定を始めた時刻。レポートに載せる。</summary>
    public DateTime? StartedAt { get; private set; }

    private void Start()
    {
        if (IsRunning) return;

        _engine.Start([.. Rows.Select(r => r.Target)], _settings);
        _pump.Start();
        StartedAt ??= DateTime.Now;
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

        if (SelectedRow is { } selected && _touched.Contains(selected))
            UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (SelectedRow is not { } row)
        {
            DetailText = "行を選ぶと、その宛先の詳しい統計が出ます。";
            return;
        }

        RttStatistics stats = row.Statistics;

        if (stats.Attempts == 0)
        {
            DetailText = $"{row.Host} — まだ測定していません。";
            return;
        }

        VoiceQuality quality = MosCalculator.Estimate(stats.AverageMs, stats.JitterMs, stats.LossPercent);

        DetailText = string.Join("　　", (string[])
        [
            row.Host,
            $"最小 {TargetRowViewModel.FormatMilliseconds(stats.MinMs)}",
            $"平均 {TargetRowViewModel.FormatMilliseconds(stats.AverageMs)}",
            $"最大 {TargetRowViewModel.FormatMilliseconds(stats.MaxMs)}",
            $"p95 {TargetRowViewModel.FormatMilliseconds(stats.P95Ms)}",
            $"ジッタ {TargetRowViewModel.FormatMilliseconds(stats.JitterMs)}",
            $"ロス {TargetRowViewModel.FormatLoss(stats.LossPercent)}（{stats.Attempts - stats.Successes} / {stats.Attempts} 回）",
            $"通話品質の目安 MOS {quality.Mos:0.0}（{quality.Grade}）",
        ]);
    }

    /// <summary>
    /// テキストの内容で宛先リストを作り直す。
    /// 測定中なら止めてから入れ替える（対象が変わった以上、履歴も引き継がない）。
    /// </summary>
    private async Task ApplyListAsync()
    {
        await StopAsync();

        TargetListParseResult parsed = TargetListParser.Parse(TargetListText);

        Rows.Clear();
        _rowsById.Clear();
        SelectedRow = null;

        foreach (Target target in parsed.Targets)
            AddRow(target);

        Save();

        ListSummary = BuildSummary(parsed);
        StatusMessage = $"宛先を {parsed.Targets.Count} 件に更新しました。";
        OnPropertyChanged(nameof(CountText));
        StartCommand.RaiseCanExecuteChanged();
    }

    private static string BuildSummary(TargetListParseResult parsed)
    {
        var builder = new StringBuilder();
        builder.Append($"{parsed.Targets.Count} 件を登録しました。");

        if (parsed.ExpandedCount > 0)
            builder.Append($"（うち範囲指定から {parsed.ExpandedCount} 件）");

        if (parsed.CommentLines > 0)
            builder.Append($" 注釈 {parsed.CommentLines} 行を読み飛ばしました。");

        foreach (TargetListError error in parsed.Errors.Take(10))
            builder.Append($"\n{error.LineNumber} 行目: {error.Message}");

        if (parsed.Errors.Count > 10)
            builder.Append($"\nほか {parsed.Errors.Count - 10} 件の問題があります。");

        return builder.ToString();
    }

    private void ClearHistory()
    {
        foreach (TargetRowViewModel row in Rows)
            row.Reset();

        StartedAt = IsRunning ? DateTime.Now : null;
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
