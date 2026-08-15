using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Metrics;
using PingWatcher.Core.Reporting;

namespace PingWatcher.App.ViewModels;

/// <summary>周辺で見えているアクセスポイント 1 台。</summary>
public sealed class AccessPointViewModel
{
    internal AccessPointViewModel(WifiAccessPoint ap)
    {
        Ssid = string.IsNullOrEmpty(ap.Ssid) ? "（ステルス）" : ap.Ssid;
        Bssid = ap.Bssid;
        Vendor = ap.Vendor ?? string.Empty;
        Rssi = $"{ap.Rssi} dBm";
        Quality = $"{ap.LinkQuality}%";
        Channel = ap.Channel.ToString();
        Band = ap.Band > 0 ? $"{ap.Band:0.#} GHz" : string.Empty;
        IsConnected = ap.IsConnected;

        // 電波の強さを 0〜1 に均す。-30dBm で満杯、-90dBm で空とする
        Strength = Math.Clamp((ap.Rssi + 90) / 60.0, 0, 1);

        // 見出しクリックの並べ替え用。表示は整形済み文字列だが、ソートは数値で行う
        RssiValue = ap.Rssi;
        ChannelValue = ap.Channel;
        BandValue = ap.Band;
    }

    internal int RssiValue { get; }
    internal int ChannelValue { get; }
    internal float BandValue { get; }

    public string Ssid { get; }
    public string Bssid { get; }
    public string Vendor { get; }
    public string Rssi { get; }
    public string Quality { get; }
    public string Channel { get; }
    public string Band { get; }
    public bool IsConnected { get; }
    public double Strength { get; }
}

/// <summary>チャンネル混雑ビューの 1 本。</summary>
public sealed class ChannelBarViewModel
{
    public ChannelBarViewModel(int channel, int count, bool isMine, int maxCount)
    {
        Channel = channel.ToString();
        Count = count;
        IsMine = isMine;

        // 一番混んでいるチャンネルを 1 として高さを決める
        Height = maxCount <= 0 ? 0 : Math.Max(3, count * 60.0 / maxCount);
        Label = count > 0 ? count.ToString() : string.Empty;
    }

    public string Channel { get; }
    public int Count { get; }
    public bool IsMine { get; }
    public double Height { get; }
    public string Label { get; }
}

/// <summary>
/// 無線 LAN 画面。
///
/// <b>この画面を開くまで無線 API に触らない。</b>Windows 11 24H2 以降は
/// スキャン系 API に位置情報の同意が要るため、起動時に呼ぶと脈絡のない
/// タイミングで許可を求めることになる。
/// </summary>
public sealed class WifiViewModel : ObservableObject
{
    /// <summary>
    /// 再スキャンの最短間隔。Windows の WLAN サービス自体が既定 60 秒間隔でしか
    /// スキャンしないので、これより短くしても新しい結果は返らない。
    /// </summary>
    private static readonly TimeSpan MinimumScanInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RssiInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _rssiTimer;
    private readonly RingBuffer<double> _rssiHistory = new(120);

    private bool _hasLoaded;
    private bool _isBusy;
    private bool _isDenied;
    private string _status = "「更新」を押すと周辺のアクセスポイントを調べます。";
    private string _connectionSummary = "—";
    private string _ssid = "—";
    private string _bssid = "—";
    private string _vendor = "—";
    private string _signal = "—";
    private string _channel = "—";
    private string _phyType = "—";
    private string _security = "—";
    private string _linkRate = "—";
    private string _adapter = "—";
    private DateTime _lastScan = DateTime.MinValue;
    private CancellationTokenSource? _cts;
    private bool _rssiTickBusy;

