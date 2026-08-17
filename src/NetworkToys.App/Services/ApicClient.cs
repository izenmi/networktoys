using System.Net.Http;
using System.Text;
using NetworkToys.Core.Fabric;

namespace NetworkToys.App.Services;

/// <summary>取得できなかったことを日本語で伝える。応答本文は読まないので DN や設定は載らない。</summary>
internal sealed class ApicApiException(string message) : Exception(message);

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
    private readonly PinnedCertificate _pinned;
    private readonly HttpClient _http;

    private string _token = "";
    private DateTime _refreshAfter = DateTime.MaxValue;

    /// <param name="acceptedFingerprint">
    /// 前に受け入れた指紋。null なら未受け入れ（＝正規の証明書でなければ繋がない）。
    /// </param>
    /// <param name="handler">
    /// 自己診断が偽の APIC を挿すための口。既定は指紋を見る <see cref="HttpClientHandler"/>。
    /// </param>
    public ApicClient(string host, string? acceptedFingerprint, HttpMessageHandler? handler = null)
    {
        _baseUrl = "https://" + AciCatalog.NormalizeHost(host);
        _pinned = new PinnedCertificate(acceptedFingerprint);

        _http = new HttpClient(handler ?? _pinned.CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkToys/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>ページを取り切れずに打ち切ったか。</summary>
    public bool WasTruncated { get; private set; }

    public void ResetTruncation() => WasTruncated = false;

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

    /// <summary>
    /// 枝を丸ごと 1 応答で取る（テナントの設定の書き出しなど）。
    /// <b>ページングは効かない問い合わせ用</b>なので、ページを重ねない。
    /// </summary>
    public Task<string> SubtreeAsync(string path, CancellationToken token) => GetAsync(path, token);

    /// <summary>ログアウト。失敗しても構わない（トークンは放っておいても寿命で消える）。</summary>
    public async Task LogoutAsync()
    {
        if (_token.Length == 0) return;

        try
        {
            await SendAsync(HttpMethod.Post, "/api/aaaLogout.json", "{}", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApicApiException or PinnedCertificateException or HttpRequestException
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
        catch (HttpRequestException ex) when (_pinned.IsProblem(ex))
        {
            // TLS で断られた。指紋を添えて投げ、受け入れるかを人に聞いてもらう
            throw _pinned.Refused();
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

    public void Dispose() => _http.Dispose();
}
