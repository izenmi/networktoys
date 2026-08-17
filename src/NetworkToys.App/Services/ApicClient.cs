using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using NetworkToys.Core.Fabric;

namespace NetworkToys.App.Services;

/// <summary>取得できなかったことを日本語で伝える。応答本文は読まないので DN や設定は載らない。</summary>
internal sealed class ApicApiException(string message) : Exception(message);

/// <summary>
/// 証明書を受け入れていないので繋がない、という失敗。<see cref="Fingerprint"/> を画面に出して
/// 人に見比べてもらうために、指紋を持って投げる。
/// </summary>
internal sealed class ApicCertificateException(string message, string fingerprint) : Exception(message)
{
    public string Fingerprint { get; } = fingerprint;
}

/// <summary>
/// APIC の REST を叩く。ここだけが HTTP に触れる層で、応答の JSON は解釈せず
/// そのまま返す（解釈は Core の <see cref="AciCatalog"/>）。
///
/// <b>読み取り専用。</b>POST するのは aaaLogin / aaaRefresh / aaaLogout の 3 つだけで、
/// 設定を書く口は持たない。
///
/// <b>パスワードは持たない。</b><see cref="LoginAsync"/> の引数で受けてその場で使い、
/// フィールドにも設定ファイルにも残さない。
/// </summary>
internal sealed class ApicClient : IDisposable
{
    /// <summary>ページングの止め。取り切れなかったことは呼び出し側が画面に出す。</summary>
    private const int MaxPages = 20;

    private readonly string _baseUrl;
    private readonly string? _accepted;
    private readonly HttpClient _http;

    private string _token = "";
    private DateTime _refreshAfter = DateTime.MaxValue;

    /// <summary>
    /// 相手が出した証明書の指紋。<b>受け入れずに切ったときも控える</b> —
    /// 出せなければ、人は受け入れようがない。
    /// 検証はリクエストのスレッドから呼ばれるので volatile。
    /// </summary>
    private volatile string _seen = "";

    /// <param name="acceptedFingerprint">
    /// 前に受け入れた指紋。null なら未受け入れ（＝正規の証明書でなければ繋がない）。
    /// </param>
    /// <param name="handler">
    /// 自己診断が偽の APIC を挿すための口。既定は指紋を見る <see cref="HttpClientHandler"/>。
    /// </param>
    public ApicClient(string host, string? acceptedFingerprint, HttpMessageHandler? handler = null)
    {
        _baseUrl = "https://" + AciCatalog.NormalizeHost(host);
        _accepted = acceptedFingerprint;

        _http = new HttpClient(handler ?? BuildHandler(this))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkToys/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>ページを取り切れずに打ち切ったか。</summary>
    public bool WasTruncated { get; private set; }

    public void ResetTruncation() => WasTruncated = false;

    /// <summary>
    /// <b>検証は切らない。</b>正規の証明書はそのまま通し、そうでないものは
    /// 受け入れ済みの指紋と一致したときだけ通す（SSH のホスト鍵と同じ思想）。
    /// </summary>
    private static HttpClientHandler BuildHandler(ApicClient owner) => new()
    {
        // http へ落とす 302 に黙って従わない（相手が APIC でないかもしれない）
        AllowAutoRedirect = false,

        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;

            owner._seen = AciCatalog.FormatFingerprint(certificate.GetCertHash(HashAlgorithmName.SHA256));

            if (errors == SslPolicyErrors.None) return true;

            return owner._seen.Length > 0
                   && string.Equals(owner._seen, owner._accepted, StringComparison.Ordinal);
        },
    };

