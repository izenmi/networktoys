using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using NetworkToys.App.Interop;
using NetworkToys.Core.Verify;

namespace NetworkToys.App.Services;

/// <summary>
/// 「その Web ページが見られるか」を、<b>プロキシを指定して</b>確かめる。
///
/// <b>Windows のプロキシ設定は触らない。</b><see cref="HttpClientHandler.Proxy"/> を
/// その場で作り分ける。システム設定を書き換える方式はほかのアプリを巻き込むし、
/// 戻し忘れの事故になる。またシステム設定は<b>プロセス起動時に読まれて固定される</b>ので、
/// 切り替えながら試すこと自体ができない。
///
/// 認証は<b>統合 Windows 認証（ログオン中の資格情報）</b>で通す。
/// 通らなければ 407 が返るので、それと分かる文言にする。
/// </summary>
internal static class HttpCheck
{
    /// <summary>1 回の試験に掛ける上限。<b>10 秒</b>（2026-08-18 ユーザー指示）。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 本文はこの長さまで読む。遮断ページの判定と「期待する文字列」探しに要るだけで、
    /// 全部読むと大きなページで時間もメモリも食う。
    /// </summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <param name="usedProxy">実際に使ったプロキシ。証跡に残すので呼ぶ側へ返す。</param>
    public static async Task<(HttpOutcome Outcome, double ElapsedMs, string UsedProxy)> RunAsync(
        string url, ProxyChoice proxy, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        long started = Stopwatch.GetTimestamp();

        string target = url.Trim();
        if (!target.Contains("://", StringComparison.Ordinal))
            target = "http://" + target;

        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
            return (new HttpOutcome(0, "", "", "", $"URL として読めません: {url}"), 0, "");

        // PAC は宛先ごとに答えが変わる。試験する URL について引き直す
        (string resolved, string? pacError) = ResolveProxy(proxy, uri);

        if (pacError is not null)
            return (new HttpOutcome(0, "", "", "", pacError), Elapsed(started), "");

        HttpClientHandler handler;

        try
        {
            handler = CreateHandler(proxy, resolved);
        }
        catch (UriFormatException ex)
        {
            // プロキシのアドレスとして読めない（PAC が SOCKS を返した、書き方が違う…）。
            // ここで落とすと試験全体が「失敗」になるので、その 1 件の不合格にとどめる
            // （2026-08-18 に「Invalid URI」で試験ごと止まると報告された）
            return (
                new HttpOutcome(0, "", "", "", $"プロキシのアドレスとして読めません（{resolved}）: {ex.Message}"),
                Elapsed(started),
                DescribeProxy(proxy, resolved));
        }

        using (handler)
        using (var client = new HttpClient(handler) { Timeout = Timeout })
        {
            return await SendAsync(client, uri, proxy, resolved, started, token).ConfigureAwait(false);
        }
    }

