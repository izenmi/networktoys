using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NetworkToys.Core.Assurance;
using NetworkToys.Core.Net;

namespace NetworkToys.App.Services;

/// <summary>取得できなかったことを日本語で伝える。応答本文は読まないので設定や端末の情報は載らない。</summary>
internal sealed class DnacApiException(string message) : Exception(message);

/// <summary>
/// Catalyst Center の REST を叩く。ここだけが HTTP に触れる層で、
/// 応答の JSON は解釈せずそのまま返す（解釈は Core の <see cref="DnacCatalog"/>）。
///
/// <b>読み取り専用。POST するのは <c>auth/token</c> と <c>cli/read-request</c> の 2 つだけ</b>で、
/// それ以外は GET。宛先は <c>/dna/</c> の下だけ（自己診断がこれを見ている）。
///
/// <b>パスワードは持たない。</b><see cref="LoginAsync"/> の引数で受けてその場で使い、
/// フィールドにも設定ファイルにも残さない。トークンだけを預かる。
///
/// トークンの寿命は 60 分で<b>更新する口が無い</b>。1 回の取得は数秒なので、
/// 取得のたびにこの持ち物を作って捨てる（＝そのつど 1 回だけログインする）。
/// <b>401 で自動的にやり直さない</b> — 認証の失敗を繰り返すとアカウントが固まる。
/// </summary>
internal sealed class DnacClient : IDisposable
{
    /// <summary>ページングの止め。取り切れなかったことは呼び出し側が画面に出す。</summary>
    private const int MaxPages = 20;

    /// <summary>1 ページの件数。</summary>
    public const int PageSize = 500;

    private readonly string _baseUrl;
    private readonly PinnedCertificate _pinned;
    private readonly HttpClient _http;

    private string _token = "";

    /// <param name="acceptedFingerprint">
    /// 前に受け入れた指紋。null なら未受け入れ（＝正規の証明書でなければ繋がない）。
    /// </param>
    /// <param name="handler">
    /// 自己診断が偽の Catalyst Center を挿すための口。既定は指紋を見る <see cref="HttpClientHandler"/>。
    /// </param>
    public DnacClient(string host, string? acceptedFingerprint, HttpMessageHandler? handler = null)
    {
        _baseUrl = "https://" + HttpsHost.Normalize(host);
        _pinned = new PinnedCertificate(acceptedFingerprint);

        _http = new HttpClient(handler ?? _pinned.CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkToys/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>直前に投げた URL。「応答を表示」に添えて、実機での切り分けを 1 分で終わらせる。</summary>
    public string LastUrl { get; private set; } = "";

    /// <summary>ページを取り切れずに打ち切ったか。</summary>
    public bool WasTruncated { get; private set; }

    public void ResetTruncation() => WasTruncated = false;

    /// <summary>
    /// ログインしてトークンを預かる。<b>Basic はこの 1 回だけ</b>で、以後は
    /// <c>X-Auth-Token</c> を毎要求に自分で付ける（<c>HttpClientHandler.Credentials</c> に任せると、
    /// 自己診断が偽のハンドラを挿したときに経路が素通りしてしまう）。
    /// </summary>
    public async Task LoginAsync(string user, string password, CancellationToken token)
    {
        var basic = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

        string response = await SendAsync(HttpMethod.Post, DnacCatalog.TokenPath, null, basic, null, token)
            .ConfigureAwait(false);

        string issued = DnacJson.One(response) is { } body ? DnacJson.First(body, "Token", "token") : "";

        if (issued.Length == 0)
            throw new DnacApiException("ログインの応答からトークンを読み取れませんでした。");

        _token = issued;
    }

    /// <summary>1 本取る。</summary>
    public Task<string> GetAsync(string path, CancellationToken token)
        => SendAsync(HttpMethod.Get, path, null, null, null, token);

    /// <summary>
    /// 候補を順に試す。版によって<b>無い機能は 404、引数の綴り違いは 400</b> で返るので、
    /// どちらも次の候補へ進む。全部駄目なら最後の失敗をそのまま伝える。
    /// </summary>
    public async Task<string> GetFirstAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        DnacApiException? last = null;

        foreach (string path in paths)
        {
            try
            {
                return await GetAsync(path, token).ConfigureAwait(false);
            }
            catch (DnacApiException ex) when (ex.Message.Contains("404", StringComparison.Ordinal)
                                              || ex.Message.Contains("400", StringComparison.Ordinal))
            {
                last = ex;
            }
        }

        throw last ?? new DnacApiException("問い合わせ先が指定されていません。");
    }

    /// <summary>
    /// 端末の接続先。<b><c>entity_type</c> と <c>entity_value</c> はクエリではなくヘッダ</b>。
    /// </summary>
    public async Task<string> ClientAsync(string entityType, string entityValue, CancellationToken token)
    {
        var headers = new List<(string, string)>
        {
            ("entity_type", entityType),
            ("entity_value", entityValue),
        };

        DnacApiException? last = null;

        foreach (string path in DnacCatalog.ClientEnrichmentPaths)
        {
            try
            {
                return await SendAsync(HttpMethod.Get, path, null, null, headers, token).ConfigureAwait(false);
            }
            catch (DnacApiException ex) when (ex.Message.Contains("404", StringComparison.Ordinal)
                                              || ex.Message.Contains("400", StringComparison.Ordinal))
            {
                last = ex;
            }
        }

        throw last ?? new DnacApiException("問い合わせ先が指定されていません。");
    }

    /// <summary>
    /// 機器の在庫を取り切る。返るのはページごとの生の JSON。
    /// <b><c>offset</c> は 1 始まり</b>（<see cref="DnacCatalog.DevicePath"/> が面倒を見る）。
    /// </summary>
    public async Task<IReadOnlyList<string>> DevicesAsync(CancellationToken token)
    {
        var pages = new List<string>();

        for (int page = 0; page < MaxPages; page++)
        {
            string json = await GetAsync(DnacCatalog.DevicePath(page, PageSize), token).ConfigureAwait(false);

            pages.Add(json);

            // 1 ページに満たなければそこで終わり（件数を数える API を叩くと 1 往復増えるだけ）
            if (DnacJson.Rows(json).Count < PageSize) return pages;
        }

        WasTruncated = true;

        return pages;
    }

    /// <summary>
    /// 参照コマンドを流して出力を受け取る。
    ///
    /// <b>投げるコマンドを組み立てるのは呼び出し側の仕事</b>（Catalyst Center 自身の許可一覧と
    /// <c>CiscoCommandGuard</c> の二重の網を通したものだけを渡すこと）。
    /// ここは往復の面倒だけを見る。
    ///
    /// <b>中断してもこちらが待つのをやめるだけ</b>で、機器での実行は取り消せない。
    /// </summary>
    public async Task<string> ReadRequestAsync(
        IReadOnlyList<string> deviceIds, IReadOnlyList<string> commands, CancellationToken token)
    {
        string accepted = await SendAsync(
            HttpMethod.Post, DnacCatalog.ReadRequestPath,
            DnacCatalog.ReadRequestBody(deviceIds, commands), null, null, token).ConfigureAwait(false);

        string taskId = DnacCatalog.ParseTaskId(accepted);

        if (taskId.Length == 0)
            throw new DnacApiException("実行を受け付けてもらえませんでした（追跡番号が返りません）。");

        // 2 秒おきに 30 回（約 60 秒）。終わらなければ黙って握らずにその旨を返す
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);

            (bool done, string fileId, string? error) =
                DnacCatalog.ParseTask(await GetAsync(DnacCatalog.TaskPath(taskId), token).ConfigureAwait(false));

            if (error is not null) throw new DnacApiException(error);
            if (done) return await GetAsync(DnacCatalog.FilePath(fileId), token).ConfigureAwait(false);
        }

