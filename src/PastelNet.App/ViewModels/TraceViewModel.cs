using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

    private string _host = "8.8.8.8";
    private string _status = string.Empty;
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public TraceViewModel()
    {
        TraceCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Host));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
    }

    public ObservableCollection<TraceHopViewModel> Hops { get; } = [];

    public RelayCommand TraceCommand { get; }
    public RelayCommand CancelCommand { get; }

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
}
