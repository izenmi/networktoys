using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Logging;

namespace PingWatcher.App.ViewModels;

/// <summary>転送履歴の 1 行。</summary>
public sealed record FileServerLogRow(string Time, string Remote, string Text);

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

    private string _port;
    private bool _isRunning;
    private string _status = string.Empty;

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
            _log.Start(_logPrefix, [$"# PingWatcher {_logPrefix.ToUpperInvariant()} ログ  ポート {port.ToString(CultureInfo.InvariantCulture)}"]);

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
            Log.Insert(0, new FileServerLogRow(time, e.RemoteAddress, e.Text));

            while (Log.Count > MaxRows)
                Log.RemoveAt(Log.Count - 1);
        });

        _log?.Append($"{e.At.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture)}\t{e.RemoteAddress}\t{e.Text}");
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
        Log.Clear();
        Status = string.Empty;
    }
}
