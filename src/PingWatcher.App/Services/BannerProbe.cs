using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PingWatcher.App.Services;

/// <summary>接続してみた結果。</summary>
/// <param name="Connected">接続できたか。</param>
/// <param name="Banner">読めた最初の応答。読まなかった／読めなかったら空。</param>
/// <param name="Problem">できなかった理由。成功なら null。</param>
/// <param name="ElapsedMs">所要時間。</param>
internal sealed record ConnectOutcome(bool Connected, string Banner, string? Problem, double ElapsedMs);

/// <summary>
/// TCP で繋いで、必要なら最初の 1 行（バナー）まで読む。
///
/// 成否の分け方は Ping/TCP 測定（<see cref="TargetMonitor"/>）に合わせる。
/// <b><c>LingerState = LingerOption(true, 0)</c> を必ず付ける</b> —
/// 行儀よく閉じると TIME_WAIT が 4 分残り、試験を繰り返すとポートを食い潰す。
/// </summary>
internal static class BannerProbe
{
    /// <summary>バナーはこの長さまで。Exchange は数十文字返す。</summary>
    private const int MaxBannerBytes = 512;

    /// <param name="readBanner">true ならサーバの第一声を待つ（メール系）。</param>
    public static async Task<ConnectOutcome> RunAsync(
        string host, int port, bool readBanner, int timeoutMs, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(host))
            return new ConnectOutcome(false, "", "宛先が空です", 0);

        if (port is <= 0 or > 65535)
            return new ConnectOutcome(false, "", "ポート番号が指定されていません", 0);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            // 測定用の接続はデータを流さないので RST で切ってよい。
            // TIME_WAIT を残さないためにこれが要る
            LingerState = new LingerOption(true, 0),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            if (!readBanner)
                return new ConnectOutcome(true, "", null, Elapsed(started));

            string banner = await ReadBannerAsync(socket, timeout.Token).ConfigureAwait(false);

            return new ConnectOutcome(true, banner, null, Elapsed(started));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ConnectOutcome(false, "", $"{timeoutMs} ミリ秒で応答がありませんでした", Elapsed(started));
        }
        catch (SocketException ex)
        {
            return new ConnectOutcome(false, "", Describe(ex), Elapsed(started));
        }
    }

    private static async Task<string> ReadBannerAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[MaxBannerBytes];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
            if (read == 0) break;

            filled += read;

            // 1 行来たら十分。全部読もうとすると、続きを送らないサーバで待たされる
            if (Array.IndexOf(buffer, (byte)'\n', 0, filled) >= 0) break;
        }

        return Encoding.ASCII.GetString(buffer, 0, filled);
    }

    /// <summary>Ping/TCP 測定と同じ分け方にする（現場での読み方を揃えるため）。</summary>
    private static string Describe(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "接続を拒否されました（ホストは生きています）",
        SocketError.HostNotFound => "名前を解決できませんでした",
        SocketError.HostUnreachable => "ホストに到達できません",
        SocketError.NetworkUnreachable => "ネットワークに到達できません",
        SocketError.TimedOut => "接続がタイムアウトしました",
        _ => $"接続できませんでした（{ex.SocketErrorCode}）",
    };

    private static double Elapsed(long started)
        => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
