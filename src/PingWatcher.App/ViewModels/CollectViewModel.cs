using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Terminal;

namespace PingWatcher.App.ViewModels;

/// <summary>収集する機器 1 台ぶんの画面状態。</summary>
public sealed class CollectRowViewModel(DeviceEntry entry) : ObservableObject
{
    private bool _selected = true;
    private string _userName = entry.UserName;
    private string _status = "◌ 待機";
    private string _password = "";
    private string _enablePassword = "";

    public string Host { get; } = entry.Host;
    public int Port { get; } = entry.Port;
    public string Memo { get; } = entry.Memo;

    /// <summary>この機器から集めるか。外すと飛ばす。</summary>
    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    /// <summary>
    /// ログインパスワード。<b>設定ファイルにも引き継ぎファイルにも入れない。</b>
    /// 画面は伏せ字（PasswordBox）で受け、ここへ一方通行で流し込む。
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string EnablePassword
    {
        get => _enablePassword;
        set => SetProperty(ref _enablePassword, value);
    }

    /// <summary>記号と文字を併記する（色だけで状態を表さない決まり）。</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public void ClearSecrets()
    {
        Password = "";
        EnablePassword = "";
    }

    public DeviceEntry ToEntry() => new(Host, Port, UserName, Memo);
}

/// <summary>
/// Cisco 機器へ入ってコマンド出力を集める画面。
///
/// 認証情報は<b>機器ごとに違う</b>ので行ごとに持つ。<b>覚えるのはユーザー名だけ</b>で、
/// パスワードは毎回入力してもらう（設定ファイルにも、画面の PNG 保存にも、
/// 管理者昇格の引き継ぎファイルにも残さない）。
/// </summary>
public sealed class CollectViewModel : ObservableObject
{
    private const int TelnetPort = 23;
    private const int SshPort = 22;

    private string _deviceListText = "";
    private string _commandText = RecommendedCommands.Ios;
    private string _status = "宛先リストから取り込むか、機器を直接書いて「収集を開始」を押します。";
    private string _notice = "";
    private bool _isBusy;
    private int _connectSeconds = 10;
    private int _idleSeconds = 15;
    private string _lastFolder = "";
    private bool _useSsh = true;
    private CancellationTokenSource? _cts;

    public CollectViewModel()
    {
        Presets = [.. RecommendedCommands.Presets.Select(p => new CollectPreset(p.Name, p.Commands))];
        _selectedPreset = Presets[0];

        ApplyDeviceListCommand = new RelayCommand(ApplyDeviceList);
        ImportFromTargetsCommand = new RelayCommand(() => RequestImport?.Invoke(this, EventArgs.Empty));
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && Rows.Count > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _lastFolder.Length > 0);

        // 前回のコマンドがあれば引き継ぐ。無ければ機種プリセットの初期値のまま
        if (Settings.Current.CollectCommands.Length > 0)
            _commandText = Settings.Current.CollectCommands;

