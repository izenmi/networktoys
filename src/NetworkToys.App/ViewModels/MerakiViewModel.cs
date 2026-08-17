using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Cloud;
using NetworkToys.Core.Design;
using NetworkToys.Core.Verify;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>組織の選択肢。</summary>
public sealed record MerakiOrganizationItem(string Id, string Name);

/// <summary>クライアント一覧をどこまでさかのぼるか。</summary>
public sealed record MerakiTimespan(string Name, int Seconds);

/// <summary>
/// 帯域の棒 1 本。<b>幅は星（比率）で持つ</b> — 画面幅を測らずに済み、
/// 窓を広げてもそのまま伸びる（Wi-Fi のチャンネル棒と同じ手）。
/// </summary>
public sealed class MerakiBarViewModel
{
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
    private bool _allNetworks;
    private IReadOnlyList<double> _usageSeries = [];
    private double _usageMaximum = 100;
    private string _usageUnit = "%";
    private string _usageSummary = "—";
    private IReadOnlyList<MerakiBandwidthRow> _bandwidthRows = [];
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public MerakiViewModel()
    {
        _selectedTimespan = Timespans[1];

        FetchCommand = new RelayCommand(() => _ = FetchAsync(), () => !IsBusy && ApiKey.Length > 0);
        FetchClientsCommand = new RelayCommand(
            () => _ = FetchClientsAsync(),
            () => !IsBusy && ApiKey.Length > 0 && (AllNetworks || SelectedNetwork is not null));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);

