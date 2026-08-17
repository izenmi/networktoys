using System.Net;

namespace NetworkToys.Core.Net;

/// <summary>
/// <see cref="TrafficAggregator.Drain"/> で取り出した 1 窓ぶんの集計を、
/// 接続表の行に引き当てて B/秒へ直す。
/// </summary>
public sealed class ConnectionRates
{
    private readonly IReadOnlyDictionary<FlowKey, FlowTotals> _flows;
    private readonly double _elapsedSeconds;

    public ConnectionRates(IReadOnlyDictionary<FlowKey, FlowTotals> flows, double elapsedSeconds)
    {
        _flows = flows;
        _elapsedSeconds = elapsedSeconds;
    }

    /// <summary>
    /// 行に対応するフローの送受信レートを返す。対応が無ければ (0, 0)。
    /// TCP はイベントの saddr/daddr の向きが揺れるため正順と逆順の両方を合算する
    /// （送/受はイベント ID 由来なので合計は狂わない）。LISTEN 行には載せない
    /// （確立済みフローは別の行として表に出る）。
    /// </summary>
    public (double SentPerSec, double ReceivedPerSec) Lookup(ConnectionRow row)
    {
        if (_elapsedSeconds <= 0 || _flows.Count == 0)
            return (0, 0);

        bool tcp = row.Protocol is ConnectionProtocol.TcpV4 or ConnectionProtocol.TcpV6;
        bool v6 = row.Protocol is ConnectionProtocol.TcpV6 or ConnectionProtocol.UdpV6;

        if (tcp && row.State == TcpConnectionState.Listen)
            return (0, 0);

        Span<byte> local = stackalloc byte[16];
        if (!TryWriteAddress(row.LocalAddress, local, out int localLength))
            return (0, 0);

        long sent = 0;
        long received = 0;

        if (tcp)
        {
            Span<byte> remote = stackalloc byte[16];
            if (!TryWriteAddress(row.RemoteAddress, remote, out int remoteLength) || remoteLength != localLength)
                return (0, 0);

            FlowKey key = FlowKey.ForTcp(v6, (uint)row.Pid,
                local[..localLength], row.LocalPort, remote[..remoteLength], row.RemotePort);
            Accumulate(key, ref sent, ref received);

            FlowKey swapped = key.Swapped();
            if (swapped != key)
                Accumulate(swapped, ref sent, ref received);
        }
        else
        {
            FlowKey key = FlowKey.ForUdp(v6, (uint)row.Pid, local[..localLength], row.LocalPort);
            Accumulate(key, ref sent, ref received);
        }

        return (sent / _elapsedSeconds, received / _elapsedSeconds);
    }

    private void Accumulate(in FlowKey key, ref long sent, ref long received)
    {
        if (_flows.TryGetValue(key, out FlowTotals totals))
        {
            sent += totals.Sent;
            received += totals.Received;
        }
    }

    private static bool TryWriteAddress(string address, Span<byte> destination, out int length)
    {
        length = 0;
        // スコープ付き（fe80::1%12）も IPAddress が受け、バイト列にはスコープが乗らない
        if (!IPAddress.TryParse(address, out IPAddress? parsed))
            return false;

        return parsed.TryWriteBytes(destination, out length);
    }
}