    /// <summary>ログインしてトークンを預かる。</summary>
    public async Task LoginAsync(string user, string password, string domain, CancellationToken token)
    {
        string body = AciCatalog.LoginBody(AciCatalog.LoginName(domain, user), password);
        string response = await SendAsync(HttpMethod.Post, "/api/aaaLogin.json", body, token).ConfigureAwait(false);

        (string issued, int seconds) = AciCatalog.ParseLogin(response);

        if (issued.Length == 0)
            throw new ApicApiException("ログインの応答からトークンを読み取れませんでした。");

        _token = issued;

        // 寿命の 8 割で更新する。タイマーは持たない（押したときだけ通信する作り）
        _refreshAfter = seconds > 0
            ? DateTime.UtcNow.AddSeconds(seconds * 0.8)
            : DateTime.MaxValue;
    }

    /// <summary>
    /// 1 クラスぶんを取り切る。<paramref name="scopeDn"/> を渡すとその配下だけに絞る。
    /// 返すのはページごとの生の JSON（解釈はしない）。
    /// </summary>
    public async Task<IReadOnlyList<string>> ClassAsync(
        string className, string? options, string? scopeDn, CancellationToken token)
    {
        string path = AciCatalog.ClassPath(className, options, scopeDn);
        var pages = new List<string>();

        string first = await GetAsync(AciCatalog.PagePath(path, 0), token).ConfigureAwait(false);
        pages.Add(first);

        int total = AciMoReader.TotalCount(first);
        int count = AciCatalog.PageCount(total);

        if (count > MaxPages)
        {
            count = MaxPages;
            WasTruncated = true;
        }

        for (int page = 1; page < count; page++)
            pages.Add(await GetAsync(AciCatalog.PagePath(path, page), token).ConfigureAwait(false));

        return pages;
    }

    /// <summary>ログアウト。失敗しても構わない（トークンは放っておいても寿命で消える）。</summary>
    public async Task LogoutAsync()
    {
        if (_token.Length == 0) return;

        try
        {
            await SendAsync(HttpMethod.Post, "/api/aaaLogout.json", "{}", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApicApiException or ApicCertificateException or HttpRequestException
                                      or OperationCanceledException)
        {
            // 出ていくだけなので、失敗を画面に出すほどのことではない
        }
        finally
        {
            _token = "";
        }
    }

    /// <summary>取得の前に、トークンの寿命が近ければ延ばす。</summary>
    private async Task<string> GetAsync(string path, CancellationToken token)
    {
        if (_token.Length > 0 && DateTime.UtcNow > _refreshAfter)
        {
            string refreshed = await SendAsync(HttpMethod.Get, "/api/aaaRefresh.json", null, token)
                .ConfigureAwait(false);

            (string issued, int seconds) = AciCatalog.ParseLogin(refreshed);

            if (issued.Length > 0) _token = issued;

            _refreshAfter = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds * 0.8) : DateTime.MaxValue;
        }

        return await SendAsync(HttpMethod.Get, path, null, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 1 往復。<b>Cookie の入れ物には任せず、毎回自分で付ける</b> —
    /// <c>CookieContainer</c> は <c>HttpClientHandler</c> の持ち物なので、
    /// 自己診断が偽のハンドラを挿すとその経路が素通りしてしまう。
    /// </summary>
    private async Task<string> SendAsync(HttpMethod method, string path, string? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);

        if (_token.Length > 0)
            request.Headers.Add("Cookie", "APIC-cookie=" + _token);

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsCertificateProblem(ex))
        {
            // TLS で断られた。指紋を添えて投げ、受け入れるかを人に聞いてもらう
            throw new ApicCertificateException("証明書を確認できませんでした。", _seen);
        }
        catch (HttpRequestException ex)
        {
            throw new ApicApiException($"接続できませんでした: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ApicApiException(AciCatalog.DescribeFailure((int)response.StatusCode));

            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// TLS で断られたのかを見分ける。握手の失敗は
    /// <see cref="AuthenticationException"/> を内側に持つ形で出てくる。
    /// 検証コールバックが指紋を控えていれば、それも根拠になる。
    /// </summary>
    private bool IsCertificateProblem(HttpRequestException ex)
        => ex.InnerException is AuthenticationException
           || (_seen.Length > 0 && !string.Equals(_seen, _accepted, StringComparison.Ordinal));

    public void Dispose() => _http.Dispose();
}