        throw new DnacApiException(
            "まだ終わっていません。Catalyst Center の画面から結果を確認してください。");
    }

    /// <summary>
    /// 1 往復。<b>宛先とメソッドの縛りはここ 1 か所</b>に閉じてある。
    /// </summary>
    private async Task<string> SendAsync(
        HttpMethod method, string path, string? body,
        AuthenticationHeaderValue? basic, IReadOnlyList<(string Name, string Value)>? headers,
        CancellationToken token)
    {
        // 読み取り専用の縛り。思い違いでここを踏み抜くくらいなら落ちた方がよい
        if (!path.StartsWith("/dna/", StringComparison.Ordinal))
            throw new DnacApiException($"想定していない宛先です: {path}");

        if (method == HttpMethod.Post
            && path != DnacCatalog.TokenPath
            && path != DnacCatalog.ReadRequestPath)
            throw new DnacApiException($"書き込みになりうる宛先です: {path}");

        if (method != HttpMethod.Get && method != HttpMethod.Post)
            throw new DnacApiException($"使わないメソッドです: {method}");

        LastUrl = _baseUrl + path;

        using var request = new HttpRequestMessage(method, LastUrl);

        if (basic is not null) request.Headers.Authorization = basic;
        else if (_token.Length > 0) request.Headers.Add("X-Auth-Token", _token);

        foreach ((string name, string value) in headers ?? [])
            request.Headers.Add(name, value);

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
            throw new DnacApiException($"接続できませんでした: {ex.Message}");
        }

        using (response)
        {
            // リストが空のとき 204 を返す版がある。0 件であって失敗ではない
            if (response.StatusCode == HttpStatusCode.NoContent) return "";

            if (!response.IsSuccessStatusCode)
                throw new DnacApiException(DnacCatalog.DescribeFailure((int)response.StatusCode));

            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();
}
