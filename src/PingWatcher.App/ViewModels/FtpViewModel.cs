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
public sealed record FtpLogRow(string Time, string Remote, string Text);

/// <summary>
/// FTP サーバ画面。現場で機器の config バックアップを受けるための使い捨てサーバ。
/// 平文なので既定は停止。ユーザーが「開始」を押したときだけ待ち受ける。
/// </summary>
public sealed class FtpViewModel : ObservableObject
{
    private const int MaxRows = 2000;

    private readonly string? _localAddress;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private FtpServer? _server;
    private SessionLogService? _log;

    private string _port = "21";
    private string _user = string.Empty;
    private string _password = string.Empty;
    private bool _isRunning;
    private string _status = string.Empty;

    /// <param name="localAddress">自分の IPv4。機器側のコマンド例に使う。</param>
    public FtpViewModel(string? localAddress)
    {
        _localAddress = localAddress;

        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    /// <summary>公開するフォルダ。exe と同じ場所の ftp フォルダに固定。</summary>
    public static string RootDirectory => AppData.PathOf("ftp");

    public ObservableCollection<FtpLogRow> Log { get; } = [];

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public string Port { get => _port; set => SetProperty(ref _port, value); }
    public string User { get => _user; set { if (SetProperty(ref _user, value)) OnPropertyChanged(nameof(CommandHint)); } }
    public string Password { get => _password; set { if (SetProperty(ref _password, value)) OnPropertyChanged(nameof(CommandHint)); } }

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

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>機器側で叩くコマンドの例。IP・ユーザー・パスを埋めて出す。</summary>
    public string CommandHint
    {
        get
        {
            string host = _localAddress ?? "<このPCのIP>";

            string credential = string.IsNullOrEmpty(User)
                ? string.Empty
                : string.IsNullOrEmpty(Password) ? $"{User}@" : $"{User}:{Password}@";

            return $"copy running-config ftp://{credential}{host}/running-config\n" +
                   "（機器が繋がらないときは機器側で「ip ftp passive」を設定）";
        }
    }

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

            _server = new FtpServer(RootDirectory, User, Password);
            _server.Event += OnServerEvent;
            _server.Start(port);

            _log = new SessionLogService();
            _log.Start("ftp", [$"# PingWatcher FTP ログ  ポート {port.ToString(CultureInfo.InvariantCulture)}"]);

            IsRunning = true;
            Status = $"ポート {port} で待ち受けています。使い終わったら停止してください。";
        }
        catch (SocketException ex)
        {
            _server?.Dispose();
            _server = null;
            Status = $"ポート {port} で待ち受けられません（{ex.SocketErrorCode}）。ほかのアプリが使っているか、権限が足りません。別のポート（例: 2121）をお試しください。";
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

    private void OnServerEvent(FtpEvent e)
    {
        // サーバのスレッドから来るので UI スレッドへ渡す
        _dispatcher.BeginInvoke(() =>
        {
            string time = e.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Log.Insert(0, new FtpLogRow(time, e.RemoteAddress, e.Text));

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
