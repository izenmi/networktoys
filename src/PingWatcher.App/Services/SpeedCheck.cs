using System.Diagnostics;
using System.IO;
using System.Net.Http;
using PingWatcher.Core.Verify;

namespace PingWatcher.App.Services;

/// <summary>
/// 速度を測る。<b>プロキシを指定して測れる</b>のが要で、
/// Zscaler と i-FILTER のどちらがボトルネックかを同じ表で比べられる。
///
/// 測り方は素直に「流したバイト数 ÷ 掛かった時間」。
/// <b>本文は捨てながら読む</b>ので、何 GB でもメモリを食わない。
/// </summary>
internal static class SpeedCheck
{
    /// <summary>1 回の測定に掛ける上限。遅い回線で待たされ続けない。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>読み捨てに使う入れ物。使い回して確保を繰り返さない。</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>アップロードで送る量の既定。宛先の指定で変えられる。</summary>
    private const long DefaultUploadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 指定した URL からダウンロードして速度を測る。
    /// </summary>
    public static async Task<(SpeedSample Sample, string UsedProxy)> DownloadAsync(
        string url, ProxyChoice proxy, CancellationToken token)
    {
        if (!TryBuildUri(url, out Uri? uri))
            return (new SpeedSample(0, 0, $"URL として読めません: {url}"), "");

        (string resolved, string? pacError) = HttpCheck.ResolveProxy(proxy, uri);
        if (pacError is not null)
            return (new SpeedSample(0, 0, pacError), "");

        using HttpClientHandler handler = HttpCheck.CreateHandler(proxy, resolved);
        using var client = new HttpClient(handler) { Timeout = Timeout };

        string usedProxy = HttpCheck.DescribeProxy(proxy, resolved);

        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (new SpeedSample(0, 0, $"HTTP {(int)response.StatusCode} が返りました"), usedProxy);

            // 応答が返ってきてから測り始める。接続と待ち時間は速度に混ぜない
            long started = Stopwatch.GetTimestamp();
            long total = 0;

            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            var chunk = new byte[ChunkBytes];
            int read;

            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                total += read;

            return (new SpeedSample(total, Elapsed(started)), usedProxy);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (new SpeedSample(0, 0, $"{Timeout.TotalSeconds:0} 秒で終わりませんでした"), usedProxy);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (new SpeedSample(0, 0, $"取得できませんでした: {ex.Message}"), usedProxy);
        }
    }

    /// <summary>
    /// 指定した URL へアップロードして速度を測る。
    ///
    /// <b>送るのは 0 で埋めた作り物</b>。宛先に <c>?bytes=…</c> や末尾の
    /// <c>|サイズMB</c> が無ければ既定の量を送る。
    /// <b>受け取ってくれる相手が要る</b>（社内なら、このアプリの FTP/SFTP サーバでもよい）。
    /// </summary>
    public static async Task<(SpeedSample Sample, string UsedProxy)> UploadAsync(
        string url, ProxyChoice proxy, CancellationToken token)
    {
        (string address, long bytes) = SplitUploadSize(url);

        if (!TryBuildUri(address, out Uri? uri))
            return (new SpeedSample(0, 0, $"URL として読めません: {url}"), "");

        (string resolved, string? pacError) = HttpCheck.ResolveProxy(proxy, uri);
        if (pacError is not null)
            return (new SpeedSample(0, 0, pacError), "");

        using HttpClientHandler handler = HttpCheck.CreateHandler(proxy, resolved);
        using var client = new HttpClient(handler) { Timeout = Timeout };

        string usedProxy = HttpCheck.DescribeProxy(proxy, resolved);

        try
        {
            long started = Stopwatch.GetTimestamp();

            using var content = new ByteArrayContent(new byte[bytes]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            using HttpResponseMessage response = await client
                .PostAsync(uri, content, token).ConfigureAwait(false);

            double elapsed = Elapsed(started);

            return response.IsSuccessStatusCode
                ? (new SpeedSample(bytes, elapsed), usedProxy)
                : (new SpeedSample(0, elapsed, $"HTTP {(int)response.StatusCode} が返りました"), usedProxy);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (new SpeedSample(0, 0, $"{Timeout.TotalSeconds:0} 秒で終わりませんでした"), usedProxy);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (new SpeedSample(0, 0, $"送信できませんでした: {ex.Message}"), usedProxy);
        }
    }

    /// <summary>
    /// 宛先の末尾の <c>|サイズMB</c> を切り出す。無ければ既定の量。
    /// URL に <c>?</c> が付くことがあるので、区切りは <c>|</c> にしてある。
    /// </summary>
    internal static (string Url, long Bytes) SplitUploadSize(string target)
    {
        string text = (target ?? "").Trim();
        int bar = text.LastIndexOf('|');

        if (bar <= 0) return (text, DefaultUploadBytes);

        return double.TryParse(text[(bar + 1)..].Trim(),
                               System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out double mb) && mb is > 0 and <= 1024
            ? (text[..bar].Trim(), (long)(mb * 1024 * 1024))
            : (text, DefaultUploadBytes);
    }

    private static bool TryBuildUri(
        string url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        string target = (url ?? "").Trim();

        if (target.Length > 0 && !target.Contains("://", StringComparison.Ordinal))
            target = "https://" + target;

        return Uri.TryCreate(target, UriKind.Absolute, out uri);
    }

    private static double Elapsed(long started)
        => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