        DeviceListText = Settings.Current.CollectDevices;
    }

    /// <summary>宛先リストから取り込みたい、という合図（実際の取り込みは画面側）。</summary>
    public event EventHandler? RequestImport;

    /// <summary>収集が終わって伏せ字欄を空にしてほしい、という合図。</summary>
    public event EventHandler? SecretsCleared;

    public ObservableCollection<CollectRowViewModel> Rows { get; } = [];

    public IReadOnlyList<CollectPreset> Presets { get; }

    private CollectPreset _selectedPreset;

    public CollectPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;

            CommandText = value.Commands;
        }
    }

    public RelayCommand ApplyDeviceListCommand { get; }
    public RelayCommand ImportFromTargetsCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>機器の一覧（<c>ホスト[:ポート],ユーザー名[,メモ]</c>）。</summary>
    public string DeviceListText
    {
        get => _deviceListText;
        set
        {
            if (SetProperty(ref _deviceListText, value))
                ApplyDeviceList();
        }
    }

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
                OnPropertyChanged(nameof(CommandSummary));
        }
    }

    /// <summary>実行するコマンドの本数と、弾いたものの内訳。</summary>
    public string CommandSummary
    {
        get
        {
            CommandListParseResult parsed = CommandListParser.Parse(CommandText);
            int blocked = parsed.Commands.Count(c => c.Risk == CommandRisk.Blocked);
            int warned = parsed.Commands.Count(c => c.Risk == CommandRisk.Warned);

            string text = $"実行 {parsed.RunnableCount} 本 / 注釈 {parsed.CommentLines} 行";

            if (blocked > 0) text += $"　⛔ 実行しません {blocked} 本";
            if (warned > 0) text += $"　▲ 確認 {warned} 本";

            return text;
        }
    }

    /// <summary>弾いたコマンドの一覧（画面に理由つきで出す）。</summary>
    public IReadOnlyList<string> BlockedCommands =>
        [.. CommandListParser.Parse(CommandText).Commands
            .Where(c => c.Risk == CommandRisk.Blocked)
            .Select(c => $"⛔ {c.Command} — {c.Reason}")];

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            StartCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// SSH でつなぐか。外すと Telnet。既定の待受ポートも切り替わる
    /// （機器ごとに <c>:ポート</c> を書けばそちらが優先される）。
    /// </summary>
    public bool UseSsh
    {
        get => _useSsh;
        set
        {
            if (!SetProperty(ref _useSsh, value)) return;

            // ポートを書いていない機器の既定ポートが変わるので読み直す
            ApplyDeviceList();
        }
    }

    /// <summary>接続の待ち（秒）。</summary>
    public int ConnectSeconds
    {
        get => _connectSeconds;
        set => SetProperty(ref _connectSeconds, Math.Clamp(value, 3, 120));
    }

    /// <summary>コマンドの無音の待ち（秒）。</summary>
    public int IdleSeconds
    {
        get => _idleSeconds;
        set => SetProperty(ref _idleSeconds, Math.Clamp(value, 3, 300));
    }

    /// <summary>宛先リストから取り込む（画面側が集めた行を渡す）。</summary>
    public void Import(IEnumerable<(string Host, string Memo)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        HashSet<string> known = [.. Rows.Select(r => r.Host)];
        List<string> added = [];

        foreach ((string host, string memo) in targets)
        {
            if (host.Length == 0 || !known.Add(host)) continue;

            // 前に使ったユーザー名があれば埋めておく(覚えているのはこれだけ)
            string user = Settings.Current.CollectUserNames.GetValueOrDefault(host, "");

            added.Add(memo.Length > 0 ? $"{host},{user},{memo}" : $"{host},{user}");
        }

        if (added.Count == 0)
        {
            Status = "宛先リストから新しく足せる機器はありませんでした。";
            return;
        }

        string current = DeviceListText.TrimEnd('\n', '\r');
        DeviceListText = current.Length == 0
            ? string.Join('\n', added) + "\n"
            : current + "\n" + string.Join('\n', added) + "\n";

        Status = $"宛先リストから {added.Count} 台を取り込みました。パスワードを入れてください。";
    }

    private void ApplyDeviceList()
    {
        DeviceListParseResult parsed = DeviceListParser.Parse(DeviceListText, UseSsh ? SshPort : TelnetPort);

        // 入力中のパスワードは消さない(打ち直しになる)
        Dictionary<string, CollectRowViewModel> existing = [];
        foreach (CollectRowViewModel row in Rows)
            existing[row.Host] = row;

        Rows.Clear();

        foreach (DeviceEntry entry in parsed.Devices)
        {
            if (existing.TryGetValue(entry.Host, out CollectRowViewModel? kept))
            {
                kept.UserName = entry.UserName.Length > 0 ? entry.UserName : kept.UserName;
                Rows.Add(kept);
                continue;
            }

            Rows.Add(new CollectRowViewModel(entry));
        }

        Notice = parsed.Errors.Count > 0 ? "⚠ " + string.Join(" / ", parsed.Errors) : "";
        StartCommand.RaiseCanExecuteChanged();
    }

    private async Task RunAsync()
    {
        CommandListParseResult parsed = CommandListParser.Parse(CommandText);

        string[] commands = [.. parsed.Commands
            .Where(c => c.Risk != CommandRisk.Blocked)
            .Select(c => c.Command)];

        if (commands.Length == 0)
        {
            Status = "実行できるコマンドがありません。";
            return;
        }

        CollectRowViewModel[] targets = [.. Rows.Where(r => r.Selected)];
        if (targets.Length == 0)
        {
            Status = "収集する機器が選ばれていません。";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();

        string folder = Path.Combine(
            DeviceCollector.RootDirectory,
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        var options = new CiscoSessionOptions { IdleTimeout = TimeSpan.FromSeconds(IdleSeconds) };
        var progress = new Progress<CollectProgress>(p =>
        {
            foreach (CollectRowViewModel row in Rows.Where(r => r.Host == p.Host))
                row.Status = "● " + p.Message;
        });

        int done = 0;
        int failed = 0;

        try
        {
            foreach (CollectRowViewModel row in targets)
            {
                _cts.Token.ThrowIfCancellationRequested();

                if (row.Password.Length == 0)
                {
                    row.Status = "⛔ パスワード未入力";
                    failed++;
                    continue;
                }

                RememberUserName(row);

                var request = new CollectRequest(
                    row.Host, row.Port, UseSsh,
                    new DeviceCredentials(row.UserName, row.Password, row.EnablePassword),
                    row.Memo);

                DeviceCollectionResult result = await DeviceCollector.CollectAsync(
                    request, commands, options, TimeSpan.FromSeconds(ConnectSeconds),
                    progress, _cts.Token);

                string? saveError = DeviceCollector.Save(folder, result);

                if (result.FailureMessage is { } failure)
                {
                    row.Status = $"✕ 失敗: {failure}";
                    failed++;
                }
                else if (saveError is not null)
                {
                    row.Status = $"✕ {saveError}";
                    failed++;
                }
                else
                {
                    int problems = result.Commands.Count(c => c.Problem is not null);

                    row.Status = problems == 0
                        ? $"✔ 完了 {result.Commands.Count} 本"
                        : $"▲ 一部失敗 {result.Commands.Count - problems}/{result.Commands.Count} 本";

                    done++;
                }

                Status = $"{done + failed}/{targets.Length} 台（成功 {done} / 失敗 {failed}）";
            }

            _lastFolder = folder;
            OpenFolderCommand.RaiseCanExecuteChanged();
            Status = $"収集しました。{done} 台成功 / {failed} 台失敗　保存先: {folder}";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。ここまでの結果は保存してあります。";

            foreach (CollectRowViewModel row in targets.Where(r => r.Status.StartsWith('●')))
                row.Status = "⊘ 中断";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "CollectViewModel.RunAsync");
            Status = $"収集に失敗しました: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            // 集め終わったら鍵は捨てる。画面の伏せ字欄も空にしてもらう
            foreach (CollectRowViewModel row in Rows)
                row.ClearSecrets();

            SecretsCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>ユーザー名だけ覚える。パスワードは覚えない。</summary>
    private static void RememberUserName(CollectRowViewModel row)
    {
        if (row.UserName.Length == 0) return;

        try
        {
            Settings.Current.CollectUserNames[row.Host] = row.UserName;
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても収集は続く
        }
    }

    private void OpenFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _lastFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "CollectViewModel.OpenFolder");
        }
    }

    /// <summary>アプリを閉じるときに、機器の一覧とコマンドだけ覚える。</summary>
    public void Save()
    {
        try
        {
            Settings.Current.CollectDevices = DeviceListText;
            Settings.Current.CollectCommands = CommandText;
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても動作には影響しない
        }
    }

    public void Reset()
    {
        _cts?.Cancel();

        foreach (CollectRowViewModel row in Rows)
            row.ClearSecrets();

        SecretsCleared?.Invoke(this, EventArgs.Empty);

        DeviceListText = "";
        CommandText = RecommendedCommands.Ios;
        Status = "宛先リストから取り込むか、機器を直接書いて「収集を開始」を押します。";
        Notice = "";
    }
}

/// <summary>コマンドのプリセット（機種ごと）。</summary>
public sealed record CollectPreset(string Name, string Commands);
