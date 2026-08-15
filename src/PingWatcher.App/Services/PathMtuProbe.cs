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

    /// <summary>探索全体に許す時間。ブラックホール経路では 1 回ごとにタイムアウトを
    /// 待つため、上限が無いと二分探索の回数ぶん(最悪 20 回超)黙り込む。</summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(15);

    public static async Task<PathMtuResult> DiscoverAsync(IPAddress destination, int timeoutMs, CancellationToken token)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
        overall.CancelAfter(OverallTimeout);

        try
        {
            return await DiscoverCoreAsync(destination, timeoutMs, overall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new PathMtuResult(0, false,
                $"{OverallTimeout.TotalSeconds:0} 秒以内に判定できませんでした。経路が応答を返していません。");
        }
    }

    private static async Task<PathMtuResult> DiscoverCoreAsync(IPAddress destination, int timeoutMs, CancellationToken token)
    {
        using var ping = new Ping();
        var options = new PingOptions(64, dontFragment: true);

        bool sawTooBig = false;
        bool sawTimeout = false;
        bool sawUnreachable = false;

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

                    case IPStatus.TimedOut:
                        sawTimeout = true;
                        return false;

                    default:
                        // 到達不能などをタイムアウト扱いにすると
                        // 「ブラックホールの可能性」と誤った説明を出してしまう
                        sawUnreachable = true;
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
                    : sawUnreachable
                        ? "到達不能が返っています。宛先か経路の設定を確認してください。"
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
