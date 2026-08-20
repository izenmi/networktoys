using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Logging;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// 転送履歴の 1 行。
/// </summary>
/// <param name="Severity">syslog のシビリティ。持たない画面（FTP など）は -1。</param>
public sealed record FileServerLogRow(string Time, string Remote, string Text, int Severity = -1)
{
    /// <summary>画面に出す重大度。<b>色だけで表さない</b>ので文字でも出す。</summary>
    public string SeverityText => Severity >= 0 ? SyslogParser.NameOf(Severity) : string.Empty;

    /// <summary>err(3) 以下。</summary>
    public bool IsSevere => Severity >= 0 && SyslogParser.IsSevere(Severity);

    /// <summary>warning(4)。</summary>
    public bool IsWarning => Severity >= 0 && SyslogParser.IsWarning(Severity);
}

/// <summary>
/// ファイル配布サーバ（FTP / TFTP / SFTP）画面の共通土台。
/// 待受の開始/停止・ポート・履歴・フォルダを開く・ログ書き出しをまとめる。
/// プロトコル固有の差（既定ポート・サーバ生成・コマンド例）は派生で埋める。
/// </summary>
public abstract class FileServerViewModel : ObservableObject
{
    private const int MaxRows = 2000;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly string _logPrefix;

    private IFileServer? _server;
    private SessionLogService? _log;

    /// <summary>受け取った全行。画面の <see cref="Log"/> は絞り込みを通した写し。</summary>
    private readonly List<FileServerLogRow> _all = [];

    private string _port;
    private bool _isRunning;
    /// <summary>何もしていないときの案内。<b>初期値と Reset の戻し先を空文字にしない</b>
    /// （何をすれば動くかが見えない画面になる。2026-08-20 の UI 改善）。</summary>
    private const string IdleHint = "「開始」を押すと待ち受けが始まります。";

    private string _status = IdleHint;
    private string _filter = string.Empty;
    private int _minSeverity = -1;

    /// <param name="logPrefix">ログファイルの接頭辞（"ftp" など）。</param>
    /// <param name="defaultPort">既定の待受ポート。</param>
    /// <param name="localAddress">自分の IPv4。機器側コマンド例に使う。</param>
    protected FileServerViewModel(string logPrefix, int defaultPort, string? localAddress)
    {
        _logPrefix = logPrefix;
        LocalAddress = localAddress;
        _port = defaultPort.ToString(CultureInfo.InvariantCulture);

        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        SaveCommand = new RelayCommand(Save, () => Log.Count > 0);
        ClearCommand = new RelayCommand(ClearLogFromButton, () => Log.Count > 0);
    }

    /// <summary>公開するフォルダ。派生でプロトコルごとに分ける。</summary>
    public abstract string RootDirectory { get; }

    /// <summary>機器側で叩くコマンドの例。派生で組み立てる。</summary>
    public abstract string CommandHint { get; }

    protected string? LocalAddress { get; }

    /// <summary>コマンド例に埋める自分の IP。未取得なら見た目のプレースホルダ。</summary>
    protected string HostForHint => LocalAddress ?? "<このPCのIP>";

    public ObservableCollection<FileServerLogRow> Log { get; } = [];

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ClearCommand { get; }

