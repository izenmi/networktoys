using System.Collections.ObjectModel;
using System.IO;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Files;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// 2 ペインの 1 行。左（この PC）と右（接続先）で同じ型を使う。
/// 表の見た目も同じにできるので、行テンプレートを 1 つで済ませる。
/// </summary>
public sealed class FileRowViewModel(RemoteEntry entry, string? fullPath)
{
    public string Name => entry.Name;

    public bool IsDirectory => entry.IsDirectory;

    /// <summary>この PC 側だけ持つ実パス。接続先側は null。</summary>
    public string? FullPath => fullPath;

    /// <summary>並べ替え用。フォルダを常に上にしたいので先頭に印を付ける。</summary>
    public string SortKey => (entry.IsDirectory ? "0" : "1") + entry.Name;

    public string Icon => entry.IsDots ? "↰" : entry.IsDirectory ? "📁" : "📄";

    public string SizeText => entry.IsDirectory ? "" : FormatSize(entry.Size);

    public long Size => entry.Size;

    public DateTime Modified => entry.Modified;

    public string ModifiedText
        => entry.Modified == DateTime.MinValue ? "" : entry.Modified.ToString("yyyy/MM/dd HH:mm");

    public bool IsParent => entry.IsDots;

    private static string FormatSize(long bytes)
    {
        // ファイルの大きさは 1024 基数（回線速度の 1000 基数と混ぜない）
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// ファイル転送（FTP / SFTP のクライアント）。左に この PC、右に 接続先。
///
/// <b>タブを開いただけでは繋がない。</b>「接続」を押して初めて外へ出る
/// （Meraki・業務確認と同じ。CI から本番へ通信が飛ぶ作りにしない）。
///
/// <b>パスワードは保存しない。</b>覚えるのは接続先とユーザー名だけ
/// （収集タブと同じ流儀）。画面側は <c>PasswordBox</c> で受ける。
/// </summary>
public sealed class TransferViewModel : ObservableObject
{
    private IRemoteFiles? _client;
    private CancellationTokenSource? _cts;

    private RemoteKind _kind = RemoteKind.Sftp;
    private string _host = string.Empty;
    private string _port = "22";
    private string _userName = string.Empty;
    private string _password = string.Empty;

    private string _localPath = string.Empty;
    private string _remotePath = "/";
    /// <summary>何もしていないときの案内。<b>初期値と Reset の戻し先を空文字にしない</b>
    /// （何をすれば動くかが見えない画面になる。2026-08-20 の UI 改善）。</summary>
    private const string IdleHint = "接続先を入れて「接続」を押すと、相手のファイル一覧が出ます。";

    private string _status = IdleHint;
    private string _fingerprint = string.Empty;
    private string _progressText = string.Empty;
    private double _progressPercent;
    private bool _isBusy;
    private bool _isConnected;
    private bool _showConnection = true;

    /// <summary>
    /// 取り消せない操作の確認。<b>画面が結線するまでは「いいえ」</b>
    /// （結線し忘れたときに黙って消えるより、何も起きない方がよい）。
    /// </summary>
    public Func<string, bool> Confirm { get; set; } = _ => false;

    /// <summary>名前の入力。取り消されたら null。画面が結線するまでは取り消し扱い。</summary>
    public Func<string, string?> Ask { get; set; } = _ => null;

    public TransferViewModel()
    {
        // コマンドを先に作る。状態を先に組むと setter が未生成のコマンドを触って落ちる
        ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => !IsBusy && !IsConnected && Host.Length > 0);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);

        UploadCommand = new RelayCommand(() => _ = UploadAsync(), () => IsConnected && !IsBusy && SelectedLocal is { IsParent: false });
        DownloadCommand = new RelayCommand(() => _ = DownloadAsync(), () => IsConnected && !IsBusy && SelectedRemote is { IsParent: false });

        MakeDirectoryCommand = new RelayCommand(() => _ = MakeDirectoryAsync(), () => IsConnected && !IsBusy);
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => IsConnected && !IsBusy && SelectedRemote is { IsParent: false });
        RenameCommand = new RelayCommand(() => _ = RenameAsync(), () => IsConnected && !IsBusy && SelectedRemote is { IsParent: false });

