using System.Diagnostics;
using System.IO;
using System.Net.Http;
using PingWatcher.Core.Verify;

namespace PingWatcher.App.Services;

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

    public static async Task<(SpeedSample Sample, string UsedProxy)> RunAsync(
        ProxyChoice proxy, CancellationToken token)
    {
        var pageUri = new Uri(PageUrl);

        (string resolved, string? pacError) = HttpCheck.ResolveProxy(proxy, pageUri);
        if (pacError is not null)
            return (new SpeedSample(0, 0, pacError), "");

        using HttpClientHandler handler = HttpCheck.CreateHandler(proxy, resolved);
        using var client = new HttpClient(handler) { Timeout = Timeout };

        string usedProxy = HttpCheck.DescribeProxy(proxy, resolved);

        try
        {
            string? html = await GetStringOrNullAsync(client, PageUrl, token).ConfigureAwait(false);
            if (html is null)
                return (Failure(FastComStep.Page), usedProxy);

            if (FastComPlan.FindScriptPath(html) is not { } scriptPath)
                return (Failure(FastComStep.Script), usedProxy);

            string? script = await GetStringOrNullAsync(client, PageUrl.TrimEnd('/') + scriptPath, token)
                .ConfigureAwait(false);
            if (script is null)
                return (Failure(FastComStep.Script), usedProxy);

            if (FastComPlan.FindToken(script) is not { } tokenValue)
                return (Failure(FastComStep.Token), usedProxy);

            string? json = await GetStringOrNullAsync(
                client, FastComPlan.BuildApiUrl(tokenValue, UrlCount), token).ConfigureAwait(false);
            if (json is null)
                return (Failure(FastComStep.Api), usedProxy);

            IReadOnlyList<string> targets = FastComPlan.ParseTargets(json);
            if (targets.Count == 0)
                return (Failure(FastComStep.Targets), usedProxy);

            return (await MeasureAsync(client, targets, token).ConfigureAwait(false), usedProxy);
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
            return (new SpeedSample(0, 0, $"fast.com と通信できませんでした: {ex.Message}"), usedProxy);
        }
    }

    /// <summary>もらった URL から<b>同時に</b>流して、合計のバイト数と時間で割る。</summary>
    private static async Task<SpeedSample> MeasureAsync(
        HttpClient client, IReadOnlyList<string> targets, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();

        long[] counts = await Task.WhenAll(
            targets.Select(url => DrainAsync(client, url, token))).ConfigureAwait(false);

        return new SpeedSample(counts.Sum(), Elapsed(started));
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
