using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Logging;
using NetworkToys.Core.Metrics;
using NetworkToys.Core.Models;
using NetworkToys.Core.Storage;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

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
    private readonly SessionLogService _log = new();
    private readonly Dictionary<string, TargetRowViewModel> _rowsById = [];
    private readonly HashSet<TargetRowViewModel> _touched = [];
    private readonly DispatcherTimer _pump;

    private readonly MonitorSettings _settings;
    private bool _isRunning;

    /// <summary>進行中の停止処理。二重に呼ばれたとき同じ完了を待たせる。</summary>
    private Task? _stopTask;
    private string _targetListText = string.Empty;

    private string? _selectedListName;
    private string _listSummary = string.Empty;
    private TargetRowViewModel? _selectedRow;
    private TargetRowViewModel? _selectedAliveRow;
    private TargetRowViewModel? _selectedDownRow;

    /// <summary>選択の同期中か。一覧側のセッターが同期の書き戻しに反応しないようにする。</summary>
    private bool _syncingSelection;
    /// <summary>何もしていないときの案内。<b>初期値と Reset の戻し先を空文字にしない</b>
    /// （何をすれば動くかが見えない画面になる。2026-08-20 の UI 改善）。</summary>
    private const string IdleHint = "宛先を書いて「開始」を押すと測り始めます。";

    private string _statusMessage = IdleHint;
    private string _detailText = "行を選ぶと、その宛先の詳しい統計が出ます。";
    private DispatcherTimer? _listDebounce;
    private string _tcpPort = "443";
    private bool _isTcpMode;

    /// <summary>
    /// この画面が TCP 専用か。
    ///
    /// ICMP と TCP を 1 つの画面で切り替える作りだと、宛先リストを共有してしまい
    /// 「ICMP で見る相手」と「ポートまで見る相手」を分けて持てない。
    /// 画面ごとに宛先を持たせるため、測り方は生成時に決め打ちにしている。
    /// </summary>
    private readonly bool _alwaysTcp;

    // 見出しクリックの並べ替え。空文字は「元の並び(宛先リストに書いた順)」
    private string _sortColumn = "";
    private bool _sortDescending;

    /// <param name="alwaysTcp">TCP 接続で測る画面にするか。宛先は settings.json 内で画面ごとに分かれている。</param>
    public MonitorViewModel(bool alwaysTcp = false)
    {
        _alwaysTcp = alwaysTcp;

        TargetDocument document = alwaysTcp ? Settings.Current.Tcp : Settings.Current.Ping;
        _settings = document.Settings;

        if (Settings.LoadError is not null)
            StatusMessage = Settings.LoadError;

        // インターフェースの列挙は安くないので 1 回で済ませ、初期宛先にも使い回す
        NetworkInfo = NetworkEnvironment.Current();

        if (document.Targets.Count == 0)
            document.Targets.AddRange(CreateStarterTargets(alwaysTcp, NetworkInfo));

        foreach (Target target in document.Targets)
            AddRow(target);

        _targetListText = TargetListParser.Format(document.Targets);

        StartCommand = new RelayCommand(() => Start(_alwaysTcp), () => !IsRunning && Rows.Count > 0);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsRunning);
        ClearHistoryCommand = new RelayCommand(ClearHistoryFromButton);

        // コマンドを先に作る。SelectedListName の setter が DeleteListCommand を触るので、
        // 逆順にすると生成の途中で落ちる（試験タブのひな型で実際に踏んだ）
        SaveListCommand = new RelayCommand(SaveList);
        DeleteListCommand = new RelayCommand(DeleteList, () => SelectedListName is { Length: > 0 });

        foreach (string name in SavedListStore.Keys.OrderBy(n => n, StringComparer.CurrentCulture))
            SavedLists.Add(name);

        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = PumpInterval };
        _pump.Tick += OnPump;

        // 宛先テキストは打ち終わってから自動で反映する（反映ボタンは置かない）
        _listDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = ListDebounce };
        _listDebounce.Tick += OnListDebounceTick;
    }

    /// <summary>全宛先。保存や測定の起動にはこちらを使う。</summary>
    public ObservableCollection<TargetRowViewModel> Rows { get; } = [];

    /// <summary>応答が返っている宛先。</summary>
    private string _filter = "";

    public ObservableCollection<TargetRowViewModel> AliveRows { get; } = [];

    /// <summary>2 回続けて応答が無かった宛先。目立つ場所へ集めて気づけるようにする。</summary>
    public ObservableCollection<TargetRowViewModel> DownRows { get; } = [];

    /// <summary>
    /// 一覧の絞り込み。宛先・備考・解決したアドレスの部分一致。
    ///
    /// 行を collection から抜かずに <see cref="ICollectionView.Filter"/> で隠す。
    /// 応答あり／なしの振り分けは元の collection を直接触っているので、
    /// フィルタで中身を出し入れすると振り分けが壊れる。
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;

            ApplyFilter(AliveRows);
            ApplyFilter(DownRows);

            OnPropertyChanged(nameof(AliveHeader));
            OnPropertyChanged(nameof(DownHeader));
            OnPropertyChanged(nameof(HasDownRows));
        }
    }

    private void ApplyFilter(ObservableCollection<TargetRowViewModel> rows)
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(rows);

        // 絞り込みが空のときは述語ごと外す（毎行の判定を走らせない）
        view.Filter = _filter.Length == 0 ? null : o => Matches((TargetRowViewModel)o);
    }

    private bool Matches(TargetRowViewModel row)
        => row.Host.Contains(_filter, StringComparison.OrdinalIgnoreCase)
           || row.Comment.Contains(_filter, StringComparison.OrdinalIgnoreCase)
           || row.Address.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private int VisibleCount(ObservableCollection<TargetRowViewModel> rows)
        => _filter.Length == 0 ? rows.Count : rows.Count(Matches);

    public string AliveHeader => $"● 応答あり　{VisibleCount(AliveRows)} 件";

    public string DownHeader => $"✕ 応答なし　{VisibleCount(DownRows)} 件";

    /// <summary>応答なしが 1 件も無ければ、その欄は場所を取らない。</summary>
    public bool HasDownRows => VisibleCount(DownRows) > 0;

    /// <summary>一覧の見出し。ソート中の列に ▲/▼ を添える。</summary>
    public string HeaderState => HeaderLabel("State", "状態");
    public string HeaderTarget => HeaderLabel("Target", "宛先");
    public string HeaderNote => HeaderLabel("Comment", "備考");
    public string HeaderRtt => HeaderLabel("Rtt", "RTT");
    public string HeaderLoss => HeaderLabel("Loss", "ロス");

    /// <summary>
    /// 見出しクリックの並べ替え。同じ列を続けてクリックすると
    /// 昇順 → 降順 → 元の並び(宛先リストに書いた順)と巡る。
    /// 並べ替えはクリックの瞬間に 1 回だけ行い、測定のたびに行を
    /// 動かさない(数値が揺れるたびに並び直すと一覧が読めなくなる)。
    /// </summary>
    public void SortBy(string column)
    {
        if (_sortColumn == column)
        {
            if (!_sortDescending)
            {
                _sortDescending = true;
            }
            else
            {
                _sortColumn = "";
                _sortDescending = false;
            }
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        SortInPlace(Rows);
        SortInPlace(AliveRows);
        SortInPlace(DownRows);

        OnPropertyChanged(nameof(HeaderState));
        OnPropertyChanged(nameof(HeaderTarget));
        OnPropertyChanged(nameof(HeaderNote));
        OnPropertyChanged(nameof(HeaderRtt));
        OnPropertyChanged(nameof(HeaderLoss));
    }

    private string HeaderLabel(string column, string title)
        => _sortColumn == column ? $"{title} {(_sortDescending ? "▼" : "▲")}" : title;

    /// <summary>
    /// 表示順の全順序。ソート列が同値(または未指定)のときは
    /// 宛先リストに書いた順(<see cref="TargetRowViewModel.Order"/>)で確定する。
    /// </summary>
    private int CompareRows(TargetRowViewModel x, TargetRowViewModel y)
    {
        int result = _sortColumn switch
        {
            "State" => ((int)x.State).CompareTo((int)y.State),
            "Target" => string.Compare(x.HostDisplay, y.HostDisplay, StringComparison.OrdinalIgnoreCase),
            "Comment" => string.Compare(x.Comment, y.Comment, StringComparison.OrdinalIgnoreCase),
            "Rtt" => x.SortRtt.CompareTo(y.SortRtt),
            "Loss" => x.SortLoss.CompareTo(y.SortLoss),
            _ => 0,
        };

        if (_sortDescending)
            result = -result;

        return result != 0 ? result : x.Order.CompareTo(y.Order);
    }

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

    /// <summary>自分の IPv4。FTP の機器側コマンド例などに使う。</summary>
    public string? LocalAddress => NetworkInfo.LocalAddress?.ToString();

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
    public RelayCommand ClearHistoryCommand { get; }

    /// <summary>TCP Ping で使うポート。宛先に :ポート が書かれていればそちらが優先される。</summary>
    public string TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

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

    /// <summary>
    /// 開始ボタンの文字。ヘッダのボタンはどのタブを見ていても押せるので、
    /// 何が始まるのかを文字で言う（ユーザー指示）。
    /// </summary>
    public string RunButtonLabel => IsRunning ? "実行中" : _alwaysTcp ? "TCP 開始" : "Ping 開始";

    /// <summary>TCP 専用の画面か。ポート欄などをこれで出し分ける。</summary>
    public bool IsTcpScreen => _alwaysTcp;

    /// <summary>
    /// 名前を付けて残した宛先リスト。<b>現場ごとに測る相手は決まっている</b>ので、
    /// いくつか持って切り替えられるようにする（2026-08-17 ユーザー指示）。
    /// Ping と TCP で別の入れ物を使う（測る相手が違う）。
    /// </summary>
    public ObservableCollection<string> SavedLists { get; } = [];

    /// <summary>
    /// 選んでいるリストの名前。<b>選び直すと画面の宛先がそれに入れ替わる</b>。
    /// 入れ替える前に、いま出ているものは元の名前へ書き戻す（編集を黙って捨てない）。
    /// </summary>
    public string? SelectedListName
    {
        get => _selectedListName;
        set
        {
            if (string.Equals(_selectedListName, value, StringComparison.Ordinal)) return;

            // 切り替える前に、いまの中身を元の名前へ残す
            if (_selectedListName is { Length: > 0 } previous && SavedListStore.ContainsKey(previous))
                SavedListStore[previous] = TargetListText;

            _selectedListName = value;
            OnPropertyChanged();

            if (value is { Length: > 0 } name && SavedListStore.TryGetValue(name, out string? text))
                TargetListText = text;

            DeleteListCommand.RaiseCanExecuteChanged();
            SaveSettings();
        }
    }

    /// <summary>いまの画面のリスト置き場。Ping と TCP で分かれている。</summary>
    private Dictionary<string, string> SavedListStore
        => _alwaysTcp ? Settings.Current.TcpTargetLists : Settings.Current.PingTargetLists;

    /// <summary>いまの宛先に名前を付けて残す。</summary>
    public RelayCommand SaveListCommand { get; }

    /// <summary>選んでいるリストを消す。宛先そのものは画面に残す。</summary>
    public RelayCommand DeleteListCommand { get; }

    /// <summary>
    /// 名前を聞くのは画面の仕事（VM から窓を開かない。試験タブのひな型と同じ）。
    /// </summary>
    public Func<string, string?>? AskListName { get; set; }

    /// <summary>
    /// リストを消してよいかを聞く。画面が結線する。
    /// <b>結線前の既定は「いいえ」</b>（ファイル転送と同じ決まり）。
    /// </summary>
    public Func<string, bool>? ConfirmDelete { get; set; }

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

    /// <summary>
    /// 選ばれている行。応答なし/応答ありの 2 つの一覧をまたいで<b>常に 1 行だけ</b>。
    /// ListBox が別々なので、そのまま同じプロパティに双方向で繋ぐと
    /// 「もう片方の選択が残ったまま」になる。一覧ごとのプロパティを経由して同期する。
    /// </summary>
    public TargetRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            bool changed = SetProperty(ref _selectedRow, value);

            // 値が同じでも、行が欄をまたいで移った直後は一覧側の選択が外れている
            SyncListSelection();

            if (changed)
                UpdateDetail();
        }
    }

    public TargetRowViewModel? SelectedAliveRow
    {
        get => _selectedAliveRow;
        set
        {
            if (!SetProperty(ref _selectedAliveRow, value) || _syncingSelection) return;

            // null は「この一覧の選択が外れた」。別の欄で選ばれたときにも来るので、
            // いま選ばれている行がこの欄のものだったときだけ選択を解く
            if (value is not null)
                SelectedRow = value;
            else if (SelectedRow is { IsDown: false })
                SelectedRow = null;
        }
    }

    public TargetRowViewModel? SelectedDownRow
    {
        get => _selectedDownRow;
        set
        {
            if (!SetProperty(ref _selectedDownRow, value) || _syncingSelection) return;

            if (value is not null)
                SelectedRow = value;
            else if (SelectedRow is { IsDown: true })
                SelectedRow = null;
        }
    }

    /// <summary>一覧側の選択を <see cref="SelectedRow"/> に合わせる。</summary>
    private void SyncListSelection()
    {
        _syncingSelection = true;
        try
        {
            SelectedAliveRow = _selectedRow is { IsDown: false } ? _selectedRow : null;
            SelectedDownRow = _selectedRow is { IsDown: true } ? _selectedRow : null;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// 選択した行の詳しい統計。一覧に列を増やすと見通しが悪くなるので、
    /// ジッタのような「必要なときだけ見たい値」はここに出す。
    /// </summary>
    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        internal set => SetProperty(ref _statusMessage, value);
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
        {
            row.StartWindow(now);

            // 前回の測定のサンプル時刻を持ち越さない。持ち越すと、再開直後に
            // 最初の結果が届くまで全行が「… 停止」表示になる
            row.NotifyResumed(now);
        }

        StartedAt ??= DateTime.Now;
        IsRunning = true;

        // 測定ログを開始。証跡なので、画面の履歴(リングバッファ)と違って
        // アプリを閉じても残る。書けない場所でも測定自体は止めない
        _log.Start(
            _alwaysTcp ? "tcp" : "ping",
            SessionLogFormatter.Header(DateTime.Now, _settings.IntervalMs, targets.Count, tcp ? $"TCP(既定 {port})" : "ICMP"));

        // 実行中であることはボタンの文字で分かり、件数は一覧の見出しに出ている。
        // ここに定型文を出しても場所を取るだけなので、前の用件だけ消しておく。
        StatusMessage = string.Empty;
    }

    /// <summary>同じ Id のまま測り方だけ TCP に差し替えた複製を作る。</summary>
    private static Target AsTcp(Target source, int defaultPort) => new()
    {
        Id = source.Id,
        Host = source.Host,
        Comment = source.Comment,
        Enabled = source.Enabled,
        IntervalMs = source.IntervalMs,
        TimeoutMs = source.TimeoutMs,
        Kind = ProbeKind.Tcp,
        Port = source.Kind == ProbeKind.Tcp && source.Port > 0 ? source.Port : defaultPort,
    };

    public Task StopAsync()
    {
        // 停止ボタンの直後にクリア操作が来ると、ここへ二重に入ってくる。
        // 2 回目を素通しすると停止処理が二重に走り、即 return にすると
        // 「停止を待ってから消す」つもりの呼び出し元が待てない。
        // 進行中の停止処理そのものを返して、全員に同じ完了を待たせる
        if (_stopTask is not null) return _stopTask;
        if (!IsRunning) return Task.CompletedTask;

        return _stopTask = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        // 必ず 1 度は呼び出し元へ制御を返してから中身に入る。
        //
        // ここを同期で走り切ると、下の finally の「_stopTask = null」が
        // 呼び出し元の「_stopTask = StopCoreAsync()」より先に動く。すると
        // <b>完了済みの停止処理が _stopTask に残りっぱなし</b>になり、
        // 次に停止を押したとき StopAsync() がそれをそのまま返して素通りする
        // ＝「停止を押しても止まらない」（ユーザー報告）。
        //
        // 停止が同期で終わるのは珍しくない（宛先が 0 件、あるいは
        // すべてのループが既に畳まれているとき）ので、必ず起きうる道。
        await Task.Yield();

        // 後片付けの途中で何が起きても「実行中」のまま固まらせない。
        // 以前は記録の書き出しなどで例外が出ると IsRunning が false にならず、
        // 停止を押しても開始ボタンが「実行中」から戻らなかった（ユーザー報告）
        try
        {
            _pump.Stop();

            try
            {
                await _engine.StopAsync();
                OnPump(this, EventArgs.Empty);   // 残っている結果を取りこぼさない
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MonitorViewModel.StopCoreAsync(engine)");
            }

            try
            {
                // 続いている不通は「復旧した」ではなく「測定を止めた」として閉じる
                foreach (OutageRecord closed in Tracker.CloseAll(DateTime.Now.Ticks, OutageCloseReason.Stopped))
                    _log.Append(SessionLogFormatter.OutageClosed(DateTime.Now, closed));

                _log.Stop(SessionLogFormatter.Footer(DateTime.Now));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MonitorViewModel.StopCoreAsync(log)");
            }
        }
        finally
        {
            // 測定は確実に止まっているので、表示も必ず戻す
            IsRunning = false;
            StatusMessage = "測定を停止しました。";
            _stopTask = null;
        }
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

        // 終了間際でもログは書き切る。ここだけは UI スレッドで同期書き込みになるが、
        // 量は残りバッファぶんだけで、アプリを閉じる場面なので許容する
        if (IsRunning)
            _log.Stop(SessionLogFormatter.Footer(DateTime.Now));

        _log.Dispose();
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
            OutageRecord? change = Tracker.Observe(KeyOf(row.Target), row.Host, result.Sample);

            _log.Append(SessionLogFormatter.Sample(result.Sample.Timestamp, row.Host, result.ResolvedAddress, result.Sample));

            if (change is not null)
            {
                _log.Append(change.IsOngoing
                    ? SessionLogFormatter.OutageOpened(DateTime.Now, change)
                    : SessionLogFormatter.OutageClosed(DateTime.Now, change));
            }

            _touched.Add(row);
        }

        // 結果が届かなくなった宛先は _touched に入らないので、別途確かめる。
        // 「応答が無い」と「測っていない」を取り違えると、確認ツールとして嘘をつく
        if (IsRunning)
        {
            long now = DateTime.Now.Ticks;

            // 同時実行の上限を超える数の宛先が全滅していると、1 周に
            // 「タイムアウト × 周回数」かかる。停止判定の閾値にもこれを織り込む
            int queueRounds = (Rows.Count + _settings.MaxConcurrency - 1) / Math.Max(1, _settings.MaxConcurrency);

            foreach (TargetRowViewModel row in Rows)
            {
                row.CheckStalled(now, queueRounds);
                _touched.Add(row);
            }
        }

        bool selectedChanged = false;

        foreach (TargetRowViewModel row in _touched)
        {
            bool wasDown = row.IsDown;
            bool changed = row.Refresh();

            if (row.IsDown != wasDown)
                Reclassify(row);

            if (changed && ReferenceEquals(row, SelectedRow))
                selectedChanged = true;
        }

        // 詳細欄の文字列は組み立てが安くないので、選択行が実際に変わったときだけ作り直す
        if (selectedChanged)
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

        DetailText = string.Join("　　", (string[])
        [
            row.Host,
            $"最小 {TargetRowViewModel.FormatMilliseconds(stats.MinMs)}",
            $"平均 {TargetRowViewModel.FormatMilliseconds(stats.AverageMs)}",
            $"最大 {TargetRowViewModel.FormatMilliseconds(stats.MaxMs)}",
            $"ジッタ {TargetRowViewModel.FormatMilliseconds(stats.JitterMs)}",
            $"ロス {TargetRowViewModel.FormatLoss(stats.LossPercent)}（{stats.Attempts - stats.Successes} / {stats.Attempts} 回）",
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
        if (Tracker.Remove(KeyOf(row.Target), DateTime.Now.Ticks) is { } closed)
            _log.Append(SessionLogFormatter.OutageClosed(DateTime.Now, closed));

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

    private void SortInPlace(ObservableCollection<TargetRowViewModel> collection)
    {
        // Move は 1 回ごとに CollectionChanged が飛び、一覧がコンテナを動かす。
        // 挿入ソートだと大きく入れ替えたとき通知が O(n²) 回になり、500 件を
        // 表計算から貼り直しただけで UI が数秒固まる。並び順を先に決めて、
        // 位置のずれた要素だけを Move する（通知は最大 n 回）
        List<TargetRowViewModel> sorted = [.. collection.OrderBy(r => r, Comparer<TargetRowViewModel>.Create(CompareRows))];

        for (int i = 0; i < sorted.Count; i++)
        {
            if (ReferenceEquals(collection[i], sorted[i]))
                continue;

            collection.Move(collection.IndexOf(sorted[i]), i);
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

    /// <summary>「履歴を消去」の確認。結線前の既定は「消さない」。</summary>
    public Func<string, bool>? ConfirmClear { get; set; }

    /// <summary>ボタンからの「履歴を消去」。取り消せないので必ず聞く（2026-08-20 の UI 改善）。</summary>
    private void ClearHistoryFromButton()
    {
        if (ConfirmClear?.Invoke("測定の履歴（RTT・ロス率・推移）を消します。宛先は残ります。") != true)
            return;

        ClearHistory();
    }

    /// <summary>確認なしの実体。「すべて消す」など、確認済みの経路からも呼ばれる。</summary>
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
        while (index < to.Count && CompareRows(to[index], row) < 0)
            index++;

        to.Insert(index, row);

        // 選ばれている行が欄をまたいで移ったときは、移った先でも選択を保つ
        // （元の一覧から Remove された時点で、その一覧の選択は外れている）
        if (ReferenceEquals(SelectedRow, row))
            SyncListSelection();

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

    /// <summary>
    /// いまの宛先に名前を付けて残す。同じ名前なら上書き。
    /// 名前を聞くのは画面側（<see cref="AskListName"/>）。
    /// </summary>
    private void SaveList()
    {
        if (AskListName?.Invoke(SelectedListName ?? "") is not { } asked) return;

        string name = asked.Trim();

        if (name.Length == 0)
        {
            StatusMessage = "リストの名前が空です。";
            return;
        }

        SaveList(name, TargetListText);
    }

    /// <summary>名前と中身を決めて残す。自己診断からも呼べるように分けてある。</summary>
    internal void SaveList(string name, string text)
    {
        SavedListStore[name] = text;

        if (!SavedLists.Contains(name))
        {
            SavedLists.Add(name);

            // 並べ替えは入れ直しで済ませる（数はせいぜい数十）
            string[] sorted = [.. SavedLists.OrderBy(n => n, StringComparer.CurrentCulture)];

            SavedLists.Clear();
            foreach (string each in sorted) SavedLists.Add(each);
        }

        _selectedListName = name;
        OnPropertyChanged(nameof(SelectedListName));
        DeleteListCommand.RaiseCanExecuteChanged();

        SaveSettings();
        StatusMessage = $"宛先リスト「{name}」に残しました。";
    }

    /// <summary>
    /// 選んでいるリストを消す。<b>画面の宛先はそのまま残す</b> —
    /// 名前を消しただけで測定対象が消えると、測っている最中に事故になる。
    /// </summary>
    private void DeleteList()
    {
        if (SelectedListName is not { Length: > 0 } name) return;

        // 消すのは取り消せない。必ず聞く（2026-08-18 ユーザー指示）。
        // 結線前の既定は「いいえ」— 聞けないなら消さない方が安全
        if (ConfirmDelete?.Invoke(
                $"宛先リスト「{name}」を消します。\n\nいま画面に出ている宛先は残ります。") != true)
        {
            return;
        }

        SavedListStore.Remove(name);
        SavedLists.Remove(name);

        _selectedListName = null;
        OnPropertyChanged(nameof(SelectedListName));
        DeleteListCommand.RaiseCanExecuteChanged();

        SaveSettings();
        StatusMessage = $"宛先リスト「{name}」を消しました（いまの宛先はそのままです）。";
    }

    private void SaveSettings()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"宛先リストを保存できませんでした: {ex.Message}";
        }
    }

    private void Save()
    {
        try
        {
            var document = new TargetDocument
            {
                Targets = [.. Rows.Select(r => r.Target)],
                Settings = _settings,
            };

            if (_alwaysTcp)
                Settings.Current.Tcp = document;
            else
                Settings.Current.Ping = document;

            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"宛先リストを保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>初回起動時の宛先。何も無い画面より、すぐ測れる方が親切。</summary>
    private static IEnumerable<Target> CreateStarterTargets(bool tcp, NetworkSnapshot snapshot)
    {
        if (tcp)
        {
            // TCP は「どのポートが開いているか」を見る道具なので、
            // ポートまで書いた例を置いておく方が使い方が伝わる
            yield return new Target { Host = "www.google.com:443", Comment = "HTTPS の疎通" };
            yield return new Target { Host = "8.8.8.8:53", Comment = "DNS（TCP）" };
            yield break;
        }

        if (snapshot.Gateway is not null)
            yield return new Target { Host = snapshot.Gateway.ToString(), Comment = "既定ゲートウェイ" };

        foreach (IPAddress dns in snapshot.DnsServers.Take(1))
            yield return new Target { Host = dns.ToString(), Comment = "DNS サーバ" };

        yield return new Target { Host = "8.8.8.8", Comment = "外部疎通の基準" };
    }
    /// <summary>
    /// 測定を止め、結果を捨てて起動直後の状態へ戻す。
    /// <b>宛先そのものは残す。</b>消したいのは測った記録であって、
    /// 打ち込んだ宛先リストではない。
    /// </summary>
    public async Task ResetAsync()
    {
        await ResetResultsAsync();

        SelectedRow = null;
        DetailText = "行を選ぶと、その宛先の詳しい統計が出ます。";
        TcpPort = "443";
        StatusMessage = IdleHint;
    }

    /// <summary>
    /// 測った結果だけを捨てる。宛先も、他の画面の内容も触らない。
    ///
    /// 作業の途中で「ここから測り直したい」ときに使う。全体のクリアと違って
    /// 作業前の記録や機器の貼り付けは残るので、やり直しの範囲が小さい。
    /// </summary>
    public async Task ResetResultsAsync()
    {
        // 停止を待ちきってから消す。待たないと、停止処理が最後に取り込む
        // パイプ残りの結果が、クリア済みの行へ後から流れ込む
        if (IsRunning)
            await StopAsync();

        ClearHistory();

        // 不通の記録も捨てる。作業タブの判定がここを見ているため、
        // 残すと「消したのに前の障害が出てくる」ことになる
        Tracker = new OutageTracker(_settings.IntervalMs);
        StartedAt = null;

        StatusMessage = "測定結果を消しました。";
    }

}
