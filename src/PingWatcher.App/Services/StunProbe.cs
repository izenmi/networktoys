using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PingWatcher.Core.Verify;

namespace PingWatcher.App.Services;

/// <summary>UDP で問い合わせた結果。</summary>
/// <param name="Reachable">応答が返ったか。</param>
/// <param name="SeenAddress">外から見えている自分のアドレス。読めなければ null。</param>
/// <param name="Problem">駄目だった理由。成功なら null。</param>
/// <param name="ElapsedMs">所要時間。</param>
internal sealed record StunOutcome(bool Reachable, IPEndPoint? SeenAddress, string? Problem, double ElapsedMs);

/// <summary>
/// STUN の Binding Request を投げて、応答が返るかを見る。
///
/// Teams の音声・映像は <b>UDP 3478〜3481</b> を通る。ここが塞がれていると
/// 「チャットはできるのに通話だけ繋がらない・音が出ない」になる。
/// ただの UDP は投げても、無応答が「開いている（相手が黙っている）」のか
/// 「塞がれている」のか区別できない。<b>STUN は応答を返す</b>ので言い切れる。
///
/// 送受信の形は <see cref="SnmpClient"/> に合わせる
/// （<c>Connect</c> 済みの <see cref="UdpClient"/> ＋ リンクした CTS ＋ 再送）。
/// </summary>
internal static class StunProbe
{
    /// <summary>UDP は落ちるものなので 1 回は撃ち直す。</summary>
    private const int Retries = 1;

    public static async Task<StunOutcome> RunAsync(
        string host, int port, int timeoutMs, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return new StunOutcome(false, null, $"{host} の名前を解決できませんでした", Elapsed(started));
        }

        // IPv4 を優先する。Teams のリレーは両方応答するが、
        // 現場で塞がれているかを見たいのは v4 側であることが多い
        IPAddress? address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault();

        if (address is null)
            return new StunOutcome(false, null, $"{host} のアドレスが得られませんでした", Elapsed(started));

        string? lastProblem = null;

        for (int attempt = 0; attempt <= Retries; attempt++)
        {
            byte[] transactionId = RandomNumberGenerator.GetBytes(12);
            byte[] request = StunMessage.BuildRequest(transactionId);

            using var udp = new UdpClient(address.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);

            try
            {
                udp.Connect(address, port);
                await udp.SendAsync(request, timeout.Token).ConfigureAwait(false);

                UdpReceiveResult received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                StunReply reply = StunMessage.ParseReply(received.Buffer, transactionId);

                if (reply.Success)
                    return new StunOutcome(true, reply.MappedAddress, null, Elapsed(started));

                lastProblem = reply.Problem;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastProblem = $"{timeoutMs} ミリ秒で応答がありませんでした（UDP {port} が塞がれている可能性があります）";
            }
            catch (SocketException ex)
            {
                return new StunOutcome(false, null, $"送信できませんでした（{ex.SocketErrorCode}）", Elapsed(started));
            }
        }

        return new StunOutcome(false, null, lastProblem, Elapsed(started));
    }

    private static double Elapsed(long started)
        => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
