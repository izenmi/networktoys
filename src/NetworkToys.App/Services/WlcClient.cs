using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NetworkToys.Core.Net;
using NetworkToys.Core.Wireless;

namespace NetworkToys.App.Services;

/// <summary>取得できなかったことを日本語で伝える。応答本文は読まないので設定の断片は載らない。</summary>
internal sealed class WlcApiException(string message) : Exception(message);

/// <summary>
/// Catalyst 9800 の RESTCONF を叩く。ここだけが HTTP に触れる層で、
/// 応答の JSON は解釈せずそのまま返す（解釈は Core の <see cref="WlcCatalog"/>）。
///
/// <b>読み取り専用。GET しか投げない。</b>設定を書く口は持たない。
///
/// APIC と違い<b>ログインもトークンも無い</b>（HTTP Basic 認証）。そのぶん
/// 資格情報は 1 回の取得のあいだ持つことになるので、<b>この持ち物は取得ごとに作って捨てる</b>。
/// </summary>
internal sealed class WlcClient : IDisposable
{
    /// <summary>
    /// 応答の上限。RESTCONF にページングは無いので、大規模環境では 1 応答が丸ごと来る。
    /// 超えたら「大きすぎる」と言って止める（黙って握り潰さない）。
    /// </summary>
    private const int MaxResponseBytes = 64 * 1024 * 1024;

    private readonly string _baseUrl;
    private readonly PinnedCertificate _pinned;
    private readonly HttpClient _http;

    public WlcClient(
        string host, string user, string password, string? acceptedFingerprint,
        HttpMessageHandler? handler = null)
    {
        _baseUrl = "https://" + HttpsHost.Normalize(host);
        _pinned = new PinnedCertificate(acceptedFingerprint);

        _http = new HttpClient(handler ?? _pinned.CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkToys/1.0");

        // これを付けないと 406 になる。application/json では通らない
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/yang-data+json");

        // Basic 認証は毎要求に自分で付ける。HttpClientHandler.Credentials に任せると、
        // 自己診断が偽のハンドラを挿したときに経路が素通りしてしまう
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>直前に投げた URL。「応答を表示」に添えて、実機での切り分けを 1 分で終わらせる。</summary>
    public string LastUrl { get; private set; } = "";

    /// <summary>
    /// 1 本取る。<b>204 No Content は 0 件</b>（空文字を返す。失敗ではない）。
    /// </summary>
    public async Task<string> GetAsync(string path, CancellationToken token)
    {
        LastUrl = _baseUrl + path;

        using var request = new HttpRequestMessage(HttpMethod.Get, LastUrl);

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (_pinned.IsProblem(ex))
        {
            throw _pinned.Refused();
        }
        catch (HttpRequestException ex)
        {
            throw new WlcApiException($"接続できませんでした: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent) return "";

            if (!response.IsSuccessStatusCode)
                throw new WlcApiException(WlcCatalog.DescribeFailure((int)response.StatusCode));

            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 候補を順に試す。<b>モジュール名は版で割れる</b>（RRM・不正 AP・参加記録）ので、
    /// 404 なら次を試す。全部駄目なら最後の失敗をそのまま伝える。
    /// </summary>
    public async Task<string> GetFirstAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        WlcApiException? last = null;

        foreach (string path in paths)
        {
            try
            {
                return await GetAsync(path, token).ConfigureAwait(false);
            }
            catch (WlcApiException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
            {
                last = ex;
            }
        }

        throw last ?? new WlcApiException("問い合わせ先が指定されていません。");
    }

    public void Dispose() => _http.Dispose();
}
