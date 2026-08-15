using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Metrics;
using PingWatcher.Core.Models;
using PingWatcher.Core.Reporting;
using PingWatcher.Core.Work;

namespace PingWatcher.App.ViewModels;

/// <summary>作業中の出来事を時刻順に並べたときの 1 行。</summary>
public sealed record TimelineRow(string Time, string Label, string Text, bool IsOutage);

/// <summary>
/// 作業画面。変更作業の前後で「変わっていないこと」を示すための場所。
///
/// 測定そのものは Ping 画面が受け持つ。ここはその結果を作業の単位で切り取り、
/// 突き合わせて記録に残す。
/// </summary>
public sealed class WorkViewModel : ObservableObject
{
    private readonly MonitorViewModel _monitor;
    private readonly string _sessionDirectory;

    private WorkSession _session = new();
    private string _sessionName = string.Empty;
    private string _markerText = string.Empty;
    private string _note = string.Empty;
    private string _status = "測定を始めてしばらくしたら「作業前として記録」を押してください。";
    private WorkSummary? _summary;
    private bool _isBusy;

    public WorkViewModel(MonitorViewModel monitor)
    {
        _monitor = monitor;

        _sessionDirectory = AppData.PathOf("sessions");

        CaptureBeforeCommand = new RelayCommand(() => _ = CaptureAsync(before: true), () => !IsBusy);
        CaptureAfterCommand = new RelayCommand(() => _ = CaptureAsync(before: false), () => !IsBusy && HasBefore);
        AddMarkerCommand = new RelayCommand(AddMarker, () => !string.IsNullOrWhiteSpace(MarkerText));
        NewSessionCommand = new RelayCommand(StartNewSession);
        SaveHtmlCommand = new RelayCommand(() => _ = SaveAsync(html: true), () => !IsBusy);
        SaveCsvCommand = new RelayCommand(() => _ = SaveAsync(html: false), () => !IsBusy);
    }

    public ObservableCollection<WorkComparison> Comparisons { get; } = [];

    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public ObservableCollection<NeighborChange> NeighborChanges { get; } = [];

    public ObservableCollection<DiffLine> IpConfigDiff { get; } = [];

    public ObservableCollection<DiffLine> RouteDiff { get; } = [];

    public RelayCommand CaptureBeforeCommand { get; }
    public RelayCommand CaptureAfterCommand { get; }
    public RelayCommand AddMarkerCommand { get; }
    public RelayCommand NewSessionCommand { get; }
    public RelayCommand SaveHtmlCommand { get; }
    public RelayCommand SaveCsvCommand { get; }

    public string SessionName
    {
        get => _sessionName;
        set => SetProperty(ref _sessionName, value);
    }

