using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Cloud;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>組織の選択肢。</summary>
public sealed record MerakiOrganizationItem(string Id, string Name);

/// <summary>クライアント一覧をどこまでさかのぼるか。</summary>
public sealed record MerakiTimespan(string Name, int Seconds);

/// <summary>
/// Meraki ダッシュボードから組織配下の情報を引く画面。
///
/// API キーは保存しない（毎回入力）。settings.json にも書かないし、
/// 画面の文言やログにも載せない — このアプリは画面を PNG で保存できるので、
/// 入力欄も伏せ字（PasswordBox）にしてある。
/// </summary>
public sealed class MerakiViewModel : ObservableObject, IDisposable
{
    private readonly MerakiDashboard _dashboard = new();

    private string _apiKey = "";
    private MerakiOrganizationItem? _selectedOrganization;
    private MerakiNetworkRow? _selectedNetwork;
    private MerakiTimespan _selectedTimespan;
    private string _status = "";
    private string _notice = "";
    private string _globalIpText = "—";
    private bool _showConnection = true;
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public MerakiViewModel()
    {
        _selectedTimespan = Timespans[1];

        FetchCommand = new RelayCommand(() => _ = FetchAsync(), () => !IsBusy && ApiKey.Length > 0);
        FetchClientsCommand = new RelayCommand(
            () => _ = FetchClientsAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedNetwork is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);

        SaveNetworksCommand = new RelayCommand(
            () => Save("networks", MerakiCatalog.ToCsv(NetworkRows)), () => NetworkRows.Count > 0);
        SaveDevicesCommand = new RelayCommand(
            () => Save("devices", MerakiCatalog.ToCsv(DeviceRows)), () => DeviceRows.Count > 0);
        SaveUplinksCommand = new RelayCommand(
            () => Save("uplinks", MerakiCatalog.ToCsv(UplinkRows)), () => UplinkRows.Count > 0);
        SaveClientsCommand = new RelayCommand(
            () => Save("clients", MerakiCatalog.ToCsv(ClientRows)), () => ClientRows.Count > 0);

        Notice = "ダッシュボードの [My profile] で発行した API キーを入れて「取得」を押してください。"
               + "キーは保存しません。";
    }

    /// <summary>消去のときに画面の伏せ字欄も空にしてもらう（PasswordBox はバインドできない）。</summary>
    public event EventHandler? ApiKeyCleared;

    public ObservableCollection<MerakiOrganizationItem> Organizations { get; } = [];
    public ObservableCollection<MerakiNetworkRow> NetworkRows { get; } = [];
    public ObservableCollection<MerakiDeviceRow> DeviceRows { get; } = [];
    public ObservableCollection<MerakiUplinkRow> UplinkRows { get; } = [];
    public ObservableCollection<MerakiClientRow> ClientRows { get; } = [];

    public IReadOnlyList<MerakiTimespan> Timespans { get; } =
    [
        new("1 時間", 3600),
        new("1 日", 86400),
        new("7 日", 604800),
        new("30 日", 2592000),
    ];

    public RelayCommand FetchCommand { get; }
    public RelayCommand FetchClientsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveNetworksCommand { get; }
    public RelayCommand SaveDevicesCommand { get; }
    public RelayCommand SaveUplinksCommand { get; }
    public RelayCommand SaveClientsCommand { get; }

    /// <summary>伏せ字欄から流し込まれる。ここ以外のどこにも書き出さない。</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (!SetProperty(ref _apiKey, value)) return;

            FetchCommand.RaiseCanExecuteChanged();
            FetchClientsCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 接続の欄を開いているか。<b>取れたら畳む</b> — 一覧に画面を使いたいので。
    /// もう一度出したいときは「接続先」を押す。
    /// </summary>
    public bool ShowConnection
    {
        get => _showConnection;
        private set
        {
            if (SetProperty(ref _showConnection, value)) OnPropertyChanged(nameof(ConnectionToggleText));
        }
    }

    /// <summary>畳んでいる間も、どの組織を見ているかは 1 行で見えるようにしておく。</summary>
    public string ConnectionSummary => SelectedOrganization is { } organization
        ? $"{organization.Name}（キーは入力済み）"
        : "組織は未選択";

    public string ConnectionToggleText => ShowConnection ? "接続先 ▴" : "接続先 ▾";

    public RelayCommand ToggleConnectionCommand { get; }

