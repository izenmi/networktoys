using System.Diagnostics;
using System.IO;
using System.Net.Http;
using NetworkToys.Core.Verify;

namespace NetworkToys.App.Services;

/// <summary>
/// fast.com（Netflix）で速度を測る。
///
/// <b>公開 API ではない。</b>ブラウザで動く JS が、ページ内の <c>app-*.js</c> に
/// 埋め込まれたトークンを使って <c>api.fast.com</c> を叩く仕組みなので、
/// アプリからはトークンを正規表現で抜き出すことになる。
/// <b>先方の作りが変われば黙って壊れる。</b>
///
/// せめて壊れ方が分かるように、<b>手順のどこで躓いたかを言い分ける</b>
/// （ページが取れないのか・スクリプトが見つからないのか・トークンが読めないのか）。
/// 文字列の扱いは <see cref="FastComPlan"/> に置いて CI で固めてある。
///
/// 安定を優先するなら「速度」の種類で URL を直に指定する方がよい。
/// </summary>
internal static class FastComCheck
{
    private const string PageUrl = "https://fast.com/";

    /// <summary>並列に流す本数。fast.com のブラウザ版もこの程度を使う。</summary>
    private const int UrlCount = 3;

    /// <summary>1 本あたりに読む上限。全部読むと回線が速いほど長引く。</summary>
    private const long MaxBytesPerStream = 25 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const int ChunkBytes = 64 * 1024;

    /// <summary>上りで送る量（1 本あたり）。下りと違い、こちらが作って送るので上限を決める。</summary>
    private const int UploadBytesPerStream = 4 * 1024 * 1024;

    /// <returns>下りと上りの測定。上りは測れなければ理由が入る。</returns>
    public static async Task<(SpeedSample Down, SpeedSample Up, string UsedProxy)> RunAsync(
        ProxyChoice proxy, CancellationToken token)
    {
        var pageUri = new Uri(PageUrl);

        (string resolved, string? pacError) = HttpCheck.ResolveProxy(proxy, pageUri);
        if (pacError is not null)
            return (new SpeedSample(0, 0, pacError), NotMeasured, "");

        using HttpClientHandler handler = HttpCheck.CreateHandler(proxy, resolved);
        using var client = new HttpClient(handler) { Timeout = Timeout };

        string usedProxy = HttpCheck.DescribeProxy(proxy, resolved);

        try
        {
            string? html = await GetStringOrNullAsync(client, PageUrl, token).ConfigureAwait(false);
            if (html is null)
                return (Failure(FastComStep.Page), NotMeasured, usedProxy);

            if (FastComPlan.FindScriptPath(html) is not { } scriptPath)
                return (Failure(FastComStep.Script), NotMeasured, usedProxy);

            string? script = await GetStringOrNullAsync(client, PageUrl.TrimEnd('/') + scriptPath, token)
                .ConfigureAwait(false);
            if (script is null)
                return (Failure(FastComStep.Script), NotMeasured, usedProxy);

            if (FastComPlan.FindToken(script) is not { } tokenValue)
                return (Failure(FastComStep.Token), NotMeasured, usedProxy);

            string? json = await GetStringOrNullAsync(
                client, FastComPlan.BuildApiUrl(tokenValue, UrlCount), token).ConfigureAwait(false);
            if (json is null)
                return (Failure(FastComStep.Api), NotMeasured, usedProxy);

            IReadOnlyList<string> targets = FastComPlan.ParseTargets(json);
            if (targets.Count == 0)
                return (Failure(FastComStep.Targets), NotMeasured, usedProxy);

            SpeedSample down = await MeasureDownAsync(client, targets, token).ConfigureAwait(false);

            // 上りは同じ測定先へ送り返す。塞がれていても下りの結果は残したいので、
            // ここで失敗しても理由を持たせて返すだけにする
            SpeedSample up = await MeasureUpAsync(client, targets, token).ConfigureAwait(false);

            return (down, up, usedProxy);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (new SpeedSample(0, 0, $"{Timeout.TotalSeconds:0} 秒で終わりませんでした"), NotMeasured, usedProxy);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (new SpeedSample(0, 0, $"fast.com と通信できませんでした: {ex.Message}"), NotMeasured, usedProxy);
        }
    }