        RefreshLocalCommand = new RelayCommand(LoadLocal);
        RefreshRemoteCommand = new RelayCommand(() => _ = LoadRemoteAsync(RemotePathText), () => IsConnected && !IsBusy);

        _localPath = DefaultLocalPath();
        LoadLocal();
    }

    public ObservableCollection<FileRowViewModel> LocalRows { get; } = [];
    public ObservableCollection<FileRowViewModel> RemoteRows { get; } = [];

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand MakeDirectoryCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand RefreshLocalCommand { get; }
    public RelayCommand RefreshRemoteCommand { get; }

    /// <summary>true = SFTP。ラジオ 2 つで切り替えるので bool にしている。</summary>
    public bool IsSftp
    {
        get => _kind == RemoteKind.Sftp;
        set
        {
            if (!SetProperty(ref _kind, value ? RemoteKind.Sftp : RemoteKind.Ftp, nameof(IsSftp))) return;

            OnPropertyChanged(nameof(IsFtp));

            // 既定のポートも一緒に動かす。触っていない値を持ち越すと繋がらない
            if (_port is "22" or "21") Port = value ? "22" : "21";
        }
    }

    public bool IsFtp
    {
        get => _kind == RemoteKind.Ftp;
        set { if (value) IsSftp = false; }
    }

    public string Host
    {
        get => _host;
        set { if (SetProperty(ref _host, value)) ConnectCommand.RaiseCanExecuteChanged(); }
    }

    public string Port { get => _port; set => SetProperty(ref _port, value); }

    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }

    /// <summary>画面の <c>PasswordBox</c> から書き込むだけ。<b>保存しない。</b></summary>
    public string Password { set => _password = value; }

    public string LocalPath
    {
        get => _localPath;
        set => SetProperty(ref _localPath, value);
    }

    /// <summary>
    /// 接続先のいまの場所。<b>プロパティ名を「RemotePath」にしない</b> —
    /// <see cref="Core.Files.RemotePath"/> と衝突して、クラス側が隠れてしまう。
    /// </summary>
    public string RemotePathText
    {
        get => _remotePath;
        private set => SetProperty(ref _remotePath, value);
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>受け入れたホスト鍵の指紋。SFTP のときだけ出る。</summary>
    public string Fingerprint { get => _fingerprint; private set => SetProperty(ref _fingerprint, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RaiseAll(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;

            RaiseAll();

            // 繋がったら接続の欄を畳む（2 ペインに画面を使いたい）。切れたら戻す
            ShowConnection = !value;
            OnPropertyChanged(nameof(ConnectionSummary));
        }
    }

    /// <summary>接続の欄を開いているか。<b>繋がったら畳む</b>。押せば戻る。</summary>
    public bool ShowConnection
    {
        get => _showConnection;
        private set
        {
            if (SetProperty(ref _showConnection, value)) OnPropertyChanged(nameof(ConnectionToggleText));
        }
    }

    /// <summary>畳んでいる間も、どこへ何で繋いだかは 1 行で見えるようにしておく。</summary>
    public string ConnectionSummary
        => Host.Length > 0 ? $"{(IsSftp ? "SFTP" : "FTP")}　{Host}／{UserName}" : "未接続";

    public string ConnectionToggleText => ShowConnection ? "接続先 ▴" : "接続先 ▾";

    public RelayCommand ToggleConnectionCommand { get; }

    private FileRowViewModel? _selectedLocal;
    private FileRowViewModel? _selectedRemote;

    public FileRowViewModel? SelectedLocal
    {
        get => _selectedLocal;
        set { if (SetProperty(ref _selectedLocal, value)) UploadCommand.RaiseCanExecuteChanged(); }
    }

    public FileRowViewModel? SelectedRemote
    {
        get => _selectedRemote;
        set
        {
            if (!SetProperty(ref _selectedRemote, value)) return;

            DownloadCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            RenameCommand.RaiseCanExecuteChanged();
        }
    }

    // ===== 接続 =====

    private async Task ConnectAsync()
    {
        Disconnect();

        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            Status = "ポート番号が不正です。";
            return;
        }

        IRemoteFiles client = _kind == RemoteKind.Sftp
            ? new SftpFileClient()
            : new FtpFileClient();

        IsBusy = true;
        Status = $"{Host}:{port} へ接続しています…";

        try
        {
            _cts = new CancellationTokenSource();

            await client.ConnectAsync(Host, port, UserName, _password, _cts.Token);

            _client = client;
            IsConnected = true;
            Fingerprint = client.Fingerprint ?? string.Empty;
            Status = $"{Host} に接続しました。";

            await LoadRemoteAsync("/");
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            Status = "接続を中止しました。";
        }
        catch (Exception ex)
        {
            client.Dispose();
            Status = $"接続できません: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;

        IsConnected = false;
        RemoteRows.Clear();
        Fingerprint = string.Empty;
        ProgressText = string.Empty;
        ProgressPercent = 0;
    }

    // ===== この PC 側 =====

    public void OpenLocal(FileRowViewModel row)
    {
        if (!row.IsDirectory) return;

        LocalPath = row.IsParent
            ? Directory.GetParent(LocalPath)?.FullName ?? LocalPath
            : row.FullPath ?? LocalPath;

        LoadLocal();
    }

    private void LoadLocal()
    {
        LocalRows.Clear();

        try
        {
            if (!Directory.Exists(LocalPath)) LocalPath = DefaultLocalPath();

            // 「..」は必ず先頭。ルートでも出しておく（押しても動かないだけ）
            LocalRows.Add(new FileRowViewModel(RemoteEntry.Parent, null));

            foreach (string path in Directory.EnumerateDirectories(LocalPath).Order(StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(path);
                LocalRows.Add(new FileRowViewModel(
                    new RemoteEntry(info.Name, IsDirectory: true, 0, info.LastWriteTime), path));
            }

            foreach (string path in Directory.EnumerateFiles(LocalPath).Order(StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                LocalRows.Add(new FileRowViewModel(
                    new RemoteEntry(info.Name, IsDirectory: false, info.Length, info.LastWriteTime), path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"{LocalPath} を読めません: {ex.Message}";
        }
    }

    private static string DefaultLocalPath()
    {
        // 収集した設定を置く場所を初期値にする。無ければドキュメント
        string collect = AppData.PathOf("collect");

        return Directory.Exists(collect)
            ? collect
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // ===== 接続先 =====

    public void OpenRemote(FileRowViewModel row)
    {
        if (!row.IsDirectory) return;

        string next = row.IsParent
            ? RemotePath.Parent(RemotePathText)
            : RemotePath.Combine(RemotePathText, row.Name);

        _ = LoadRemoteAsync(next);
    }

    private async Task LoadRemoteAsync(string path)
    {
        if (_client is not { } client) return;

        IsBusy = true;

        try
        {
            _cts = new CancellationTokenSource();

            IReadOnlyList<RemoteEntry> entries = await client.ListAsync(path, _cts.Token);

            RemotePathText = path;
            RemoteRows.Clear();
            RemoteRows.Add(new FileRowViewModel(RemoteEntry.Parent, null));

            // フォルダを上に。同じ種類なら名前順
            foreach (RemoteEntry entry in entries
                         .OrderByDescending(e => e.IsDirectory)
                         .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                RemoteRows.Add(new FileRowViewModel(entry, null));
            }

            Status = $"{path} … {entries.Count} 件";
        }
        catch (OperationCanceledException)
        {
            Status = "中止しました。";
        }
        catch (Exception ex)
        {
            Status = $"一覧を取れません: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ===== 転送 =====

    private async Task DownloadAsync()
    {
        if (_client is not { } client || SelectedRemote is not { IsParent: false } row) return;

        if (row.IsDirectory)
        {
            // フォルダごとは運ばない。何をどれだけ運ぶのかが見えないまま始まる方が怖い
            Status = "フォルダは運べません。中のファイルを選んでください。";
            return;
        }

        string to = Path.Combine(LocalPath, row.Name);

        if (File.Exists(to) && !Confirm($"{row.Name} は既にあります。上書きしますか？")) return;

        await RunTransferAsync(
            token => client.DownloadAsync(
                RemotePath.Combine(RemotePathText, row.Name), to, Report(), token),
            $"{row.Name} を取得しました。");

        LoadLocal();
    }

    private async Task UploadAsync()
    {
        if (_client is not { } client || SelectedLocal is not { IsParent: false } row) return;

        if (row.IsDirectory || row.FullPath is not { } from)
        {
            Status = "フォルダは運べません。中のファイルを選んでください。";
            return;
        }

        string to = RemotePath.Combine(RemotePathText, row.Name);

        if (RemoteRows.Any(r => !r.IsDirectory && r.Name == row.Name)
            && !Confirm($"{row.Name} は接続先に既にあります。上書きしますか？"))
        {
            return;
        }

        await RunTransferAsync(
            token => client.UploadAsync(from, to, Report(), token),
            $"{row.Name} を送りました。");

        await LoadRemoteAsync(RemotePathText);
    }

    private IProgress<TransferProgress> Report()
        => new Progress<TransferProgress>(p =>
        {
            ProgressPercent = p.Percent;
            ProgressText = p.Total > 0
                ? $"{p.Name}  {p.Done:N0} / {p.Total:N0} バイト"
                : $"{p.Name}  {p.Done:N0} バイト";
        });

    private async Task RunTransferAsync(Func<CancellationToken, Task> transfer, string done)
    {
        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = string.Empty;

        try
        {
            _cts = new CancellationTokenSource();

            await transfer(_cts.Token);

            Status = done;
        }
        catch (OperationCanceledException)
        {
            Status = "転送を中止しました。";
        }
        catch (Exception ex)
        {
            Status = $"転送できません: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            ProgressPercent = 0;
        }
    }

    // ===== 作る・消す・名前を変える =====

    private async Task MakeDirectoryAsync()
    {
        if (_client is not { } client) return;

        if (Ask("新しいフォルダの名前") is not { Length: > 0 } name) return;

        await RunTransferAsync(
            token => client.MakeDirectoryAsync(RemotePath.Combine(RemotePathText, name), token),
            $"{name} を作りました。");

        await LoadRemoteAsync(RemotePathText);
    }

    private async Task DeleteAsync()
    {
        if (_client is not { } client || SelectedRemote is not { IsParent: false } row) return;

        // 取り消せない。必ず聞く
        if (!Confirm($"{row.Name} を削除します。元に戻せません。よろしいですか？")) return;

        await RunTransferAsync(
            token => client.DeleteAsync(
                RemotePath.Combine(RemotePathText, row.Name), row.IsDirectory, token),
            $"{row.Name} を削除しました。");

        await LoadRemoteAsync(RemotePathText);
    }

    private async Task RenameAsync()
    {
        if (_client is not { } client || SelectedRemote is not { IsParent: false } row) return;

        if (Ask($"{row.Name} の新しい名前") is not { Length: > 0 } name) return;

        await RunTransferAsync(
            token => client.RenameAsync(
                RemotePath.Combine(RemotePathText, row.Name),
                RemotePath.Combine(RemotePathText, name), token),
            $"{name} に変えました。");

        await LoadRemoteAsync(RemotePathText);
    }

    private void RaiseAll()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        UploadCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        MakeDirectoryCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        RefreshRemoteCommand.RaiseCanExecuteChanged();
    }
}