    private static async Task<(HttpOutcome Outcome, double ElapsedMs, string UsedProxy)> SendAsync(
        HttpClient client, Uri uri, ProxyChoice proxy, string resolved, long started, CancellationToken token)
    {
        string usedProxy = DescribeProxy(proxy, resolved);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            string body = await ReadHeadAsync(response, token).ConfigureAwait(false);

            var outcome = new HttpOutcome(
                StatusCode: (int)response.StatusCode,
                FinalUrl: response.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri,
                ServerHeader: Header(response),
                Body: body);

            return (outcome, Elapsed(started), usedProxy);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout も OperationCanceledException で来る（中断とは区別する）
            return (Failure($"{Timeout.TotalSeconds:0} 秒で応答がありませんでした"), Elapsed(started), usedProxy);
        }
        catch (HttpRequestException ex)
        {
            return (Failure(Describe(ex)), Elapsed(started), usedProxy);
        }
    }

    /// <summary>
    /// PAC なら宛先について引き直す。ほかの指定では何もしない。
    /// <b>PAC は宛先ごとに答えが変わる</b>ので、URL が変わるたびに呼ぶ。
    /// </summary>
    internal static (string Resolved, string? Error) ResolveProxy(ProxyChoice proxy, Uri uri)
    {
        if (proxy.Mode != ProxyMode.Pac) return ("", null);

        PacLookup lookup = WinHttpNativeMethods.Resolve(proxy.Address, uri.AbsoluteUri);

        return lookup.Error is { } error ? ("", error) : (lookup.Proxy, null);
    }

    /// <summary>
    /// プロキシの指定に応じてハンドラを作る。
    ///
    /// <see cref="ProxyMode.System"/> だけは既定のまま（＝いまの Windows 設定に従う）にして、
    /// 「端末の現状」を比較の基準にできるようにする。
    /// </summary>
    internal static HttpClientHandler CreateHandler(ProxyChoice proxy, string resolved)
    {
        // リダイレクトは追う。社内サイトはログイン画面へ飛ばす作りが多く、
        // 追わないと 302 のまま「合格」に見えてしまう
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,

            // 統合 Windows 認証。プロキシ側もサーバ側も、ログオン中の資格情報で通す
            UseDefaultCredentials = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };

        // 証明書の検証は OS 既定のまま。TLS を傍受するプロキシを使う環境では、
        // その CA が Windows に入っていることが前提になる。
        // 検証を切ると「見えているのに実は別物」を通してしまうので、切らない

        string address = proxy.Mode switch
        {
            ProxyMode.Fixed => proxy.Address,
            ProxyMode.Pac => resolved,
            _ => "",
        };

        if (proxy.Mode == ProxyMode.System)
            return handler;   // 既定＝システムの設定に従う

        if (address.Length == 0)
        {
            handler.UseProxy = false;   // 直接、または PAC が DIRECT を返した
            return handler;
        }

        handler.Proxy = new WebProxy(address) { UseDefaultCredentials = true };
        handler.UseProxy = true;

        return handler;
    }

    /// <summary>証跡に出すプロキシの名前。PAC は解決先まで出す。</summary>
    /// <summary>
    /// 証跡に残す「実際に使ったプロキシ」。
    ///
    /// <b>PAC は宛先ごとに答えが変わる</b>ので、その答えまで書く。
    /// PAC が「直接」と答えたときに名前だけだと、<b>PAC を選んだのに直接出たように読める</b>
    /// （2026-08-18 に「直接と出る」と報告された）。
    /// </summary>
    internal static string DescribeProxy(ProxyChoice proxy, string resolved)
        => proxy.Mode switch
        {
            // 名前が PAC の URL そのものになることがある。長いのでファイル名だけにする
            ProxyMode.Pac => $"{proxy.ShortName}（PAC の答え: {PacProxy.Describe(resolved)}）",
            _ => proxy.ShortName,
        };

    private static async Task<string> ReadHeadAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            var buffer = new byte[MaxBodyBytes];
            int filled = 0;

            while (filled < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
                if (read == 0) break;

                filled += read;
            }

            // 文字コードは宣言を信じない（遮断ページは Shift_JIS のことがある）。
            // 見たいのは目印の文字列なので、UTF-8 として読めた分で判定する
            return System.Text.Encoding.UTF8.GetString(buffer, 0, filled);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            // 本文が読めなくても、応答コードは取れている。そちらで判定させる
            return "";
        }
    }

    private static string Header(HttpResponseMessage response)
        => response.Headers.TryGetValues("Server", out IEnumerable<string>? values)
            ? string.Join(" ", values)
            : "";

    /// <summary>次に何をすればよいか分かる文言にする。</summary>
    private static string Describe(HttpRequestException ex)
    {
        if (ex.InnerException is System.Net.Sockets.SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.HostNotFound => "名前を解決できませんでした",
                System.Net.Sockets.SocketError.ConnectionRefused => "接続を拒否されました",
                System.Net.Sockets.SocketError.TimedOut => "接続がタイムアウトしました",
                _ => $"接続できませんでした（{socket.SocketErrorCode}）",
            };
        }

        if (ex.InnerException is System.Security.Authentication.AuthenticationException)
            return "TLS の検証に失敗しました（傍受するプロキシの証明書が入っているかご確認ください）";

        return $"通信に失敗しました: {ex.Message}";
    }

    private static HttpOutcome Failure(string reason) => new(0, "", "", "", reason);

    private static double Elapsed(long started)
        => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