    /// <summary>取れたら接続の欄を畳む。押せば戻る。</summary>
    private void Collapse()
    {
        ShowConnection = false;
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    public MerakiOrganizationItem? SelectedOrganization
    {
        get => _selectedOrganization;
        set => SetProperty(ref _selectedOrganization, value);
    }

    public MerakiNetworkRow? SelectedNetwork
    {
        get => _selectedNetwork;
        set
        {
            if (SetProperty(ref _selectedNetwork, value))
                FetchClientsCommand.RaiseCanExecuteChanged();
        }
    }

    public MerakiTimespan SelectedTimespan
    {
        get => _selectedTimespan;
        set => SetProperty(ref _selectedTimespan, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>恒常的な案内（キー未入力など）。Status と違って上書きで消えない。</summary>
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

    /// <summary>アップリンクのグローバル IP をまとめた 1 行。</summary>
    public string GlobalIpText
    {
        get => _globalIpText;
        private set => SetProperty(ref _globalIpText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            FetchCommand.RaiseCanExecuteChanged();
            FetchClientsCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 組織配下をまとめて取る。組織が 1 つならそのまま続け、
    /// 複数あるときは選んでもらってからもう一度押してもらう。
    /// </summary>
    private async Task FetchAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            CancellationToken token = _cts.Token;

            Status = "組織を確認しています…";
            IReadOnlyList<(string Id, string Name)> organizations =
                MerakiCatalog.ParseOrganizations(await _dashboard.OrganizationsAsync(ApiKey, token));

            SyncOrganizations(organizations);

            if (Organizations.Count == 0)
            {
                Status = "参照できる組織がありませんでした。";
                return;
            }

            if (Organizations.Count == 1)
                SelectedOrganization = Organizations[0];

            if (SelectedOrganization is null)
            {
                Status = $"組織が {Organizations.Count} 件あります。組織を選んでから、もう一度「取得」を押してください。";
                return;
            }

            string organizationId = SelectedOrganization.Id;
            Notice = "";

            Status = "ネットワーク一覧を取得しています…";
            IReadOnlyList<string> networkPages = await _dashboard.NetworksAsync(ApiKey, organizationId, token);
            IReadOnlyList<MerakiNetworkRow> networks = MerakiCatalog.ParseNetworks(networkPages);
            Replace(NetworkRows, networks);
            SelectedNetwork ??= NetworkRows.FirstOrDefault();

            Status = "機器一覧を取得しています…";
            IReadOnlyList<string> devicePages = await _dashboard.DevicesAsync(ApiKey, organizationId, token);
            IReadOnlyList<string> statusPages = await _dashboard.DeviceStatusesAsync(ApiKey, organizationId, token);
            Replace(DeviceRows, MerakiCatalog.JoinDevices(devicePages, statusPages, networks));

            Status = "アップリンクの状態を取得しています…";
            IReadOnlyList<string> uplinkPages = await _dashboard.UplinksAsync(ApiKey, organizationId, token);
            IReadOnlyList<MerakiUplinkRow> uplinks = MerakiCatalog.ParseUplinks(uplinkPages, networks);
            Replace(UplinkRows, uplinks);
            GlobalIpText = MerakiCatalog.GlobalIpSummary(uplinks);

            Status = $"ネットワーク {NetworkRows.Count} 件 / 機器 {DeviceRows.Count} 台 / "
                   + $"回線 {UplinkRows.Count} 本（{DateTime.Now:HH:mm:ss} 時点）。";

            if (_dashboard.WasTruncated)
                Notice = "⚠ 件数が多いため途中までしか取得できていません。ダッシュボードで全体を確認してください。";

            Collapse();
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            // 途中で失敗しても、取れたところまでは保存できるようにする
            RefreshSaveCommands();
        }
    }

    private async Task FetchClientsAsync()
    {
        if (SelectedNetwork is not { } network) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            Status = $"「{network.Name}」のクライアントを取得しています…";

            IReadOnlyList<string> pages = await _dashboard.ClientsAsync(
                ApiKey, network.Id, SelectedTimespan.Seconds, _cts.Token);

            Replace(ClientRows, MerakiCatalog.ParseClients(pages));

            Status = $"「{network.Name}」で {ClientRows.Count} 台（直近 {SelectedTimespan.Name}）。";

            if (_dashboard.WasTruncated)
                Notice = "⚠ 台数が多いため途中までしか取得できていません。期間を短くして試してください。";
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"クライアントを取得できませんでした: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchClientsAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>選択中の組織はできるだけ保つ（取り直しのたびに選び直させない）。</summary>
    private void SyncOrganizations(IReadOnlyList<(string Id, string Name)> organizations)
    {
        string? keep = SelectedOrganization?.Id;

        Organizations.Clear();
        foreach ((string id, string name) in organizations)
            Organizations.Add(new MerakiOrganizationItem(id, name));

        SelectedOrganization = Organizations.FirstOrDefault(o => o.Id == keep);
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        target.Clear();
        foreach (T row in rows)
            target.Add(row);
    }

    private void Save(string kind, CsvTable table)
    {
        if (Services.CsvExport.Save($"meraki-{kind}", table) is { } message)
            Status = message;
    }

    /// <summary>すべて消す。キーも画面の伏せ字欄も残さない。</summary>
    public void Reset()
    {
        _cts?.Cancel();

        Organizations.Clear();
        NetworkRows.Clear();
        DeviceRows.Clear();
        UplinkRows.Clear();
        ClientRows.Clear();

        SelectedOrganization = null;
        SelectedNetwork = null;
        GlobalIpText = "—";
        ShowConnection = true;
        Status = "";
        ApiKey = "";
        ApiKeyCleared?.Invoke(this, EventArgs.Empty);

        RefreshSaveCommands();

        Notice = "ダッシュボードの [My profile] で発行した API キーを入れて「取得」を押してください。"
               + "キーは保存しません。";
    }

    private void RefreshSaveCommands()
    {
        SaveNetworksCommand.RaiseCanExecuteChanged();
        SaveDevicesCommand.RaiseCanExecuteChanged();
        SaveUplinksCommand.RaiseCanExecuteChanged();
        SaveClientsCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _dashboard.Dispose();
    }
}