        FetchInstallCheckCommand = new RelayCommand(
            () => _ = FetchInstallCheckAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedNetwork is not null && DeviceRows.Count > 0);
        SaveInstallCheckCommand = new RelayCommand(
            () => Save("check", MerakiInstallCheck.ToCsv([.. CheckRows])), () => CheckRows.Count > 0);
        MarkCheckPassCommand = new RelayCommand<MerakiCheckRow>(row => MarkCheck(row, CheckVerdict.Pass));
        MarkCheckFailCommand = new RelayCommand<MerakiCheckRow>(row => MarkCheck(row, CheckVerdict.Fail));

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
            () => !IsBusy && ApiKey.Length > 0 && SelectedNetwork is not null);
        FetchUtilizationCommand = new RelayCommand(() => _ = FetchUtilizationAsync(),
            () => !IsBusy && ApiKey.Length > 0 && DeviceRows.Count > 0);
        SaveUtilizationCommand = new RelayCommand(
            () => Save("utilization", MerakiCatalog.ToCsv([.. UtilizationRows])),
            () => UtilizationRows.Count > 0);
        FetchBandwidthCommand = new RelayCommand(() => _ = FetchBandwidthAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);
        SaveBandwidthCommand = new RelayCommand(() => Save("bandwidth", MerakiCatalog.ToCsv(_bandwidthRows)),
            () => _bandwidthRows.Count > 0);

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

    /// <summary>導入時確認の判定。</summary>
    public ObservableCollection<MerakiCheckRow> CheckRows { get; } = [];

    /// <summary>拠点ごとの内訳（クライアント数とセグメント）。</summary>
    public ObservableCollection<MerakiSiteRow> SiteRows { get; } = [];

    /// <summary>DHCP の払い出し状況。</summary>
    public ObservableCollection<MerakiDhcpRow> DhcpRows { get; } = [];

    /// <summary>アラート。</summary>
    public ObservableCollection<MerakiAlertRow> AlertRows { get; } = [];

    /// <summary>MX ごとの利用率。</summary>
    public ObservableCollection<MerakiUtilizationRow> UtilizationRows { get; } = [];

    /// <summary>回線の使用量の推移（折れ線に渡す値。古い順）。</summary>
    public IReadOnlyList<double> UsageSeries
    {
        get => _usageSeries;
        private set => SetProperty(ref _usageSeries, value);
    }

    /// <summary>縦軸の上限。その期間の最大に合わせる。</summary>
    public double UsageMaximum
    {
        get => _usageMaximum;
        private set => SetProperty(ref _usageMaximum, value);
    }

    public string UsageUnit
    {
        get => _usageUnit;
        private set => SetProperty(ref _usageUnit, value);
    }

    /// <summary>最大と平均。グラフだけでは数字が読めないので必ず添える。</summary>
    public string UsageSummary
    {
        get => _usageSummary;
        private set => SetProperty(ref _usageSummary, value);
    }

    /// <summary>拠点ごとの帯域（棒）。</summary>
    public ObservableCollection<MerakiBarViewModel> BandwidthBars { get; } = [];

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
    public RelayCommand SaveDevicesCommand { get; }
    public RelayCommand SaveUplinksCommand { get; }
    public RelayCommand SaveClientsCommand { get; }

    /// <summary>選んだ拠点の導入時確認を一通り走らせる。</summary>
    public RelayCommand FetchInstallCheckCommand { get; }

    public RelayCommand SaveInstallCheckCommand { get; }

    /// <summary>目視の項目に人が合否を付ける（API から判定できないもの）。</summary>
    public RelayCommand<MerakiCheckRow> MarkCheckPassCommand { get; }

    public RelayCommand<MerakiCheckRow> MarkCheckFailCommand { get; }

    /// <summary>拠点ごとの内訳を取る。クライアント数の期間は 30 日で固定。</summary>
    public RelayCommand FetchSitesCommand { get; }

    /// <summary>MX の DHCP 払い出し状況を取る。</summary>
    public RelayCommand FetchDhcpCommand { get; }

    /// <summary>組織のアラートを取る。</summary>
    public RelayCommand FetchAlertsCommand { get; }

    public RelayCommand FetchUsageCommand { get; }

    /// <summary>MX ごとの利用率を取る（device utilization）。</summary>
    public RelayCommand FetchUtilizationCommand { get; }

    public RelayCommand SaveUtilizationCommand { get; }

    public RelayCommand FetchBandwidthCommand { get; }

    public RelayCommand SaveBandwidthCommand { get; }

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
            FetchInstallCheckCommand.RaiseCanExecuteChanged();
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
            if (!SetProperty(ref _selectedNetwork, value)) return;

            FetchClientsCommand.RaiseCanExecuteChanged();
            FetchInstallCheckCommand.RaiseCanExecuteChanged();
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

    /// <summary>
    /// クライアントを全拠点から取るか。<b>拠点の数だけ呼び出しが増える</b>ので、
    /// 既定は選んだ拠点だけ。
    /// </summary>
    public bool AllNetworks
    {
        get => _allNetworks;
        set { if (SetProperty(ref _allNetworks, value)) FetchClientsCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>利用率で見るか、通信量で見るか。</summary>
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
            FetchInstallCheckCommand.RaiseCanExecuteChanged();
            FetchSitesCommand.RaiseCanExecuteChanged();
            FetchDhcpCommand.RaiseCanExecuteChanged();
            FetchAlertsCommand.RaiseCanExecuteChanged();
            FetchUsageCommand.RaiseCanExecuteChanged();
            FetchBandwidthCommand.RaiseCanExecuteChanged();
            FetchUtilizationCommand.RaiseCanExecuteChanged();
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
        if (!AllNetworks && SelectedNetwork is null) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            MerakiTimespan timespan = SelectedTimespan;

            // 全拠点は拠点の数だけ呼び出しが増えるので、拠点の一覧と同じ上限で打ち切る
            MerakiNetworkRow[] targets = AllNetworks
                ? [.. NetworkRows.Take(MaxSites)]
                : [SelectedNetwork!];

            var clients = new List<MerakiClientRow>();

            for (int i = 0; i < targets.Length; i++)
            {
                MerakiNetworkRow network = targets[i];

                Status = targets.Length == 1
                    ? $"「{network.Name}」のクライアントを取得しています…"
                    : $"クライアントを取得しています… {i + 1}/{targets.Length}（{network.Name}）";

                clients.AddRange(MerakiCatalog.ParseClients(
                    await _dashboard.ClientsAsync(ApiKey, network.Id, timespan.Seconds, _cts.Token),
                    network.Name));
            }

            Replace(ClientRows, clients);

            Status = targets.Length == 1
                ? $"「{targets[0].Name}」で {ClientRows.Count} 台（直近 {timespan.Name}）。"
                : $"{targets.Length} 拠点で {ClientRows.Count} 台（直近 {timespan.Name}）。";

            if (_dashboard.WasTruncated)
                Notice = "⚠ 台数が多いため途中までしか取得できていません。期間を短くして試してください。";

            if (AllNetworks && NetworkRows.Count > targets.Length)
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

    /// <summary>ロスと遅延をさかのぼる長さ。<b>この API は 5 分までしか受けない。</b></summary>
    private const int QualitySeconds = 300;

    /// <summary>イベントを何件見るか。多くしても導入日には古すぎて役に立たない。</summary>
    private const int EventCount = 100;

    /// <summary>導入時確認で走らせる項目の数（画面に「3/9」と出すため）。</summary>
    private const int CheckSteps = 9;

    /// <summary>
    /// 選んだ拠点の導入時確認。<b>項目ごとに失敗を捕まえて次へ進む</b> —
    /// 1 本が取れないだけで残りが分からなくなる方が、現場では困る。
    /// 取れなかった項目は「確認できず」として理由ごと残す（合格に丸めない）。
    /// </summary>
    private async Task FetchInstallCheckAsync()
    {
        if (SelectedNetwork is not { } network) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        var rows = new List<MerakiCheckRow>();

        try
        {
            CancellationToken token = _cts.Token;
            MerakiTimespan timespan = SelectedTimespan;
            string organizationId = SelectedOrganization?.Id ?? "";

            MerakiDeviceRow[] devices = [.. DeviceRows.Where(d => d.Network == network.Name)];
            MerakiDeviceRow[] appliances = [.. devices.Where(d => IsModel(d, "MX"))];
            MerakiDeviceRow[] switches = [.. devices.Where(d => IsModel(d, "MS"))];

            // 1. 機器が上がっているか（取得済みの一覧だけで判る）
            Step(1, "機器の稼働", network);
            rows.Add(MerakiInstallCheck.Devices(devices));

            // 2. 有効にしてある WAN が全部リンクアップしているか
            Step(2, "インターネット回線", network);

            if (appliances.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.WanName, network.Name, "この拠点に MX がありません"));
            }

            foreach (MerakiDeviceRow appliance in appliances)
            {
                MerakiUplinkRow[] uplinks =
                [
                    .. UplinkRows.Where(u => string.Equals(
                        u.Serial, appliance.Serial, StringComparison.OrdinalIgnoreCase)),
                ];

                IReadOnlyList<string> settings = [];

                try
                {
                    settings = await _dashboard.UplinkSettingsAsync(ApiKey, appliance.Serial, token);
                }
                catch (MerakiApiException)
                {
                    // 設定が取れないぶんは「状態だけで見た」注意になる
                }

                rows.Add(MerakiInstallCheck.Wan(NameOf(appliance), settings, uplinks));
            }

            // 3. 回線のロスと遅延（実測値）
            Step(3, "回線の品質", network);

            if (organizationId.Length == 0 || appliances.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.QualityName,
                    network.Name,
                    appliances.Length == 0 ? "この拠点に MX がありません" : "組織が選ばれていません"));
            }
            else
            {
                try
                {
                    rows.AddRange(MerakiInstallCheck.Quality(
                        await _dashboard.LossAndLatencyAsync(ApiKey, organizationId, QualitySeconds, token),
                        appliances));
                }
                catch (MerakiApiException ex)
                {
                    rows.Add(MerakiInstallCheck.Unavailable(
                        MerakiInstallCheck.QualityName, network.Name, ex.Message));
                }
            }

            // 4. ポートの速度と全二重（MX は API に無いので目視に回す）
            Step(4, "ポートの速度・全二重", network);

            foreach (MerakiDeviceRow device in switches)
            {
                try
                {
                    rows.Add(MerakiInstallCheck.SwitchPorts(
                        NameOf(device),
                        await _dashboard.SwitchPortStatusesAsync(ApiKey, device.Serial, token)));
                }
                catch (MerakiApiException ex)
                {
                    rows.Add(MerakiInstallCheck.Unavailable(
                        MerakiInstallCheck.PortsName, NameOf(device), ex.Message));
                }
            }

            foreach (MerakiDeviceRow appliance in appliances)
                rows.Add(MerakiInstallCheck.AppliancePortsByPerson(NameOf(appliance), appliance.Model));

            if (switches.Length == 0 && appliances.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.PortsName, network.Name, "この拠点にスイッチも MX もありません"));
            }

            // 5. トンネルが張れているか
            Step(5, "VPN", network);

            if (organizationId.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.VpnName, network.Name, "組織が選ばれていません"));
            }
            else
            {
                try
                {
                    rows.AddRange(MerakiInstallCheck.Vpn(
                        await _dashboard.VpnStatusesAsync(ApiKey, organizationId, token), network.Id));
                }
                catch (MerakiApiException ex)
                {
                    rows.Add(MerakiInstallCheck.Unavailable(
                        MerakiInstallCheck.VpnName, network.Name, ex.Message));
                }
            }

            // 6. アドレスが配れているか
            Step(6, "DHCP", network);

            var subnets = new List<MerakiDhcpRow>();

            foreach (MerakiDeviceRow appliance in appliances)
            {
                try
                {
                    subnets.AddRange(MerakiCatalog.ParseDhcp(
                        await _dashboard.DhcpSubnetsAsync(ApiKey, appliance.Serial, token),
                        network.Name,
                        NameOf(appliance)));
                }
                catch (MerakiApiException)
                {
                    // DHCP を持たせていない MX もある。その 1 台で全体を止めない
                }
            }

            rows.AddRange(MerakiInstallCheck.Dhcp(subnets));

            // 7. Teams をローカルブレイクアウトさせる設定が入っているか
            Step(7, "Teams の LBO", network);

            try
            {
                rows.Add(MerakiInstallCheck.TeamsBreakout(
                    await _dashboard.VpnExclusionsAsync(ApiKey, network.Id, token)));
            }
            catch (MerakiApiException ex)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.TeamsName, network.Name, ex.Message));
            }

            // 8. 気になるイベントが出ていないか
            Step(8, "イベントログ", network);

            try
            {
                rows.Add(MerakiInstallCheck.Events(
                    await _dashboard.EventsAsync(ApiKey, network.Id, EventCount, token)));
            }
            catch (MerakiApiException ex)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.EventsName, network.Name, ex.Message));
            }

            // 9. 端末が実際に繋がっているか
            Step(9, "クライアント", network);

            try
            {
                rows.Add(MerakiInstallCheck.Clients(
                    MerakiCatalog.ParseClients(
                        await _dashboard.ClientsAsync(ApiKey, network.Id, timespan.Seconds, token),
                        network.Name),
                    timespan.Name));
            }
            catch (MerakiApiException ex)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.ClientsName, network.Name, ex.Message));
            }

            Status = $"{network.Name}（{DateTime.Now:HH:mm:ss} 時点）："
                   + MerakiInstallCheck.Summarize(rows);
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。ここまでの判定だけ残しています。";
        }
        catch (Exception ex)
        {
            Status = $"導入時確認に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchInstallCheckAsync");
        }
        finally
        {
            // 中断しても、済んだところまでは見せる
            Replace(CheckRows, rows);

            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>いま何をしているかを出す。項目が多いので進み具合が見えないと待てない。</summary>
    private void Step(int step, string what, MerakiNetworkRow network)
        => Status = $"「{network.Name}」を確認しています… {step}/{CheckSteps}（{what}）";

    /// <summary>
    /// 目視の項目に人が合否を付ける。<b>行を差し替える</b>ので CSV にもそのまま載る
    /// （試験タブと同じ扱い）。
    /// </summary>
    private void MarkCheck(MerakiCheckRow? row, CheckVerdict verdict)
    {
        if (row is null) return;

        int at = CheckRows.IndexOf(row);
        if (at < 0) return;

        string mark = verdict == CheckVerdict.Pass ? "目視で確認しました" : "目視で問題を確認しました";

        CheckRows[at] = row with { Verdict = verdict, Detail = $"{mark}（{row.Detail}）" };

        Status = MerakiInstallCheck.Summarize([.. CheckRows]);
    }

    private static bool IsModel(MerakiDeviceRow device, string prefix)
        => device.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && device.Serial.Length > 0;

    /// <summary>名前が無い機器はシリアルで呼ぶ（空欄だと行が誰のものか分からない）。</summary>
    private static string NameOf(MerakiDeviceRow device)
        => device.Name.Length > 0 ? device.Name : device.Serial;

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
    /// 回線（WAN）の使用量の推移。<b>拠点を 1 つ選んで、その期間の中を見る</b>。
    /// 区切りの細かさは期間から決める（60 点くらいに収まるように）。
    /// </summary>
    private async Task FetchUsageAsync()
    {
        if (SelectedNetwork is not { } network)
        {
            Status = "拠点を選んでください（先に「取得」を押すと一覧に入ります）。";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            MerakiTimespan timespan = SelectedTimespan;

            // 点が多すぎると線が潰れる。少なすぎると変化が見えない
            int resolution = Math.Max(timespan.Seconds / 60, 60);

            Status = "回線の使用量の推移を取得しています…";

            MerakiCatalog.MerakiSeries series = MerakiCatalog.ParseUplinkHistory(
                await _dashboard.UplinkHistoryAsync(
                    ApiKey, network.Id, timespan.Seconds, resolution, _cts.Token),
                label: network.Name);

            UsageSeries = series.Values;
            UsageMaximum = series.Maximum;
            UsageUnit = series.Unit;
            UsageSummary = series.Summary;

            Status = series.Values.Count == 0
                ? "回線の使用量は取れませんでした（この拠点では持っていないことがあります）。"
                : $"{network.Name}：回線の使用量 {series.Values.Count} 点（{timespan.Name}・{DateTime.Now:HH:mm:ss} 時点）。";
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
    /// MX ごとの利用率（device utilization）。
    ///
    /// <b>答えるのはプライマリの MX だけ</b>で、それ以外は 400、まだデータが無ければ 204 が返る。
    /// どちらも「取れなかった」で 1 行として出す — <b>一覧から消すと「その機器は無い」と誤読される</b>。
    /// </summary>
    private async Task FetchUtilizationAsync()
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

            var rows = new List<MerakiUtilizationRow>();

            for (int i = 0; i < appliances.Length; i++)
            {
                MerakiDeviceRow device = appliances[i];
                string name = device.Name.Length > 0 ? device.Name : device.Serial;

                Status = $"利用率を確認しています… {i + 1}/{appliances.Length}（{name}）";

                try
                {
                    rows.Add(MerakiCatalog.ParseAppliancePerformance(
                        await _dashboard.AppliancePerformanceAsync(ApiKey, device.Serial, token),
                        device.Network, name, device.Model, device.Serial));
                }
                catch (MerakiApiException ex)
                {
                    // 1 台で全体を止めない。答えられない理由は行に残す
                    rows.Add(new MerakiUtilizationRow(
                        device.Network, name, device.Model, device.Serial,
                        "—", SeverityKind.Muted, ex.Message));
                }
            }

            Replace(UtilizationRows, rows);

            Status = appliances.Length == 0
                ? "MX が見つかりませんでした（先に「機器」を取得してください）。"
                : $"MX {UtilizationRows.Count} 台の利用率（{DateTime.Now:HH:mm:ss} 時点）。";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchUtilizationAsync");
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
        CheckRows.Clear();
        SiteRows.Clear();
        DhcpRows.Clear();
        AlertRows.Clear();
        UtilizationRows.Clear();
        BandwidthBars.Clear();
        UsageSeries = [];
        UsageSummary = "—";
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
        SaveDevicesCommand.RaiseCanExecuteChanged();
        SaveUplinksCommand.RaiseCanExecuteChanged();
        SaveClientsCommand.RaiseCanExecuteChanged();
        SaveInstallCheckCommand.RaiseCanExecuteChanged();
        SaveSitesCommand.RaiseCanExecuteChanged();
        SaveDhcpCommand.RaiseCanExecuteChanged();
        SaveAlertsCommand.RaiseCanExecuteChanged();
        SaveBandwidthCommand.RaiseCanExecuteChanged();
        SaveUtilizationCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _dashboard.Dispose();
    }
}