    public WifiViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true), () => !IsBusy);
        SaveCommand = new RelayCommand(SaveSnapshot);
        OpenLocationSettingsCommand = new RelayCommand(OpenLocationSettings);

        _rssiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RssiInterval };
        _rssiTimer.Tick += OnRssiTick;
    }

    public ObservableCollection<AccessPointViewModel> AccessPoints { get; } = [];

    public ObservableCollection<ChannelBarViewModel> Channels24 { get; } = [];

    public ObservableCollection<ChannelBarViewModel> Channels5 { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenLocationSettingsCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>位置情報が許可されていない状態か。案内の表示切り替えに使う。</summary>
    public bool IsDenied
    {
        get => _isDenied;
        private set => SetProperty(ref _isDenied, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string ConnectionSummary { get => _connectionSummary; private set => SetProperty(ref _connectionSummary, value); }
    public string Ssid { get => _ssid; private set => SetProperty(ref _ssid, value); }
    public string Bssid { get => _bssid; private set => SetProperty(ref _bssid, value); }
    public string Vendor { get => _vendor; private set => SetProperty(ref _vendor, value); }
    public string Signal { get => _signal; private set => SetProperty(ref _signal, value); }
    public string Channel { get => _channel; private set => SetProperty(ref _channel, value); }
    public string PhyType { get => _phyType; private set => SetProperty(ref _phyType, value); }
    public string Security { get => _security; private set => SetProperty(ref _security, value); }
    public string LinkRate { get => _linkRate; private set => SetProperty(ref _linkRate, value); }
    public string Adapter { get => _adapter; private set => SetProperty(ref _adapter, value); }

    /// <summary>RSSI の推移。電波の弱い場所を歩いて探す用途。</summary>
    public event EventHandler? RssiHistoryChanged;

    public int CopyRssiHistory(Span<double> destination) => _rssiHistory.CopyLatestTo(destination, destination.Length);

    /// <summary>画面が最初に開かれたときに呼ぶ。ここで初めて無線 API に触れる。</summary>
    public void OnActivated()
    {
        _rssiTimer.Start();

        if (_hasLoaded) return;
        _hasLoaded = true;
        _ = RefreshAsync(force: false);
    }

    public void OnDeactivated() => _rssiTimer.Stop();

    private async Task RefreshAsync(bool force)
    {
        if (IsBusy) return;

        bool tooSoon = DateTime.Now - _lastScan < MinimumScanInterval;
        if (force && tooSoon)
        {
            // 短い間隔で叩いても OS は古い結果を返すだけ。無駄にスキャンさせない
            Status = $"直前のスキャンから {MinimumScanInterval.TotalSeconds:0} 秒は結果が変わりません。一覧だけ読み直しました。";
            Apply(WifiService.Collect());
            return;
        }

        IsBusy = true;
        Status = "周辺のアクセスポイントを調べています…（スキャン中は通信が一瞬途切れることがあります）";

        _cts = new CancellationTokenSource();

        try
        {
            WifiSnapshot snapshot = await WifiService.ScanAsync(_cts.Token);
            _lastScan = DateTime.Now;
            Apply(snapshot);
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"無線 LAN の情報を取得できませんでした: {ex.Message}";
            CrashLog.Write(ex, "WifiViewModel.RefreshAsync");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void Apply(WifiSnapshot snapshot)
    {
        IsDenied = snapshot.Failure == WifiFailure.LocationDenied;

        if (IsDenied)
        {
            Status = "無線 LAN の情報を見るには位置情報の許可が必要です（Windows 11 24H2 以降の仕様）。";
            return;
        }

        if (snapshot.Failure != WifiFailure.None)
        {
            Status = snapshot.Message ?? "無線 LAN の情報を取得できませんでした。";
            return;
        }

        AccessPoints.Clear();
        foreach (AccessPointViewModel ap in SortedAccessPoints(snapshot.AccessPoints.Select(ap => new AccessPointViewModel(ap))))
            AccessPoints.Add(ap);

        BuildChannelChart(snapshot);

        if (snapshot.Connection is { } connection)
        {
            Ssid = connection.Ssid.Length > 0 ? connection.Ssid : "—";
            Bssid = connection.Bssid.Length > 0 ? connection.Bssid : "—";
            Vendor = connection.Vendor ?? "—";
            Signal = connection.Rssi is { } rssi
                ? $"{connection.SignalQuality}%（{rssi} dBm）"
                : $"{connection.SignalQuality}%";
            Channel = connection.Channel > 0
                ? (connection.Band > 0 ? $"{connection.Channel}（{connection.Band:0.#} GHz）" : connection.Channel.ToString())
                : "—";
            PhyType = connection.PhyType;
            Security = $"{connection.Authentication} / {connection.Encryption}";
            LinkRate = connection.RxRateKbps > 0 || connection.TxRateKbps > 0
                ? $"受信 {connection.RxRateKbps / 1000.0:0} / 送信 {connection.TxRateKbps / 1000.0:0} Mbps"
                : "—";
            Adapter = connection.InterfaceName;
            ConnectionSummary = $"{Ssid} に接続中";
        }
        else
        {
            ConnectionSummary = "無線 LAN に接続していません";
            Ssid = Bssid = Vendor = Signal = Channel = PhyType = Security = LinkRate = Adapter = "—";
        }

        Status = snapshot.Message
                 ?? $"{snapshot.AccessPoints.Count} 台のアクセスポイントが見えています（{DateTime.Now:HH:mm:ss} 時点）。";
    }

    /// <summary>
    /// チャンネルごとの AP 数を数える。
    /// 2.4GHz は隣接チャンネルが重なるが、チャンネル幅は BSS 情報から分からないので
    /// ここでは実測できる「そのチャンネルを使っている AP の数」だけを示す。
    /// </summary>
    private void BuildChannelChart(WifiSnapshot snapshot)
    {
        Channels24.Clear();
        Channels5.Clear();

        var counts24 = new Dictionary<int, int>();
        var counts5 = new Dictionary<int, int>();
        int myChannel = snapshot.Connection?.Channel ?? 0;

        foreach (WifiAccessPoint ap in snapshot.AccessPoints)
        {
            if (ap.Channel <= 0) continue;

            // 帯域はチャンネル番号で判定する。Band は取れないことがあり(0)、
            // それを 2.4GHz に混ぜると 5GHz の AP が誤った帯に立ってしまう
            Dictionary<int, int> target = ap.Channel <= 14 ? counts24 : counts5;
            target[ap.Channel] = target.GetValueOrDefault(ap.Channel) + 1;
        }

        // 14ch まで並べる(国内の 11b 専用 AP が使う。13 までだと一覧から消える)
        AddBars(Channels24, Enumerable.Range(1, 14), counts24, myChannel);

        // 5GHz は使われうるチャンネルだけを並べる（全部出すと横に長くなりすぎる）
        int[] used5 = [.. counts5.Keys.Order()];
        AddBars(Channels5, used5, counts5, myChannel);
    }

    private static void AddBars(
        ObservableCollection<ChannelBarViewModel> target,
        IEnumerable<int> channels,
        Dictionary<int, int> counts,
        int myChannel)
    {
        int max = counts.Count > 0 ? counts.Values.Max() : 0;

        foreach (int channel in channels)
            target.Add(new ChannelBarViewModel(channel, counts.GetValueOrDefault(channel), channel == myChannel, max));
    }

    private async void OnRssiTick(object? sender, EventArgs e)
    {
        // WLAN サービスへの問い合わせは同期 API で、アダプタの状態遷移中は
        // 数百 ms 返らないことがある。UI スレッドで待つと毎秒引っかかるので
        // ThreadPool で取り、詰まっている間は次のティックを重ねない
        if (_rssiTickBusy) return;
        _rssiTickBusy = true;

        try
        {
            int? rssi = await Task.Run(WifiService.GetRssi);
            if (rssi is not { } value) return;

            _rssiHistory.Add(value);
            RssiHistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _rssiTickBusy = false;
        }
    }

    private void OpenLocationSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:privacy-location",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status = $"設定を開けませんでした: {ex.Message}";
        }
    }

    /// <summary>結果を起動時の状態へ戻す。走っていれば止める。</summary>
    public void Reset()
    {
        _cts?.Cancel();

        AccessPoints.Clear();
        Channels24.Clear();
        Channels5.Clear();

        _rssiHistory.Clear();
        _lastScan = DateTime.MinValue;
        RssiHistoryChanged?.Invoke(this, EventArgs.Empty);

        ConnectionSummary = "—";
        Ssid = Bssid = Vendor = Signal = Channel = PhyType = Security = LinkRate = Adapter = "—";
        Status = "「更新」を押すと周辺のアクセスポイントを調べます。";
    }

    /// <summary>
    /// 記録に載せる無線の情報。まだ調べていなければ null。
    ///
    /// <b>ここから勝手にスキャンはしない。</b>Windows 11 24H2 以降はスキャンに
    /// 位置情報の同意が要るため、記録を書き出した拍子に許可を求めることになる。
    /// 無線画面を開いたときに取った内容を、そのまま載せる。
    /// </summary>
    public IReadOnlyList<(string Label, string Value)>? DescribeForReport()
    {
        if (_lastScan == DateTime.MinValue && Ssid == "—")
            return null;

        var items = new List<(string, string)>
        {
            ("状態", ConnectionSummary),
            ("SSID", Ssid),
            ("BSSID", Bssid),
            ("メーカー", Vendor),
            ("信号", Signal),
            ("チャンネル", Channel),
            ("規格", PhyType),
            ("暗号化", Security),
            ("リンク速度", LinkRate),
            ("アダプター", Adapter),
        };

        if (_lastScan != DateTime.MinValue)
        {
            items.Add(("周辺の AP", $"{AccessPoints.Count} 件"));
            items.Add(("取得時刻", _lastScan.ToString("yyyy/MM/dd HH:mm:ss")));
        }

        return items;
    }

    /// <summary>情報が無いときに記録へ書く理由。空欄のまま出すと不親切。</summary>
    public string? ReportNote => DescribeForReport() is null
        ? "無線 LAN の情報は取得していません（「無線」タブを開くと取得します）。"
        : null;

    // 周辺 AP 一覧の見出しクリックの並べ替え。空文字は既定(電波の強い順=取得順)
    private string _apSortColumn = "";
    private bool _apSortDescending;

    /// <summary>一覧の見出し。ソート中の列に ▲/▼ を添える。</summary>
    public string HeaderSsid => ApHeaderLabel("Ssid", "SSID");
    public string HeaderBssid => ApHeaderLabel("Bssid", "BSSID");
    public string HeaderVendor => ApHeaderLabel("Vendor", "機器");
    public string HeaderRssi => ApHeaderLabel("Rssi", "強度");
    public string HeaderChannel => ApHeaderLabel("Channel", "ch");
    public string HeaderBand => ApHeaderLabel("Band", "帯域");

    /// <summary>見出しクリックの並べ替え。昇順 → 降順 → 既定(強い順)と巡る。</summary>
    public void SortAccessPointsBy(string column)
    {
        if (_apSortColumn == column)
        {
            if (!_apSortDescending)
            {
                _apSortDescending = true;
            }
            else
            {
                _apSortColumn = "";
                _apSortDescending = false;
            }
        }
        else
        {
            _apSortColumn = column;
            _apSortDescending = false;
        }

        // 一覧はスキャン(10 秒以上間隔)でしか入れ替わらないので、作り直しで十分
        List<AccessPointViewModel> sorted = SortedAccessPoints([.. AccessPoints]);
        AccessPoints.Clear();
        foreach (AccessPointViewModel ap in sorted)
            AccessPoints.Add(ap);

        OnPropertyChanged(nameof(HeaderSsid));
        OnPropertyChanged(nameof(HeaderBssid));
        OnPropertyChanged(nameof(HeaderVendor));
        OnPropertyChanged(nameof(HeaderRssi));
        OnPropertyChanged(nameof(HeaderChannel));
        OnPropertyChanged(nameof(HeaderBand));
    }

    private string ApHeaderLabel(string column, string title)
        => _apSortColumn == column ? $"{title} {(_apSortDescending ? "▼" : "▲")}" : title;

    private List<AccessPointViewModel> SortedAccessPoints(IEnumerable<AccessPointViewModel> accessPoints)
    {
        var list = new List<AccessPointViewModel>(accessPoints);

        if (_apSortColumn.Length == 0)
            return list;   // 既定 = WifiService が返す電波の強い順

        list.Sort((x, y) =>
        {
            int result = _apSortColumn switch
            {
                "Ssid" => string.Compare(x.Ssid, y.Ssid, StringComparison.OrdinalIgnoreCase),
                "Bssid" => string.CompareOrdinal(x.Bssid, y.Bssid),
                "Vendor" => string.Compare(x.Vendor, y.Vendor, StringComparison.OrdinalIgnoreCase),
                "Rssi" => x.RssiValue.CompareTo(y.RssiValue),
                "Channel" => x.ChannelValue.CompareTo(y.ChannelValue),
                "Band" => x.BandValue.CompareTo(y.BandValue),
                _ => 0,
            };

            if (_apSortDescending)
                result = -result;

            // 同値は強い順で安定させる
            return result != 0 ? result : y.RssiValue.CompareTo(x.RssiValue);
        });

        return list;
    }

    /// <summary>周辺 AP の一覧を記録用の形にする。画面表示と同じ整形済み文字列を渡す。</summary>
    public IReadOnlyList<WirelessAccessPoint> DescribeAccessPointsForReport()
        => [.. AccessPoints.Select(ap => new WirelessAccessPoint(
            ap.Ssid, ap.Bssid, ap.Vendor, ap.Rssi, ap.Quality, ap.Channel, ap.Band, ap.IsConnected))];

    /// <summary>
    /// いま見えている無線の状況（接続情報+周辺 AP）をテキストで保存する。
    /// 現場の電波環境を単体で控えたい場面用。レポート全体は「記録」タブから。
    /// </summary>
    private void SaveSnapshot()
    {
        IReadOnlyList<(string Label, string Value)>? connection = DescribeForReport();
        IReadOnlyList<WirelessAccessPoint> accessPoints = DescribeAccessPointsForReport();

        if (connection is null && accessPoints.Count == 0)
        {
            Status = "保存する情報がまだありません。「更新」で周辺を調べてから保存してください。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"wifi-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExt = "txt",
            Filter = "テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName,
                WifiSnapshotWriter.Render(DateTime.Now, connection, accessPoints),
                Encoding.UTF8);
            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }
}
