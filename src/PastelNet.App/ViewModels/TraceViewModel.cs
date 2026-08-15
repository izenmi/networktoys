using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using PastelNet.App.Mvvm;
using PastelNet.App.Services;

namespace PastelNet.App.ViewModels;

/// <summary>経路の 1 ホップ。逆引きは後から差し込まれる。</summary>
public sealed class TraceHopViewModel : ObservableObject
{
    private string _hostName = string.Empty;

    internal TraceHopViewModel(TraceHop hop)
    {
        Ttl = hop.Ttl;
        Address = hop.Address?.ToString() ?? "* * *";
        Rtt = hop.RttMs is { } rtt ? TargetRowViewModel.FormatMilliseconds(rtt) : "—";
        IsDestination = hop.IsDestination;
        Responded = hop.Address is not null;

        Note = hop.Status switch
        {
            IPStatus.TimedOut => "応答なし",
            IPStatus.Success => "到達",
            IPStatus.TtlExpired => string.Empty,
            IPStatus.DestinationHostUnreachable => "ホスト到達不能",
            IPStatus.DestinationNetworkUnreachable => "ネットワーク到達不能",
            IPStatus.DestinationProhibited => "遮断",
            IPStatus.Unknown => "エラー",
            _ => hop.Status.ToString(),
        };
    }

    public int Ttl { get; }

    public string Address { get; }

    public string Rtt { get; }

    public string Note { get; }

    public bool IsDestination { get; }

    public bool Responded { get; }

    /// <summary>逆引き結果。解決できるまでは空。</summary>
    public string HostName
    {
        get => _hostName;
        internal set => SetProperty(ref _hostName, value);
    }
}

/// <summary>経路が変わったときの記録。</summary>
public sealed class RouteChangeViewModel
{
    public RouteChangeViewModel(DateTime detectedAt, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        Time = detectedAt.ToString("MM/dd HH:mm:ss");

        string[] removed = [.. before.Where(h => h != "*" && !after.Contains(h))];
        string[] added = [.. after.Where(h => h != "*" && !before.Contains(h))];

        var parts = new List<string> { $"{before.Count} → {after.Count} ホップ" };

        if (removed.Length > 0) parts.Add($"消えた: {string.Join(", ", removed)}");
        if (added.Length > 0) parts.Add($"現れた: {string.Join(", ", added)}");

        Summary = string.Join(" ／ ", parts);
    }

    public string Time { get; }

    public string Summary { get; }
}

/// <summary>
/// 経路（traceroute）画面。
/// TTL を並列に投げるので、逐次実行のツールのように 1 ホップずつ待たされない。
/// </summary>
public sealed class TraceViewModel : ObservableObject
{
    private const int MaxHops = 30;
    private const int TimeoutMs = 2000;

    /// <summary>
    /// TTL ごとにこれだけずらして投げる。完全同時だと ICMP のレート制限で
    /// 中間ホップの応答が落ちやすい。
    /// </summary>
    private const int StaggerMs = 25;

    /// <summary>経路の見張り間隔。頻繁に打つと経路上のルータに負担をかける。</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(60);

    private string _host = "8.8.8.8";
    private string _status = string.Empty;
    private string _mtuText = string.Empty;
    private bool _isBusy;
    private bool _isWatching;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _watchTimer;

    // ECMP で経路が揺れるだけのことがあるので、1 回違っただけでは変化とみなさない
    private string[]? _lastPath;
    private string[]? _pendingPath;

