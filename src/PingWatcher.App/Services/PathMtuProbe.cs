using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingWatcher.App.Services;

/// <param name="Mtu">推定した MTU（バイト）。判定できなければ 0。</param>
/// <param name="Confirmed">
/// ICMP の「要フラグメント」を実際に受け取ったか。
/// false のときはタイムアウトからの推定なので、値の確からしさが一段落ちる。
/// </param>
/// <param name="Note">判定の根拠。</param>
internal sealed record PathMtuResult(int Mtu, bool Confirmed, string Note);

/// <summary>
/// 経路上で通る最大のパケットサイズを二分探索で求める。
///
/// <b>ブラックホールに注意。</b>ICMP「要フラグメント」を返さないルータがあると、
/// 大きすぎる場合の応答が <see cref="IPStatus.PacketTooBig"/> ではなく
/// <see cref="IPStatus.TimedOut"/> になる。両者を区別して結果に残さないと、
/// 「MTU が小さい」のか「単に届いていない」のか判らなくなる。
/// </summary>
internal static class PathMtuProbe
{
    // IPv4 の下限（RFC 791 の最小 MTU 576）から、Ethernet の 1500 まで。
    // MTU = ペイロード + ICMP ヘッダ 8 + IP ヘッダ 20
    private const int Overhead = 28;
    private const int MinPayload = 548;
    private const int MaxPayload = 1472;

    public static async Task<PathMtuResult> DiscoverAsync(IPAddress destination, int timeoutMs, CancellationToken token)
    {
        using var ping = new Ping();
        var options = new PingOptions(64, dontFragment: true);

        bool sawTooBig = false;
        bool sawTimeout = false;

        async Task<bool> Fits(int payload)
        {
            byte[] buffer = new byte[payload];

            try
            {
                PingReply reply = await ping.SendPingAsync(
                    destination, TimeSpan.FromMilliseconds(timeoutMs), buffer, options, token).ConfigureAwait(false);

                switch (reply.Status)
                {
                    case IPStatus.Success:
                        return true;

                    case IPStatus.PacketTooBig:
                        sawTooBig = true;
                        return false;

                    default:
                        sawTimeout = true;
                        return false;
                }
            }
            catch (Exception ex) when (ex is PingException or SocketException)
            {
                sawTimeout = true;
                return false;
            }
        }

        // 下限すら通らないなら、そもそも相手に届いていない
        if (!await Fits(MinPayload).ConfigureAwait(false))
        {
            return new PathMtuResult(0, false,
                sawTooBig
                    ? $"{MinPayload + Overhead} バイトでも通りませんでした。経路の MTU が極端に小さい可能性があります。"
                    : "応答がありません。ICMP が遮断されているか、宛先に届いていません。");
        }

        // 上限が通るなら探索は不要
        if (await Fits(MaxPayload).ConfigureAwait(false))
            return new PathMtuResult(MaxPayload + Overhead, true, "1500 バイトがそのまま通りました。");

        int low = MinPayload;    // 通ることが分かっている
        int high = MaxPayload;   // 通らないことが分かっている

        while (high - low > 1)
        {
            token.ThrowIfCancellationRequested();

            int middle = low + (high - low) / 2;

            if (await Fits(middle).ConfigureAwait(false))
                low = middle;
            else
                high = middle;
        }

        int mtu = low + Overhead;

        string note = sawTooBig
            ? "経路上のルータが「要フラグメント」を返したため、境界を特定できました。"
            : "「要フラグメント」の応答が無く、タイムアウトから推定した値です（ブラックホールの可能性があります）。";

        if (sawTimeout && sawTooBig)
            note += " 一部の試行はタイムアウトしています。";

        return new PathMtuResult(mtu, sawTooBig, note);
    }
}
