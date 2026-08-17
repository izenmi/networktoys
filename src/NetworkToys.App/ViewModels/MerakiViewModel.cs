using System.Collections.ObjectModel;
using System.Windows;
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

/// <summary>使われ具合の見方。</summary>
public sealed record MerakiUsageKind(string Name, bool IsUtilization);

/// <summary>
/// 使われ具合の棒 1 本。<b>幅は星（比率）で持つ</b> — 画面幅を測らずに済み、
/// 窓を広げてもそのまま伸びる（Wi-Fi のチャンネル棒と同じ手）。
/// </summary>
public sealed class MerakiBarViewModel
{
    public MerakiBarViewModel(MerakiUtilizationRow row, double max)
        : this(row?.Device ?? "", row?.Model ?? "", row?.Network ?? "", row?.ValueText ?? "", row?.Value ?? 0, max)
    {
    }

    public MerakiBarViewModel(
        string device, string model, string network, string valueText, double value, double max)
    {
        Device = device;
        Model = model;
        Network = network;
        ValueText = valueText;

        double share = max > 0 ? Math.Clamp(value / max, 0, 1) : 0;

        // 0 のときも細く出す。行があるのに何も無いと「取れていない」と読めてしまう
        Bar = new GridLength(Math.Max(share, 0.004), GridUnitType.Star);
        Rest = new GridLength(Math.Max(1 - share, 0), GridUnitType.Star);
    }