    /// <summary>作業中に打つメモ。Enter だけで記録できるようにしてある。</summary>
    public string MarkerText
    {
        get => _markerText;
        set
        {
            if (SetProperty(ref _markerText, value))
                AddMarkerCommand.RaiseCanExecuteChanged();
        }
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            CaptureBeforeCommand.RaiseCanExecuteChanged();
            CaptureAfterCommand.RaiseCanExecuteChanged();
            SaveHtmlCommand.RaiseCanExecuteChanged();
            SaveCsvCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasBefore => _session.HasBefore;

    public bool HasComparison => _summary is not null;

    public string BeforeText => _session.Before is { } before
        ? $"作業前: {before.CapturedAt:MM/dd HH:mm:ss}（{before.Entries.Count} 件）"
        : "作業前: 未記録";

    public string AfterText => _session.After is { } after
        ? $"作業後: {after.CapturedAt:MM/dd HH:mm:ss}（{after.Entries.Count} 件）"
        : "作業後: 未記録";

    public string VerdictText => _summary?.Verdict ?? "—";

    public string HeadlineText => _summary?.Headline ?? "作業前と作業後を記録すると、ここに結果が出ます。";

    /// <summary>判定の色分けに使う。</summary>
    public string VerdictLevelName => _summary is null
        ? "None"
        : _summary.Failures > 0 ? "Failure"
        : _summary.Unknowns > 0 ? "Unknown"
        : _summary.Warnings > 0 ? "Warning"
        : "Ok";

    /// <summary>画面を開いたとき。ボタンの有効・無効を見直す。</summary>
    public void OnActivated()
    {
        CaptureBeforeCommand.RaiseCanExecuteChanged();
        CaptureAfterCommand.RaiseCanExecuteChanged();
        SaveHtmlCommand.RaiseCanExecuteChanged();
        SaveCsvCommand.RaiseCanExecuteChanged();

        // 測定中に打たれたマーカーや不通を取り込み直す
        BuildTimeline();
    }

    private async Task CaptureAsync(bool before)
    {
        if (_monitor.Rows.Count == 0)
        {
            Status = "宛先がありません。";
            return;
        }

        IsBusy = true;
        Status = before ? "作業前の状態を控えています…" : "作業後の状態を控えています…";

        try
        {
            var entries = new List<WorkEntry>(_monitor.Rows.Count);
            int thin = 0;

            foreach (TargetRowViewModel row in _monitor.Rows)
            {
                WorkEntry entry = BuildEntry(row);
                entries.Add(entry);

                if (entry.Attempts < 10)
                    thin++;
            }

            var snapshot = new WorkSnapshot(DateTimeOffset.Now, Note, _monitor.IntervalMs, entries);
            EnvironmentSnapshot environment = await CaptureEnvironmentAsync();

            if (before)
            {
                _session = new WorkSession
                {
                    Name = SessionName,
                    StartedAt = DateTimeOffset.Now,
                    Before = snapshot,
                    EnvBefore = environment,
                    Markers = [.. _session.Markers],
                };

                Comparisons.Clear();

                // 判定は派生プロパティ経由で表示されているので、消したことも通知する。
                // 通知しないと、撮り直したのに前回の「合格」バッジが画面に残る
                _summary = null;
                OnPropertyChanged(nameof(VerdictText));
                OnPropertyChanged(nameof(HeadlineText));
                OnPropertyChanged(nameof(VerdictLevelName));
                OnPropertyChanged(nameof(HasComparison));

                Status = thin > 0
                    ? $"作業前として記録しました。ただし {thin} 件は測定回数が少ないため、もう少し測ってから記録し直すことをお勧めします。"
                    : $"作業前として記録しました（{entries.Count} 件）。";
            }
            else
            {
                _session.After = snapshot;
                _session.EnvAfter = environment;
                _session.EndedAt = DateTimeOffset.Now;
                _session.Outages = [.. _monitor.Tracker.Records];

                BuildComparison();
                Status = $"作業後として記録し、突き合わせました。{_summary?.Headline}";
            }

            BuildTimeline();
            Save();

            OnPropertyChanged(nameof(HasBefore));
            OnPropertyChanged(nameof(BeforeText));
            OnPropertyChanged(nameof(AfterText));
            CaptureAfterCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Status = $"記録できませんでした: {ex.Message}";
            CrashLog.Write(ex, "WorkViewModel.CaptureAsync");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 行の状態を控える。
    /// <b>統計は履歴全体ではなく「測定を始めてから」の集計を使う。</b>
    /// 履歴全体から出すと、作業前の良好なサンプルが作業後の数字を薄めてしまう。
    /// </summary>
    private WorkEntry BuildEntry(TargetRowViewModel row)
    {
        WindowCounters window = row.Window;
        RttStatistics stats = row.Statistics;

        return new WorkEntry(
            MonitorViewModel.KeyOf(row.Target),
            row.Host,
            _monitor.DescribeEffectiveKind(row.Target),
            row.Address,
            row.Comment,
            Responded: window.Successes > 0,
            Attempts: window.Attempts,
            LossPercent: window.LossPercent,
            AverageMs: window.AverageMs,
            P95Ms: stats.P95Ms,
            MaxConsecutiveFailures: window.MaxConsecutiveFailures);
    }

    private async Task<EnvironmentSnapshot> CaptureEnvironmentAsync()
    {
        CommandCapture ipConfig = await SystemInfoProbe.GetIpConfigAsync(CancellationToken.None);
        CommandCapture routes = await SystemInfoProbe.GetRouteTableAsync(CancellationToken.None);

        // 近隣キャッシュは arp コマンドではなく iphlpapi から取る（表示言語に左右されない）
        Dictionary<string, string> neighbors = Interop.NativeMethods.GetArpTable();

        return new EnvironmentSnapshot(
            DateTimeOffset.Now,
            ipConfig.Ok ? ipConfig.Text : null,
            routes.Ok ? routes.Text : null,
            neighbors);
    }

    private void BuildComparison()
    {
        if (_session.Before is not { } before || _session.After is not { } after)
            return;

        // 作業前を控えてから作業後を控えるまでの間に不通があった宛先を拾う。
        // 前後とも正常でも「途中で切れた」なら報告しなければならない
        IReadOnlyCollection<string> outageKeys = _monitor.Tracker.KeysWithOutageBetween(
            before.CapturedAt.Ticks,
            after.CapturedAt.Ticks);

        IReadOnlyList<WorkComparison> comparisons = BaselineComparer.Compare(before, after, outageKeys);
        _summary = BaselineComparer.Summarize(comparisons, _session.Outages.Count);

        Comparisons.Clear();
        foreach (WorkComparison comparison in comparisons)
            Comparisons.Add(comparison);

        BuildEnvironmentDiff();

        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(VerdictLevelName));
    }

    private void BuildEnvironmentDiff()
    {
        IpConfigDiff.Clear();
        RouteDiff.Clear();
        NeighborChanges.Clear();

        if (_session.EnvBefore is not { } before || _session.EnvAfter is not { } after)
            return;

        foreach (DiffLine line in TextDiff.Compare(before.IpConfig, after.IpConfig, DiffNoiseFilter.IpConfig).Lines)
        {
            if (line.Kind != DiffKind.Unchanged)
                IpConfigDiff.Add(line);
        }

        foreach (DiffLine line in TextDiff.Compare(before.Routes, after.Routes, DiffNoiseFilter.RouteTable).Lines)
        {
            if (line.Kind != DiffKind.Unchanged)
                RouteDiff.Add(line);
        }

        string? gateway = _monitor.GatewayText == "—" ? null : _monitor.GatewayText;

        foreach (NeighborChange change in NeighborDiff.Compare(before.Neighbors, after.Neighbors, gateway))
            NeighborChanges.Add(change);
    }

    /// <summary>
    /// マーカーと不通を同じ並びに混ぜる。
    /// 「12:03:15 コア機器再起動」の直後に「12:03:16 file-srv 約4秒 不達」が並べば、
    /// どの操作で切れたのかが一目で分かる。
    /// </summary>
    private void BuildTimeline()
    {
        var rows = new List<(DateTimeOffset At, TimelineRow Row)>();

        foreach (WorkMarker marker in _session.Markers)
        {
            rows.Add((marker.At, new TimelineRow(
                marker.At.ToString("MM/dd HH:mm:ss"),
                "作業",
                marker.Text,
                IsOutage: false)));
        }

        foreach (OutageRecord outage in _monitor.Tracker.Records)
        {
            var at = new DateTimeOffset(outage.StartedAt);
            string status = outage.DominantStatus == ProbeStatus.DnsFailure ? "（名前解決の失敗）" : string.Empty;

            rows.Add((at, new TimelineRow(
                at.ToString("MM/dd HH:mm:ss"),
                "不通",
                $"{outage.Host} が {outage.DurationText} 応答なし{status}",
                IsOutage: true)));
        }

        Timeline.Clear();
        foreach ((_, TimelineRow row) in rows.OrderByDescending(r => r.At))
            Timeline.Add(row);
    }

    private void AddMarker()
    {
        string text = MarkerText.Trim();
        if (text.Length == 0) return;

        _session.Markers.Add(new WorkMarker(DateTimeOffset.Now, text));
        MarkerText = string.Empty;

        BuildTimeline();
        Save();

        Status = $"記録しました: {text}";
    }

    private void StartNewSession()
    {
        _session = new WorkSession { Name = SessionName };
        _summary = null;

        Comparisons.Clear();
        Timeline.Clear();
        IpConfigDiff.Clear();
        RouteDiff.Clear();
        NeighborChanges.Clear();

        Status = "新しい作業を始めました。";

        OnPropertyChanged(nameof(HasBefore));
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(BeforeText));
        OnPropertyChanged(nameof(AfterText));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(VerdictLevelName));
        CaptureAfterCommand.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        try
        {
            _session.Name = SessionName;

            string fileName = WorkSessionStore.BuildFileName(_session.StartedAt, _session.Name);
            WorkSessionStore.Save(Path.Combine(_sessionDirectory, fileName), _session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"作業記録を保存できませんでした: {ex.Message}";
        }
    }

    private async Task SaveAsync(bool html)
    {
        string extension = html ? "html" : "csv";

        var dialog = new SaveFileDialog
        {
            FileName = ReportService.SuggestFileName(extension),
            DefaultExt = extension,
            Filter = html
                ? "HTML レポート (*.html)|*.html|すべてのファイル (*.*)|*.*"
                : "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;

        try
        {
            Status = "レポートを作成しています…";

            string? ipConfig = null;
            if (html)
            {
                CommandCapture capture = await SystemInfoProbe.GetIpConfigAsync(CancellationToken.None);
                ipConfig = capture.Text;
            }

            ReportData data = ReportService.Build(
                string.IsNullOrWhiteSpace(SessionName) ? "ネットワーク変更作業の確認" : SessionName,
                Note,
                _monitor.Rows,
                _monitor.NetworkInfo,
                _monitor.StartedAt,
                _monitor.IntervalMs,
                ipConfig,
                html ? BuildWorkSection() : null,
                _monitor.DescribeEffectiveKind);

            if (html)
                ReportService.SaveHtml(dialog.FileName, data);
            else
                ReportService.SaveCsv(dialog.FileName, data);

            Status = $"{Path.GetFileName(dialog.FileName)} に書き出しました。";
        }
        catch (Exception ex)
        {
            Status = $"レポートを作成できませんでした: {ex.Message}";
            CrashLog.Write(ex, "WorkViewModel.SaveAsync");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private WorkSection BuildWorkSection()
    {
        var timeline = new List<WorkTimelineItem>();

        foreach (WorkMarker marker in _session.Markers)
            timeline.Add(new WorkTimelineItem(marker.At, "作業", marker.Text));

        foreach (OutageRecord outage in _monitor.Tracker.Records)
        {
            timeline.Add(new WorkTimelineItem(
                new DateTimeOffset(outage.StartedAt),
                "不通",
                $"{outage.Host} が {outage.DurationText} 応答なし"));
        }

        TextDiffResult? ipDiff = null;
        TextDiffResult? routeDiff = null;

        if (_session.EnvBefore is { } before && _session.EnvAfter is { } after)
        {
            ipDiff = TextDiff.Compare(before.IpConfig, after.IpConfig, DiffNoiseFilter.IpConfig);
            routeDiff = TextDiff.Compare(before.Routes, after.Routes, DiffNoiseFilter.RouteTable);
        }

        return new WorkSection(
            SessionName,
            _summary,
            [.. Comparisons],
            [.. timeline.OrderBy(t => t.At)],
            ipDiff,
            routeDiff,
            [.. NeighborChanges]);
    }
    /// <summary>作業の記録と入力を起動時の状態へ戻す。保存済みのファイルには触れない。</summary>
    public void Reset()
    {
        SessionName = string.Empty;
        MarkerText = string.Empty;
        Note = string.Empty;

        StartNewSession();
        Status = "測定を始めてしばらくしたら「作業前として記録」を押してください。";
    }

}