    public string Port
    {
        get => _port;
        set { if (SetProperty(ref _port, value)) RefreshCommandHint(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(RunLabel));
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public string RunLabel => IsRunning ? "待受中" : "開始";

    public string Status { get => _status; protected set => SetProperty(ref _status, value); }

    /// <summary>本文・送信元の部分一致で絞る。待受は止めない。</summary>
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) RebuildLog(); }
    }

    /// <summary>
    /// この値以下のシビリティだけ残す（0=emerg 〜 7=debug で、<b>小さいほど重い</b>）。
    /// -1 は絞らない。重大度を持たない画面では使わない。
    /// </summary>
    protected int MinSeverity
    {
        get => _minSeverity;
        set { if (SetProperty(ref _minSeverity, value)) RebuildLog(); }
    }

    /// <summary>絞り込みを通して画面の一覧を作り直す。</summary>
    private void RebuildLog()
    {
        string needle = _filter.Trim();

        Log.Clear();
        foreach (FileServerLogRow row in _all)
        {
            if (_minSeverity >= 0 && (row.Severity < 0 || row.Severity > _minSeverity)) continue;

            if (needle.Length > 0
                && !row.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !row.Remote.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            Log.Add(row);
        }

        SaveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    /// <summary>指定ポートでサーバを作る。ポート番号は検証済み。</summary>
    private protected abstract IFileServer CreateServer(int port);

    /// <summary>ポート欄が変わったなど、コマンド例を出し直したいときに派生から呼ぶ。</summary>
    protected void RefreshCommandHint() => OnPropertyChanged(nameof(CommandHint));

    private void Start()
    {
        if (IsRunning) return;

        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            Status = "ポート番号が正しくありません（1〜65535）。";
            return;
        }

        try
        {
            Directory.CreateDirectory(RootDirectory);

            _server = CreateServer(port);
            _server.Event += OnServerEvent;
            _server.Start(port);

            _log = new SessionLogService();
            _log.Start(_logPrefix, [$"# NetworkToys {_logPrefix.ToUpperInvariant()} ログ  ポート {port.ToString(CultureInfo.InvariantCulture)}"]);

            IsRunning = true;
            Status = $"ポート {port} で待ち受けています。使い終わったら停止してください。";
        }
        catch (SocketException ex)
        {
            _server?.Dispose();
            _server = null;
            Status = $"ポート {port} で待ち受けられません（{ex.SocketErrorCode}）。ほかのアプリが使っているか、権限が足りません。別のポートをお試しください。";
        }
    }

    private void Stop()
    {
        if (_server is not null)
        {
            _server.Event -= OnServerEvent;
            _server.Dispose();
            _server = null;
        }

        _log?.Dispose();
        _log = null;

        IsRunning = false;
        Status = "停止しました。";
    }

    private void OnServerEvent(FileServerEvent e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            string time = e.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var row = new FileServerLogRow(time, e.RemoteAddress, e.Text, e.Severity);

            _all.Insert(0, row);
            while (_all.Count > MaxRows)
                _all.RemoveAt(_all.Count - 1);

            // 絞り込みに合う行だけ画面へ。全体の作り直しは重いので届いた 1 行だけ足す
            if (Matches(row))
            {
                Log.Insert(0, row);
                while (Log.Count > MaxRows)
                    Log.RemoveAt(Log.Count - 1);
            }

            SaveCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
        });

        _log?.Append($"{e.At.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture)}\t{e.RemoteAddress}\t{e.Text}");
    }

    /// <summary>
    /// 画面に出ている行を CSV に出す。<c>.log</c> は書いているが、
    /// これまで画面からは「フォルダを開く」しかできなかった。
    /// </summary>
    private void Save()
    {
        var table = new CsvTable(
            ["時刻", "送信元", "重大度", "内容"],
            [.. Log.Select(r => new[] { r.Time, r.Remote, r.SeverityText, r.Text })]);

        if (Services.CsvExport.Save(_logPrefix, table) is { } message)
            Status = message;
    }

    /// <summary>一覧だけ消す。待受は止めないし、ファイルのログにも触らない。</summary>
    private bool Matches(FileServerLogRow row)
    {
        if (_minSeverity >= 0 && (row.Severity < 0 || row.Severity > _minSeverity)) return false;

        string needle = _filter.Trim();
        return needle.Length == 0
            || row.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || row.Remote.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>「一覧を消す」の確認。結線前の既定は「消さない」。</summary>
    public Func<string, bool>? ConfirmClear { get; set; }

    /// <summary>ボタンからの「一覧を消す」。取り消せないので必ず聞く（2026-08-20 の UI 改善）。</summary>
    private void ClearLogFromButton()
    {
        if (ConfirmClear?.Invoke("受信の一覧を消します。ファイルに残した記録は消えません。") != true)
            return;

        ClearLog();
    }

    /// <summary>確認なしの実体。「すべて消す」（Reset）からも呼ばれる。</summary>
    private void ClearLog()
    {
        _all.Clear();
        Log.Clear();
        SaveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            Process.Start(new ProcessStartInfo { FileName = RootDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"フォルダを開けませんでした: {ex.Message}";
        }
    }

    /// <summary>アプリを閉じるとき/全消去のときに呼ぶ。</summary>
    public void Reset()
    {
        Stop();
        ClearLog();
        Status = IdleHint;
    }
}