    /// <summary>測っていないことを表す印。下りだけ取れた場合に上りへ入れる。</summary>
    private static SpeedSample NotMeasured => new(0, 0, "測っていません");

    /// <summary>もらった URL から<b>同時に</b>流して、合計のバイト数と時間で割る。</summary>
    private static async Task<SpeedSample> MeasureDownAsync(
        HttpClient client, IReadOnlyList<string> targets, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();

        long[] counts = await Task.WhenAll(
            targets.Select(url => DrainAsync(client, url, token))).ConfigureAwait(false);

        return new SpeedSample(counts.Sum(), Elapsed(started));
    }

    /// <summary>
    /// 同じ測定先へ<b>送り返して</b>上りを測る。fast.com のブラウザ版も同じ相手を使う。
    /// 1 本も通らなければ、その旨を理由として返す（上りだけ絞られている経路がある）。
    /// </summary>
    private static async Task<SpeedSample> MeasureUpAsync(
        HttpClient client, IReadOnlyList<string> targets, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();

        (long Bytes, string? Reason)[] results = await Task.WhenAll(
            targets.Select(url => PushAsync(client, url, token))).ConfigureAwait(false);

        long total = results.Sum(r => r.Bytes);

        if (total > 0) return new SpeedSample(total, Elapsed(started));

        // <b>断られた理由をそのまま出す。</b>「受け付けてもらえませんでした」だけでは、
        // プロキシが弾いたのか先方が断ったのか分からない（2026-08-18 報告）
        string? reason = results.Select(r => r.Reason).FirstOrDefault(r => r is { Length: > 0 });

        return new SpeedSample(
            0,
            Elapsed(started),
            reason is { Length: > 0 }
                ? $"送信を受け付けてもらえませんでした（{reason}）"
                : "送信を受け付けてもらえませんでした");
    }

    /// <summary>
    /// 0 で埋めた作り物を送る。受け付けられなければ 0 として続ける。
    ///
    /// <b>宛先から <c>/range/…</c> を落とす</b> — 下りは範囲付きで取るが、
    /// 上りは範囲を持たない同じ URL へ送るのが fast.com の作り。
    /// </summary>
    private static async Task<(long Bytes, string? Reason)> PushAsync(
        HttpClient client, string url, CancellationToken token)
    {
        try
        {
            using var content = new ByteArrayContent(new byte[UploadBytesPerStream]);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            using HttpResponseMessage response = await client
                .PostAsync(UploadUrl(url), content, token).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? (UploadBytesPerStream, null)
                : (0, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return (0, ex.Message);
        }
    }

    /// <summary>上りの宛先。<c>/range/…</c> が付いていれば落とす。</summary>
    internal static string UploadUrl(string url)
    {
        int range = url.IndexOf("/range/", StringComparison.OrdinalIgnoreCase);

        if (range < 0) return url;

        int query = url.IndexOf('?', StringComparison.Ordinal);

        // 範囲の後ろに ? が付く形（…/range/0-100?c=jp）でも、問い合わせは残す
        return query > range ? url[..range] + url[query..] : url[..range];
    }

    /// <summary>読み捨てながらバイト数だけ数える。1 本が失敗しても 0 として続ける。</summary>
    private static async Task<long> DrainAsync(HttpClient client, string url, CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return 0;

            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            var chunk = new byte[ChunkBytes];
            long total = 0;
            int read;

            while (total < MaxBytesPerStream
                   && (read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return 0;
        }
    }

    private static async Task<string?> GetStringOrNullAsync(
        HttpClient client, string url, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)
            : null;
    }

    private static SpeedSample Failure(FastComStep step)
        => new(0, 0, FastComPlan.DescribeFailure(step));

    private static double Elapsed(long started)
        => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
