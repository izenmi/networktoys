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
using PastelNet.Core.Work;

namespace PastelNet.App.ViewModels;

/// <summary>監視画面。宛先リストの管理と、測定結果の UI への反映を受け持つ。</summary>
public sealed class MonitorViewModel : ObservableObject
{
    // 測定は 1 秒間隔なので、UI の取り込みは 10Hz あれば十分。
    // 結果 1 件ごとに Dispatcher を叩くと数百宛先で描画が破綻する。
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 宛先テキストを打ち終わってから反映するまでの待ち。
    /// 1 文字ごとに解釈すると、範囲指定を打っている途中で
    /// 巨大な展開が走ってしまう。
    /// </summary>
    private static readonly TimeSpan ListDebounce = TimeSpan.FromMilliseconds(600);

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
    private DispatcherTimer? _listDebounce;
    private string _tcpPort = "443";
    private bool _isTcpMode;
    private bool _isCompact = true;

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

        StartCommand = new RelayCommand(() => Start(tcp: false), () => !IsRunning && Rows.Count > 0);
        StartTcpCommand = new RelayCommand(() => Start(tcp: true), () => !IsRunning && Rows.Count > 0);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsRunning);
        ClearHistoryCommand = new RelayCommand(ClearHistory);

        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = PumpInterval };
        _pump.Tick += OnPump;

        // 宛先テキストは打ち終わってから自動で反映する（反映ボタンは置かない）
        _listDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = ListDebounce };
        _listDebounce.Tick += OnListDebounceTick;
    }

    /// <summary>全宛先。保存や測定の起動にはこちらを使う。</summary>
    public ObservableCollection<TargetRowViewModel> Rows { get; } = [];

    /// <summary>応答が返っている宛先。</summary>
    public ObservableCollection<TargetRowViewModel> AliveRows { get; } = [];

    /// <summary>2 回続けて応答が無かった宛先。目立つ場所へ集めて気づけるようにする。</summary>
    public ObservableCollection<TargetRowViewModel> DownRows { get; } = [];

    public string AliveHeader => $"● 応答あり　{AliveRows.Count} 件";

    public string DownHeader => $"✕ 応答なし　{DownRows.Count} 件";

    /// <summary>応答なしが 1 件も無ければ、その欄は場所を取らない。</summary>
    public bool HasDownRows => DownRows.Count > 0;

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
    public RelayCommand StartTcpCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    /// <summary>TCP Ping で使うポート。宛先に :ポート が書かれていればそちらが優先される。</summary>
    public string TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    /// <summary>
    /// 1 行 1 件の詳しい表示ではなく、小さな枠を敷き詰めて一望する表示にするか。
    /// 100 件を超える監視では、こちらが既定。
    /// </summary>
    public bool IsCompact
    {
        get => _isCompact;
        set => SetProperty(ref _isCompact, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;

            OnPropertyChanged(nameof(RunButtonLabel));
            OnPropertyChanged(nameof(TcpButtonLabel));
            StartCommand.RaiseCanExecuteChanged();
            StartTcpCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public string RunButtonLabel => IsRunning && !_isTcpMode ? "Ping 実行中" : "Ping";

    public string TcpButtonLabel => IsRunning && _isTcpMode ? "TCP 実行中" : "TCP Ping";

    /// <summary>宛先タブで編集するテキスト。書式は EXPing に合わせている。</summary>
    public string TargetListText
    {
        get => _targetListText;
        set
        {
            if (!SetProperty(ref _targetListText, value)) return;

            // 打ち終わってから反映する
            _listDebounce?.Stop();
            _listDebounce?.Start();
        }
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

    /// <summary>
    /// 作業中に起きた不通の記録。作業タブとレポートで使う。
    /// 測定を開始し直しても消さない（作業をまたいで見返したいため）。
    /// </summary>
    internal OutageTracker Tracker { get; private set; } = new(1000);

    /// <summary>いま TCP で測っているか。ベースラインに実効の測り方を残すのに要る。</summary>
    internal bool IsTcpMode => _isTcpMode;

    /// <summary>TCP Ping のポート。同上。</summary>
    internal int EffectiveTcpPort => ParsePortOrDefault();

    /// <summary>最初に測定を始めた時刻。レポートに載せる。</summary>
    public DateTime? StartedAt { get; private set; }

    /// <param name="tcp">
    /// true なら全宛先を TCP 接続で測る。ICMP が塞がれている相手の確認に使う。
    /// 宛先リスト側で :ポート を指定してある宛先は、そのポートを優先する。
    /// </param>
    private void Start(bool tcp)
    {
        if (IsRunning) return;

        int port = 0;
        if (tcp && (!int.TryParse(TcpPort, out port) || port is < 1 or > 65535))
        {
            StatusMessage = "TCP Ping のポート番号が正しくありません（1〜65535）。";
            return;
        }

        // 宛先の登録内容そのものは書き換えない。TCP はあくまで測り方の切り替え
        List<Target> targets = [.. Rows.Select(r => tcp ? AsTcp(r.Target, port) : r.Target)];

        _isTcpMode = tcp;

        // 記録に残す測定間隔は開始時のもの。誤差の幅を示すのに使う
        Tracker = new OutageTracker(_settings.IntervalMs);

        _engine.Start(targets, _settings);
        _pump.Start();

        long now = DateTime.Now.Ticks;
        foreach (TargetRowViewModel row in Rows)
            row.StartWindow(now);

        StartedAt ??= DateTime.Now;
        IsRunning = true;

        StatusMessage = tcp
            ? $"{_engine.ActiveCount} 件を TCP:{port} へ {_settings.IntervalMs} ms 間隔で測定しています。"
            : $"{_engine.ActiveCount} 件を {_settings.IntervalMs} ms 間隔で測定しています。";
    }

    /// <summary>同じ Id のまま測り方だけ TCP に差し替えた複製を作る。</summary>
    private static Target AsTcp(Target source, int defaultPort) => new()
    {
        Id = source.Id,
        Host = source.Host,
        Comment = source.Comment,
        Group = source.Group,
        Enabled = source.Enabled,
        IntervalMs = source.IntervalMs,
        TimeoutMs = source.TimeoutMs,
        Kind = ProbeKind.Tcp,
        Port = source.Kind == ProbeKind.Tcp && source.Port > 0 ? source.Port : defaultPort,
    };

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _pump.Stop();
        await _engine.StopAsync();
        OnPump(this, EventArgs.Empty);   // 残っている結果を取りこぼさない

        // 続いている不通は「復旧した」ではなく「測定を止めた」として閉じる
        Tracker.CloseAll(DateTime.Now.Ticks, OutageCloseReason.Stopped);

        IsRunning = false;
        StatusMessage = "測定を停止しました。";
    }

    /// <summary>
    /// アプリを閉じるときの停止。<b>完了を待たない。</b>
    ///
    /// 待つと、名前解決中やタイムアウト待ちの宛先があるだけで終了が数秒固まる。
    /// 宛先リストと設定は編集のたびに保存済みなので、ここで待って守るものは無い。
    /// ソケットはプロセス終了時に OS が片付ける。
    /// </summary>
    public void BeginStop()
    {
        _pump.Stop();
        _listDebounce?.Stop();
        _engine.BeginStop();
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
            if (!_rowsById.TryGetValue(result.TargetId, out TargetRowViewModel? row))
                continue;

            row.Append(result.Sample, result.ResolvedAddress);

            // 不通の記録は行の表示とは別に、全サンプルを見て判定する
            Tracker.Observe(KeyOf(row.Target), row.Host, result.Sample);

            _touched.Add(row);
        }

        // 結果が届かなくなった宛先は _touched に入らないので、別途確かめる。
        // 「応答が無い」と「測っていない」を取り違えると、確認ツールとして嘘をつく
        if (IsRunning)
        {
            long now = DateTime.Now.Ticks;

            foreach (TargetRowViewModel row in Rows)
            {
                row.CheckStalled(now);
                _touched.Add(row);
            }
        }

        foreach (TargetRowViewModel row in _touched)
        {
            bool wasDown = row.IsDown;
            row.Refresh();

            if (row.IsDown != wasDown)
                Reclassify(row);
        }

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

    private void OnListDebounceTick(object? sender, EventArgs e)
    {
        _listDebounce?.Stop();
        ApplyListLive();
    }

    /// <summary>
    /// 宛先テキストの内容を一覧へ反映する。反映ボタンは置かず、打ち終わりを待って自動で行う。
    ///
    /// 測定は止めない。同じ宛先は行も履歴もそのまま残し、<b>増えた分だけ測り始め、
    /// 消えた分だけ止める</b>。宛先ごとに独立したループなので、これができる。
    ///
    /// ただし<b>書き間違いのある間は反映しない</b>。「192.168.1.0/2」のような
    /// 打ちかけの入力で宛先が総入れ替えになると事故になるため。
    /// </summary>
    private void ApplyListLive()
    {
        TargetListParseResult parsed = TargetListParser.Parse(TargetListText);
        ListSummary = BuildSummary(parsed);

        if (parsed.HasErrors)
        {
            StatusMessage = "宛先リストに解釈できない行があるため、反映を保留しています。";
            return;
        }

        var wanted = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (Target target in parsed.Targets)
            wanted.TryAdd(KeyOf(target), target);

        // 消えた宛先を落とす
        foreach (TargetRowViewModel row in Rows.Where(r => !wanted.ContainsKey(KeyOf(r.Target))).ToList())
            RemoveRow(row);

        // 増えた宛先を足す／残った宛先は備考だけ更新する
        var existing = new Dictionary<string, TargetRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (TargetRowViewModel row in Rows)
            existing.TryAdd(KeyOf(row.Target), row);

        foreach (Target target in parsed.Targets)
        {
            string key = KeyOf(target);

            if (existing.TryGetValue(key, out TargetRowViewModel? row))
            {
                row.UpdateComment(target.Comment);
                continue;
            }

            TargetRowViewModel added = AddRow(target);
            existing[key] = added;

            if (IsRunning)
                _engine.AddTarget(_isTcpMode ? AsTcp(target, ParsePortOrDefault()) : target);
        }

        Reorder(parsed.Targets);
        Save();

        StatusMessage = $"宛先を {Rows.Count} 件に更新しました。";
        OnPropertyChanged(nameof(CountText));
        StartCommand.RaiseCanExecuteChanged();
        StartTcpCommand.RaiseCanExecuteChanged();
    }

    private int ParsePortOrDefault() => int.TryParse(TcpPort, out int port) && port is >= 1 and <= 65535 ? port : 443;

    /// <summary>
    /// 同じ宛先とみなす条件。ホストと測り方が同じなら、備考が変わっても同一とする。
    /// <b>Target.Id は宛先テキストを読み直すたびに新しくなるので、鍵にはできない。</b>
    /// </summary>
    internal static string KeyOf(Target target) => $"{target.Host}|{target.Kind}|{target.Port}";

    /// <summary>
    /// いま<b>実際に</b>どう測っているかを返す。
    ///
    /// TCP Ping は宛先の登録内容を書き換えずに測り方だけを差し替えるため、
    /// <c>Target.Kind</c> を見ると TCP で測っている最中も「ICMP」と答えてしまう。
    /// 作業前後の比較でこれを取り違えると、ICMP で採った基準と TCP の実測を
    /// 突き合わせて「合格」と出しかねない。
    /// </summary>
    internal string DescribeEffectiveKind(Target target)
    {
        if (_isTcpMode)
        {
            int port = target.Kind == ProbeKind.Tcp && target.Port > 0 ? target.Port : ParsePortOrDefault();
            return $"TCP:{port}";
        }

        return target.Kind == ProbeKind.Tcp ? $"TCP:{target.Port}" : "ICMP";
    }

    private void RemoveRow(TargetRowViewModel row)
    {
        Rows.Remove(row);
        AliveRows.Remove(row);
        DownRows.Remove(row);
        _rowsById.Remove(row.Id);

        if (ReferenceEquals(SelectedRow, row))
            SelectedRow = null;

        if (IsRunning)
            _ = _engine.RemoveTargetAsync(row.Id);

        // 宛先が消えたことを「復旧」と記録しないよう、理由を残して閉じる
        Tracker.Remove(KeyOf(row.Target), DateTime.Now.Ticks);

        OnPropertyChanged(nameof(AliveHeader));
        OnPropertyChanged(nameof(DownHeader));
        OnPropertyChanged(nameof(HasDownRows));
    }

    /// <summary>テキストに書かれた順序へ並べ直す。</summary>
    private void Reorder(IReadOnlyList<Target> targets)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < targets.Count; i++)
            order.TryAdd(KeyOf(targets[i]), i);

        foreach (TargetRowViewModel row in Rows)
            row.Order = order.GetValueOrDefault(KeyOf(row.Target), int.MaxValue);

        SortInPlace(Rows);
        SortInPlace(AliveRows);
        SortInPlace(DownRows);
    }

    private static void SortInPlace(ObservableCollection<TargetRowViewModel> collection)
    {
        // 件数が多くても並べ替えは宛先の編集時にしか走らないので、素直な挿入ソートで足りる
        for (int i = 1; i < collection.Count; i++)
        {
            int j = i;
            while (j > 0 && collection[j - 1].Order > collection[j].Order)
            {
                collection.Move(j, j - 1);
                j--;
            }
        }
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

        // 全員が「応答なし」判定から外れるので、欄も戻す
        while (DownRows.Count > 0)
            Reclassify(DownRows[0]);

        StartedAt = IsRunning ? DateTime.Now : null;
        StatusMessage = "履歴を消去しました。";
    }

    private TargetRowViewModel AddRow(Target target)
    {
        var row = new TargetRowViewModel(target, _settings) { Order = Rows.Count };
        Rows.Add(row);
        AliveRows.Add(row);
        _rowsById[target.Id] = row;

        OnPropertyChanged(nameof(AliveHeader));
        OnPropertyChanged(nameof(HasDownRows));
        return row;
    }

    /// <summary>
    /// 応答の有無で欄を振り分ける。
    /// 元の並び順を保ったまま移すので、宛先リストに書いた順序が崩れない。
    /// </summary>
    private void Reclassify(TargetRowViewModel row)
    {
        ObservableCollection<TargetRowViewModel> from = row.IsDown ? AliveRows : DownRows;
        ObservableCollection<TargetRowViewModel> to = row.IsDown ? DownRows : AliveRows;

        if (!from.Remove(row))
            return;   // すでに正しい側にいる

        int index = 0;
        while (index < to.Count && to[index].Order < row.Order)
            index++;

        to.Insert(index, row);

        OnPropertyChanged(nameof(AliveHeader));
        OnPropertyChanged(nameof(DownHeader));
        OnPropertyChanged(nameof(HasDownRows));
    }

    private void ClearRows()
    {
        Rows.Clear();
        AliveRows.Clear();
        DownRows.Clear();
        _rowsById.Clear();

        OnPropertyChanged(nameof(AliveHeader));
        OnPropertyChanged(nameof(DownHeader));
        OnPropertyChanged(nameof(HasDownRows));
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
