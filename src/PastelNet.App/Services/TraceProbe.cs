using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PastelNet.App.Services;

/// <param name="Ttl">ホップ番号（TTL）。</param>
/// <param name="Address">応答したルータ。無応答なら null。</param>
/// <param name="RttMs">往復時間。無応答なら null。</param>
/// <param name="IsDestination">宛先そのものに届いたホップか。</param>
/// <param name="Status">ICMP の応答種別（表示の補足用）。</param>
internal sealed record TraceHop(
    int Ttl,
    IPAddress? Address,
    double? RttMs,
    bool IsDestination,
    IPStatus Status);

internal static class TraceProbe
{
    /// <summary>
    /// TTL を 1 つずつ増やしながら経路を辿る。
    ///
    /// 逐次に投げると 30 ホップ分の待ち時間が積み上がるので<b>並列</b>に投げる。
    /// ただし完全同時だと、ICMP の TTL 超過応答をレート制限するルータで欠落が増えるため、
    /// TTL ごとに少しずつ時間をずらす。
    /// </summary>
    public static async Task<IReadOnlyList<TraceHop>> TraceAsync(
        IPAddress destination,
        int maxHops,
        int timeoutMs,
        int staggerMs,
        CancellationToken token)
    {
        var tasks = new List<Task<TraceHop>>(maxHops);

        for (int ttl = 1; ttl <= maxHops; ttl++)
            tasks.Add(ProbeHopAsync(destination, ttl, timeoutMs, staggerMs * (ttl - 1), token));

        TraceHop[] hops = await Task.WhenAll(tasks);

        // 宛先に届いた時点で経路は終わり。その先のホップは意味がないので落とす
        int arrival = Array.FindIndex(hops, h => h.IsDestination);
        return arrival >= 0 ? hops[..(arrival + 1)] : hops;
    }

    private static async Task<TraceHop> ProbeHopAsync(
        IPAddress destination, int ttl, int timeoutMs, int delayMs, CancellationToken token)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs, token);

        // Ping インスタンスは同時に 1 リクエストしか扱えないので、ホップごとに用意する
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: false);
        byte[] payload = new byte[32];

        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            PingReply reply = await ping.SendPingAsync(
                destination,
                TimeSpan.FromMilliseconds(timeoutMs),
                payload,
                options,
                token);

            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            return reply.Status switch
            {
                // 宛先に到達した
                IPStatus.Success => new TraceHop(ttl, reply.Address, elapsedMs, true, reply.Status),

                // 途中のルータが TTL 切れを返した = このホップの正体
                IPStatus.TtlExpired => new TraceHop(ttl, reply.Address, elapsedMs, false, reply.Status),

                IPStatus.TimedOut => new TraceHop(ttl, null, null, false, reply.Status),

                // 到達不能を返したルータも経路上の存在としては見えている
                _ => new TraceHop(ttl, reply.Address, elapsedMs, false, reply.Status),
            };
        }
        catch (Exception ex) when (ex is PingException or SocketException)
        {
            return new TraceHop(ttl, null, null, false, IPStatus.Unknown);
        }
    }

    /// <summary>
    /// ホップの逆引き。経路表示をブロックしないよう、判明した順に差し込む前提で使う。
    /// </summary>
    public static async Task<string?> ResolveNameAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(address, token);
            return string.IsNullOrEmpty(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }
}
