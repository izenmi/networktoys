using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PingWatcher.App.Interop;

namespace PingWatcher.App.Services;

/// <param name="Address">応答したホスト。</param>
/// <param name="RttMs">応答時間。</param>
/// <param name="Mac">ARP から引いた MAC。取れなければ null。</param>
/// <param name="Vendor">MAC から引いたベンダー名。</param>
/// <param name="HostName">逆引き結果。</param>
/// <param name="OpenPorts">開いていたポート。</param>
internal sealed record ScanHit(
    IPAddress Address,
    double RttMs,
    string? Mac,
    string? Vendor,
    string? HostName,
    IReadOnlyList<int> OpenPorts);

/// <param name="Done">完了したホスト数。</param>
/// <param name="Total">対象ホスト数。</param>
/// <param name="Found">見つかったホスト数。</param>
/// <param name="Phase">今どの段階か。</param>
internal sealed record ScanProgress(int Done, int Total, int Found, string Phase);

internal sealed class ScanOptions
{
    public int TimeoutMs { get; init; } = 800;

    /// <summary>同時に飛ばす ping の数。多すぎると自分の回線を潰す。</summary>
    public int Concurrency { get; init; } = 128;

    public bool ResolveNames { get; init; }

    public bool ScanPorts { get; init; }

    /// <summary>現場で意味のある範囲に絞ったポート。全 65535 は既定にしない。</summary>
    public IReadOnlyList<int> Ports { get; init; } = DefaultPorts;

    public static readonly int[] DefaultPorts =
    [
        21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445,
        515, 631, 993, 995, 1433, 3306, 3389, 5432, 8080, 8443,
    ];
}

/// <summary>
/// 指定範囲へ並列に ping を打ち、応答したホストの素性を調べる。
/// </summary>
internal sealed class ScanService
{
    /// <summary>フェーズ 3 で同時に調べるホスト数の上限。ホスト内のポート並列(16)と掛け算になる。</summary>
    private const int LookupConcurrency = 16;

    public Task<IReadOnlyList<ScanHit>> ScanAsync(
        IReadOnlyList<IPAddress> targets,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        // 呼び出し元は UI スレッド。ここで ThreadPool へ逃がさないと、数千個の
        // async ラムダの同期部分・ARP の同期 P/Invoke・ソートがすべて
        // Dispatcher 上で走り、スキャン中はウィンドウのドラッグすら効かなくなる。
        // 進捗は Progress<T> が UI 文脈を覚えているので、そのまま画面に届く。
        return Task.Run(() => ScanCoreAsync(targets, options, progress, token), token);
    }

    private static async Task<IReadOnlyList<ScanHit>> ScanCoreAsync(
        IReadOnlyList<IPAddress> targets,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {

        var alive = new List<(IPAddress Address, double RttMs)>();
        using var gate = new SemaphoreSlim(options.Concurrency, options.Concurrency);
        int done = 0;

        // 1) 生きているホストを洗い出す
        var pings = targets.Select(async address =>
        {
            await gate.WaitAsync(token);
            try
            {
                double? rtt = await PingOnceAsync(address, options.TimeoutMs, token);
                if (rtt is { } value)
                {
                    lock (alive)
                        alive.Add((address, value));
                }
            }
            finally
            {
                gate.Release();

                int current = Interlocked.Increment(ref done);
                if (current % 16 == 0 || current == targets.Count)
                {
                    int found;
                    lock (alive) found = alive.Count;
                    progress?.Report(new ScanProgress(current, targets.Count, found, "応答を確認しています"));
                }
            }
        });

        await Task.WhenAll(pings);
        token.ThrowIfCancellationRequested();

        alive.Sort((a, b) => CompareAddresses(a.Address, b.Address));

        // 2) ARP テーブルは 1 回だけ読む。ホストごとに引くより遥かに速い。
        //    ping の直後に読むのが肝で、この時点なら近隣キャッシュが埋まっている。
        progress?.Report(new ScanProgress(targets.Count, targets.Count, alive.Count, "MAC アドレスを調べています"));
        Dictionary<string, string> arp = NativeMethods.GetArpTable();

        // 3) 逆引きとポートスキャン
        progress?.Report(new ScanProgress(targets.Count, targets.Count, alive.Count, "ホスト名とポートを調べています"));

        var results = new List<ScanHit>(alive.Count);

        // ホスト間にも上限を掛ける。応答ホスト全件へ一斉に掛かると、/24 で 200 台
        // 応答しただけで 200×16 = 3,200 接続が同時に開き、Windows 側のリソース上限に
        // 当たった接続が「閉じている」と誤判定される（開いているポートを閉と報告する
        // のは診断ツールとして最悪の壊れ方）。逆引きの同時数も同じ理由で絞る。
        using var hostGate = new SemaphoreSlim(LookupConcurrency, LookupConcurrency);
        var lookups = alive.Select(async entry =>
        {
            string? mac = arp.GetValueOrDefault(entry.Address.ToString());
            string? vendor = mac is null ? null : OuiCatalog.FindVendor(mac);

            await hostGate.WaitAsync(token);
            try
            {
                string? hostName = options.ResolveNames ? await ResolveNameAsync(entry.Address, token) : null;

                IReadOnlyList<int> ports = options.ScanPorts
                    ? await ScanPortsAsync(entry.Address, options, token)
                    : [];

                lock (results)
                    results.Add(new ScanHit(entry.Address, entry.RttMs, mac, vendor, hostName, ports));
            }
            finally
            {
                hostGate.Release();
            }
        });

        await Task.WhenAll(lookups);

        results.Sort((a, b) => CompareAddresses(a.Address, b.Address));
        return results;
    }

    private static async Task<double?> PingOnceAsync(IPAddress address, int timeoutMs, CancellationToken token)
    {
        using var ping = new Ping();
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            PingReply reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: token);
            return reply.Status == IPStatus.Success
                ? Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException)
        {
            return null;
        }
    }

    private static async Task<string?> ResolveNameAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            // 逆引きは引けない相手の方が多い。1 台ずつ DNS のタイムアウトを待つと
            // スキャン全体が終わらないので、待つ側で見切りをつける。
            IPHostEntry entry = await Dns.GetHostEntryAsync(address.ToString(), token)
                .WaitAsync(TimeSpan.FromSeconds(3), token);

            return string.IsNullOrEmpty(entry.HostName) || entry.HostName == address.ToString()
                ? null
                : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<int>> ScanPortsAsync(IPAddress address, ScanOptions options, CancellationToken token)
    {
        var open = new List<int>();

        // 1 ホストあたりの同時接続は控えめに。相手にも自分にも優しくする
        using var gate = new SemaphoreSlim(16, 16);

        var probes = options.Ports.Select(async port =>
        {
            await gate.WaitAsync(token);
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                // RST で閉じて TIME_WAIT を残さない。行儀よく FIN で閉じると
                // ポートごとに約 4 分エフェメラルポートを掴んだままになり、
                // スキャンを繰り返すと自分の一時ポートが枯渇する
                socket.LingerState = new LingerOption(true, 0);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(options.TimeoutMs);

                await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token);

                lock (open)
                    open.Add(port);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 閉じている・届かないのは想定内
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes);

        open.Sort();
        return open;
    }

    private static int CompareAddresses(IPAddress a, IPAddress b)
        => Core.Addressing.IpMath.ToUInt32(a).CompareTo(Core.Addressing.IpMath.ToUInt32(b));
}