    public string Device { get; }
    public string Model { get; }
    public string Network { get; }
    public string ValueText { get; }
    public GridLength Bar { get; }
    public GridLength Rest { get; }
}

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
    private IReadOnlyList<MerakiUtilizationRow> _usageRows = [];
    private IReadOnlyList<MerakiBandwidthRow> _bandwidthRows = [];
    private MerakiUsageKind _selectedUsageKind;
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

        FetchSitesCommand = new RelayCommand(() => _ = FetchSitesAsync(),
            () => !IsBusy && ApiKey.Length > 0 && NetworkRows.Count > 0);
        FetchDhcpCommand = new RelayCommand(() => _ = FetchDhcpAsync(),
            () => !IsBusy && ApiKey.Length > 0 && DeviceRows.Count > 0);
        FetchAlertsCommand = new RelayCommand(() => _ = FetchAlertsAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);

        SaveSitesCommand = new RelayCommand(() => Save("sites", MerakiCatalog.ToCsv([.. SiteRows])),
            () => SiteRows.Count > 0);
        SaveDhcpCommand = new RelayCommand(() => Save("dhcp", MerakiCatalog.ToCsv([.. DhcpRows])),
            () => DhcpRows.Count > 0);
        SaveAlertsCommand = new RelayCommand(() => Save("alerts", MerakiCatalog.ToCsv([.. AlertRows])),
            () => AlertRows.Count > 0);

        FetchUsageCommand = new RelayCommand(() => _ = FetchUsageAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);
        SaveUsageCommand = new RelayCommand(() => Save("usage", MerakiCatalog.ToCsv(_usageRows)),
            () => _usageRows.Count > 0);

        FetchBandwidthCommand = new RelayCommand(() => _ = FetchBandwidthAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);
        SaveBandwidthCommand = new RelayCommand(() => Save("bandwidth", MerakiCatalog.ToCsv(_bandwidthRows)),
            () => _bandwidthRows.Count > 0);

        _selectedUsageKind = UsageKinds[0];

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

    /// <summary>拠点ごとの内訳（クライアント数とセグメント）。</summary>
    public ObservableCollection<MerakiSiteRow> SiteRows { get; } = [];

    /// <summary>DHCP の払い出し状況。</summary>
    public ObservableCollection<MerakiDhcpRow> DhcpRows { get; } = [];

    /// <summary>アラート。</summary>
    public ObservableCollection<MerakiAlertRow> AlertRows { get; } = [];

    /// <summary>使われ具合のグラフ（棒）。</summary>
    public ObservableCollection<MerakiBarViewModel> UsageBars { get; } = [];

    /// <summary>拠点ごとの帯域（棒）。</summary>
    public ObservableCollection<MerakiBarViewModel> BandwidthBars { get; } = [];

    public MerakiUsageKind[] UsageKinds { get; } =
    [
        new MerakiUsageKind("利用率（MX）", true),
        new MerakiUsageKind("通信量（全機器）", false),
    ];

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

    /// <summary>拠点ごとの内訳を取る。クライアント数の期間は 30 日で固定。</summary>
    public RelayCommand FetchSitesCommand { get; }

    /// <summary>MX の DHCP 払い出し状況を取る。</summary>
    public RelayCommand FetchDhcpCommand { get; }

    /// <summary>組織のアラートを取る。</summary>
    public RelayCommand FetchAlertsCommand { get; }

    public RelayCommand FetchUsageCommand { get; }

    public RelayCommand FetchBandwidthCommand { get; }

    public RelayCommand SaveBandwidthCommand { get; }

    public RelayCommand SaveUsageCommand { get; }

    public RelayCommand SaveSitesCommand { get; }
    public RelayCommand SaveDhcpCommand { get; }
    public RelayCommand SaveAlertsCommand { get; }

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

    /// <summary>利用率で見るか、通信量で見るか。</summary>
    public MerakiUsageKind SelectedUsageKind
    {
        get => _selectedUsageKind;
        set { if (value is not null) SetProperty(ref _selectedUsageKind, value); }
    }

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
            FetchSitesCommand.RaiseCanExecuteChanged();
            FetchDhcpCommand.RaiseCanExecuteChanged();
            FetchAlertsCommand.RaiseCanExecuteChanged();
            FetchUsageCommand.RaiseCanExecuteChanged();
            FetchBandwidthCommand.RaiseCanExecuteChanged();
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

    /// <summary>拠点の数だけ呼ぶので、多すぎる組織では途中で止める。</summary>
    private const int MaxSites = 40;

    /// <summary>
    /// 拠点ごとの内訳。<b>拠点の数だけ呼び出しが増える</b>ので、
    /// 取れたところまでで打ち切り、打ち切ったことは画面に出す。
    ///
    /// ついでに機器一覧の LAN IP へ、その拠点のセグメント（クライアントが居るものだけ）を足す。
    /// </summary>
    private async Task FetchSitesAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            CancellationToken token = _cts.Token;

            // 期間は取得の途中で変えられても困るので、始めに固定する
            MerakiTimespan timespan = SelectedTimespan;

            var sites = new List<MerakiSiteRow>();
            var segments = new Dictionary<string, string>(StringComparer.Ordinal);

            MerakiNetworkRow[] targets = [.. NetworkRows.Take(MaxSites)];

            for (int i = 0; i < targets.Length; i++)
            {
                MerakiNetworkRow network = targets[i];

                Status = $"拠点を数えています… {i + 1}/{targets.Length}（{network.Name}）";

                IReadOnlyList<MerakiClientRow> clients = MerakiCatalog.ParseClients(
                    await _dashboard.ClientsAsync(ApiKey, network.Id, timespan.Seconds, token));

                // スタティックルートは MX のある拠点にしか無い。無ければ 404 になるので、
                // そこで取得全体を止めない
                IReadOnlyList<string> routes = [];

                try
                {
                    routes = MerakiCatalog.ParseStaticRouteSubnets(
                        await _dashboard.StaticRoutesAsync(ApiKey, network.Id, token));
                }
                catch (MerakiApiException)
                {
                    // その拠点に MX が無い、というだけ
                }

                IReadOnlyList<string> used = MerakiCatalog.SegmentsWithClients(
                    routes, clients.Select(c => c.Ip));

                if (used.Count > 0) segments[network.Name] = string.Join(" / ", used);

                string note = routes.Count > used.Count
                    ? $"クライアントの居ないセグメント {routes.Count - used.Count} 件は省略"
                    : "";

                sites.Add(MerakiCatalog.SiteRow(network, clients.Count, used, note));
            }

            Replace(SiteRows, sites);

            // 機器一覧の LAN IP にセグメントを足す（足すのは MX だけ）
            Replace(DeviceRows, MerakiCatalog.WithSegments(DeviceRows, segments));

            Status = $"拠点 {SiteRows.Count} 件を数えました（{timespan.Name}・{DateTime.Now:HH:mm:ss} 時点）。";

            if (NetworkRows.Count > targets.Length)
                Notice = $"⚠ 拠点が多いため {targets.Length} 件で打ち切りました（全 {NetworkRows.Count} 件）。";
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
            CrashLog.Write(ex, "MerakiViewModel.FetchSitesAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>MX ごとの DHCP 払い出し状況。機器一覧から MX を拾って回る。</summary>
    private async Task FetchDhcpAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            CancellationToken token = _cts.Token;

            MerakiDeviceRow[] appliances =
            [
                .. DeviceRows.Where(d => d.Model.StartsWith("MX", StringComparison.OrdinalIgnoreCase)
                                         && d.Serial.Length > 0).Take(MaxSites),
            ];

            var rows = new List<MerakiDhcpRow>();

            for (int i = 0; i < appliances.Length; i++)
            {
                MerakiDeviceRow device = appliances[i];

                Status = $"DHCP を確認しています… {i + 1}/{appliances.Length}（{device.Name}）";

                try
                {
                    rows.AddRange(MerakiCatalog.ParseDhcp(
                        await _dashboard.DhcpSubnetsAsync(ApiKey, device.Serial, token),
                        device.Network,
                        device.Name.Length > 0 ? device.Name : device.Serial));
                }
                catch (MerakiApiException)
                {
                    // DHCP を持たせていない MX もある。その 1 台で全体を止めない
                }
            }

            Replace(DhcpRows, rows);

            Status = appliances.Length == 0
                ? "MX が見つかりませんでした（先に「取得」を押してください）。"
                : $"DHCP {DhcpRows.Count} 件（{DateTime.Now:HH:mm:ss} 時点）。";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchDhcpAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>組織のアラート。版によっては持っていないので、その旨を画面に出す。</summary>
    private async Task FetchAlertsAsync()
    {
        if (SelectedOrganization is not { } organization) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            Status = "アラートを取得しています…";

            Replace(AlertRows, MerakiCatalog.ParseAlerts(
                await _dashboard.AlertsAsync(ApiKey, organization.Id, _cts.Token)));

            Status = $"アラート {AlertRows.Count} 件（{DateTime.Now:HH:mm:ss} 時点）。";
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message + "（この組織ではアラートの一覧を持っていないことがあります）";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchAlertsAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>
    /// 機器の使われ具合。<b>棒の長さは一番大きいものを基準にした比率</b>で持つので、
    /// 単位（% とバイト）が違っても同じ見た目で読める。
    /// </summary>
    private async Task FetchUsageAsync()
    {
        if (SelectedOrganization is not { } organization) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            bool utilization = SelectedUsageKind.IsUtilization;

            Status = $"{SelectedUsageKind.Name}を取得しています…";

            IReadOnlyList<string> pages = utilization
                ? await _dashboard.TopAppliancesAsync(ApiKey, organization.Id, SelectedTimespan.Seconds, _cts.Token)
                : await _dashboard.TopDevicesAsync(ApiKey, organization.Id, SelectedTimespan.Seconds, _cts.Token);

            _usageRows = utilization ? MerakiCatalog.ParseUtilization(pages) : MerakiCatalog.ParseUsage(pages);

            double max = _usageRows.Count > 0 ? _usageRows.Max(r => r.Value) : 0;

            UsageBars.Clear();

            foreach (MerakiUtilizationRow row in _usageRows.OrderByDescending(r => r.Value))
                UsageBars.Add(new MerakiBarViewModel(row, max));

            Status = _usageRows.Count == 0
                ? $"{SelectedUsageKind.Name}は取れませんでした（この組織では持っていないことがあります）。"
                : $"{SelectedUsageKind.Name} {_usageRows.Count} 件（{SelectedTimespan.Name}・{DateTime.Now:HH:mm:ss} 時点）。";
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
            CrashLog.Write(ex, "MerakiViewModel.FetchUsageAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>
    /// 拠点ごとの帯域。応答は期間内の合計バイト数なので、Core 側で毎秒に直してもらう。
    /// <b>回線ごとに 1 行</b>（まとめるとどちらの回線が使われているか見えなくなる）。
    /// </summary>
    private async Task FetchBandwidthAsync()
    {
        if (SelectedOrganization is not { } organization) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            Status = "拠点ごとの帯域を取得しています…";

            _bandwidthRows = MerakiCatalog.ParseBandwidth(
                await _dashboard.UplinkUsageAsync(
                    ApiKey, organization.Id, SelectedTimespan.Seconds, _cts.Token),
                SelectedTimespan.Seconds);

            double max = _bandwidthRows.Count > 0 ? _bandwidthRows.Max(r => r.BytesPerSecond) : 0;

            BandwidthBars.Clear();

            foreach (MerakiBandwidthRow row in _bandwidthRows.OrderByDescending(r => r.BytesPerSecond))
            {
                BandwidthBars.Add(new MerakiBarViewModel(
                    device: row.Network,
                    model: row.Uplink,
                    network: $"↑ {row.SentRate} ／ ↓ {row.ReceivedRate}",
                    valueText: row.Rate,
                    value: row.BytesPerSecond,
                    max: max));
            }

            Status = _bandwidthRows.Count == 0
                ? "帯域の情報は取れませんでした（MX のある組織でだけ出ます）。"
                : $"拠点の帯域 {_bandwidthRows.Count} 回線（{SelectedTimespan.Name}の平均・{DateTime.Now:HH:mm:ss} 時点）。";
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
            CrashLog.Write(ex, "MerakiViewModel.FetchBandwidthAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
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
        SiteRows.Clear();
        DhcpRows.Clear();
        AlertRows.Clear();
        UsageBars.Clear();
        BandwidthBars.Clear();
        _usageRows = [];
        _bandwidthRows = [];

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
        SaveSitesCommand.RaiseCanExecuteChanged();
        SaveDhcpCommand.RaiseCanExecuteChanged();
        SaveAlertsCommand.RaiseCanExecuteChanged();
        SaveUsageCommand.RaiseCanExecuteChanged();
        SaveBandwidthCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _dashboard.Dispose();
    }
}