    public TraceViewModel()
    {
        TraceCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Host));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        MtuCommand = new RelayCommand(() => _ = RunMtuAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Host));
    }

    public ObservableCollection<TraceHopViewModel> Hops { get; } = [];

    /// <summary>経路が変わった記録。新しいものが上に来る。</summary>
    public ObservableCollection<RouteChangeViewModel> Changes { get; } = [];

    /// <summary>
    /// 定期的に経路を調べ直して、変化を記録する。
    /// 障害の切り分けで「いつ経路が変わったか」が分かると当たりが付けやすい。
    /// </summary>
    public bool IsWatching
    {
        get => _isWatching;
        set
        {
            if (!SetProperty(ref _isWatching, value)) return;

            if (value)
            {
                _watchTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = WatchInterval };
                _watchTimer.Tick -= OnWatchTick;
                _watchTimer.Tick += OnWatchTick;
                _watchTimer.Start();
                Status = $"{WatchInterval.TotalSeconds:0} 秒ごとに経路を調べ直します。";
            }
            else
            {
                _watchTimer?.Stop();
            }
        }
    }

    public RelayCommand TraceCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand MtuCommand { get; }

    /// <summary>Path MTU の探索結果。</summary>
    public string MtuText
    {
        get => _mtuText;
        private set => SetProperty(ref _mtuText, value);
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
                TraceCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            TraceCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            MtuCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunAsync()
    {
        string host = Host.Trim();

        IsBusy = true;
        Hops.Clear();
        Status = $"{host} への経路を調べています…";

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        try
        {
            IPAddress? destination = await ResolveAsync(host, token);
            if (destination is null)
            {
                Status = $"{host} の名前を解決できませんでした。";
                return;
            }

            IReadOnlyList<TraceHop> hops = await TraceProbe.TraceAsync(destination, MaxHops, TimeoutMs, StaggerMs, token);

            var viewModels = new List<TraceHopViewModel>(hops.Count);
            foreach (TraceHop hop in hops)
            {
                var vm = new TraceHopViewModel(hop);
                Hops.Add(vm);
                viewModels.Add(vm);
            }

            CheckRouteChange(hops);

            bool arrived = hops.Count > 0 && hops[^1].IsDestination;
            Status = arrived
                ? $"{destination} まで {hops.Count} ホップで到達しました。"
                : $"{MaxHops} ホップ以内に到達しませんでした。";

            // 逆引きは表示をブロックしない。判明した行から順に埋める
            await ResolveNamesAsync(hops, viewModels, token);
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"経路の取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "TraceViewModel.RunAsync");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (IsBusy) return;   // 前回がまだ終わっていなければ見送る
        _ = RunAsync();
    }

    /// <summary>
    /// 経路が変わったかを判定する。
    ///
    /// ECMP（等コストの複数経路）があると、同じ宛先でもパケットごとに経路が
    /// ばらつく。1 回違っただけで「変化」と鳴らすと誤検知だらけになるので、
    /// <b>2 回続けて同じ新しい経路</b>が観測されたときだけ記録する。
    /// </summary>
    private void CheckRouteChange(IReadOnlyList<TraceHop> hops)
    {
        string[] path = [.. hops.Select(h => h.Address?.ToString() ?? "*")];

        if (_lastPath is null)
        {
            _lastPath = path;
            return;
        }

        if (path.SequenceEqual(_lastPath))
        {
            _pendingPath = null;
            return;
        }

        if (_pendingPath is not null && path.SequenceEqual(_pendingPath))
        {
            Changes.Insert(0, new RouteChangeViewModel(DateTime.Now, _lastPath, path));
            _lastPath = path;
            _pendingPath = null;

            // 古い記録は落とす
            while (Changes.Count > 50)
                Changes.RemoveAt(Changes.Count - 1);
        }
        else
        {
            _pendingPath = path;
        }
    }

    /// <summary>
    /// 経路上で通る最大パケットサイズを調べる。
    /// VPN やトンネルの内側で通信が詰まるときの切り分けに使う。
    /// </summary>
    private async Task RunMtuAsync()
    {
        string host = Host.Trim();

        IsBusy = true;
        MtuText = "調べています…";

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        try
        {
            IPAddress? destination = await ResolveAsync(host, token);
            if (destination is null)
            {
                MtuText = $"{host} の名前を解決できませんでした。";
                return;
            }

            PathMtuResult result = await PathMtuProbe.DiscoverAsync(destination, 2000, token);

            MtuText = result.Mtu > 0
                ? $"MTU {result.Mtu} バイト（{(result.Confirmed ? "確定" : "推定")}）— {result.Note}"
                : result.Note;
        }
        catch (OperationCanceledException)
        {
            MtuText = "中断しました。";
        }
        catch (Exception ex)
        {
            MtuText = $"調べられませんでした: {ex.Message}";
            CrashLog.Write(ex, "TraceViewModel.RunMtuAsync");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
            return parsed;

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token);
            return Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? (addresses.Length > 0 ? addresses[0] : null);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task ResolveNamesAsync(
        IReadOnlyList<TraceHop> hops, List<TraceHopViewModel> viewModels, CancellationToken token)
    {
        var lookups = new List<Task>(hops.Count);

        for (int i = 0; i < hops.Count; i++)
        {
            if (hops[i].Address is not { } address) continue;

            TraceHopViewModel vm = viewModels[i];
            lookups.Add(Resolve(address, vm));
        }

        await Task.WhenAll(lookups);
        return;

        async Task Resolve(IPAddress address, TraceHopViewModel vm)
        {
            string? name = await TraceProbe.ResolveNameAsync(address, token);
            if (name is not null && !string.Equals(name, address.ToString(), StringComparison.Ordinal))
                vm.HostName = name;
        }
    }

    /// <summary>結果と入力を起動時の状態へ戻す。走っていれば止める。</summary>
    public void Reset()
    {
        _cts?.Cancel();
        IsWatching = false;

        Hops.Clear();
        Changes.Clear();

        // 経路の変化判定に使う記憶も捨てる。残すと次の 1 回目が誤って「変化」になる
        _lastPath = null;
        _pendingPath = null;

        Host = "8.8.8.8";
        MtuText = string.Empty;
        Status = string.Empty;
    }

}
