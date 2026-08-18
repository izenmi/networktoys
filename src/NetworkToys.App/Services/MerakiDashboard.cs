using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using NetworkToys.Core.Cloud;

namespace NetworkToys.App.Services;

/// <summary>取得できなかったことを日本語で伝える。本文は読まないので鍵は載らない。</summary>
internal sealed class MerakiApiException(string message) : Exception(message);

/// <summary>
/// Meraki ダッシュボード API を叩く。ここだけが HTTP に触れる層で、
/// 応答の JSON は解釈せずそのまま返す（解釈は Core の MerakiCatalog）。
///
/// API キーは受け取るたびに使うだけで、このクラスも設定ファイルも保持しない。
/// </summary>
internal sealed class MerakiDashboard : IDisposable
{
    private const string BaseUrl = "https://api.meraki.com/api/v1";

    /// <summary>ページングの止め。取り切れなかったことは呼び出し側が画面に出す。</summary>
    private const int MaxPages = 20;

    private const int MaxRetries = 3;

    private readonly HttpClient _http;

    public MerakiDashboard()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            // 組織によってはリージョン別のホストへ 302 で誘導される。
            // .NET は別ホストへ自動追従するとき Authorization ヘッダを落とすので、
            // 自動追従に任せると 401 になる。自前で追ってヘッダを付け直す。
            AllowAutoRedirect = false,

            // プロキシは既定（Windows のシステム設定）のまま。IP設定タブで
            // 設定したプロキシがそのまま効く
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkToys/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Task<IReadOnlyList<string>> OrganizationsAsync(string apiKey, CancellationToken token)
        => GetPagesAsync("/organizations", apiKey, token);

    public Task<IReadOnlyList<string>> NetworksAsync(string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync($"/organizations/{Escape(organizationId)}/networks?perPage=1000", apiKey, token);

    public Task<IReadOnlyList<string>> DevicesAsync(string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync($"/organizations/{Escape(organizationId)}/devices?perPage=1000", apiKey, token);

    public Task<IReadOnlyList<string>> DeviceStatusesAsync(string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync($"/organizations/{Escape(organizationId)}/devices/statuses?perPage=1000", apiKey, token);

    /// <summary>
    /// 組織のファーム更新の記録。<b>いま動いている版はここからしか分からない</b> —
    /// 機器一覧の <c>firmware</c> は、設定と違う版で動いていると版ではなく英文を返す。
    /// </summary>
    public Task<IReadOnlyList<string>> FirmwareUpgradesAsync(
        string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync(
            $"/organizations/{Escape(organizationId)}/firmware/upgrades/byDevice?perPage=250", apiKey, token);

    /// <summary>
    /// 拠点の LAN 側の設定（VLAN ごとの MX のアドレス）。
    /// VLAN を使っていない拠点では 400 が返るので、<see cref="ApplianceSingleLanAsync"/> へ落とす。
    /// </summary>
    public Task<IReadOnlyList<string>> ApplianceVlansAsync(
        string apiKey, string networkId, CancellationToken token)
        => GetPagesAsync($"/networks/{Escape(networkId)}/appliance/vlans", apiKey, token);

    /// <summary>VLAN を使っていない拠点の LAN 側の設定。<b>応答はオブジェクト 1 つ。</b></summary>
    public Task<IReadOnlyList<string>> ApplianceSingleLanAsync(
        string apiKey, string networkId, CancellationToken token)
        => GetPagesAsync($"/networks/{Escape(networkId)}/appliance/singleLan", apiKey, token);

    public Task<IReadOnlyList<string>> UplinksAsync(string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync($"/organizations/{Escape(organizationId)}/appliance/uplink/statuses?perPage=1000", apiKey, token);

    public Task<IReadOnlyList<string>> ClientsAsync(
        string apiKey, string networkId, int timespanSeconds, CancellationToken token)
        => GetPagesAsync(
            $"/networks/{Escape(networkId)}/clients?perPage=1000&timespan={timespanSeconds}", apiKey, token);

    /// <summary>拠点のスタティックルート（MX）。</summary>
    public Task<IReadOnlyList<string>> StaticRoutesAsync(string apiKey, string networkId, CancellationToken token)
        => GetPagesAsync($"/networks/{Escape(networkId)}/appliance/staticRoutes", apiKey, token);

    /// <summary>MX 1 台の DHCP 払い出し状況。</summary>
    public Task<IReadOnlyList<string>> DhcpSubnetsAsync(string apiKey, string serial, CancellationToken token)
        => GetPagesAsync($"/devices/{Escape(serial)}/appliance/dhcp/subnets", apiKey, token);

    /// <summary>
    /// 拠点ごとの<b>まとめ</b>（ダッシュボードの「Networks by status」と同じもの）。
    /// 台数と通信量が 1 回で取れる。版によっては持っていない（404）。
    /// </summary>
    public Task<IReadOnlyList<string>> NetworkSummaryAsync(
        string apiKey, string organizationId, int timespanSeconds, CancellationToken token)
        => GetPagesAsync(
            $"/organizations/{Escape(organizationId)}/summary/top/networks/byStatus"
            + $"?timespan={timespanSeconds}&perPage=300",
            apiKey, token);

    /// <summary>拠点ごとの WAN 使用量（期間内の合計。単位はキロバイト）。</summary>
    public Task<IReadOnlyList<string>> UplinkUsageAsync(
        string apiKey, string organizationId, int timespanSeconds, CancellationToken token)
        => GetPagesAsync(
            $"/organizations/{Escape(organizationId)}/appliance/uplinks/usage/byNetwork?timespan={timespanSeconds}",
            apiKey, token);

    /// <summary>組織のアラート。版によっては持っていない（その場合は 404 が返る）。</summary>
    public Task<IReadOnlyList<string>> AlertsAsync(string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync($"/organizations/{Escape(organizationId)}/assurance/alerts?perPage=300", apiKey, token);

    // ===== 導入時確認で使うもの =====

    /// <summary>
    /// MX 1 台の回線の設定。<b>どの WAN を使う設定にしてあるか</b>はここにしか無い
    /// （状態の方には、使っていない WAN も「未接続」として出てくる）。
    /// </summary>
    public Task<IReadOnlyList<string>> UplinkSettingsAsync(string apiKey, string serial, CancellationToken token)
        => GetPagesAsync($"/devices/{Escape(serial)}/appliance/uplinks/settings", apiKey, token);

    /// <summary>
    /// 回線ごとのロスと遅延（実測値）。<b>期間は 5 分まで</b>で、
    /// リンクアップした直後はまだ値が入っていない。
    /// </summary>
    public Task<IReadOnlyList<string>> LossAndLatencyAsync(
        string apiKey, string organizationId, int timespanSeconds, CancellationToken token)
        => GetPagesAsync(
            $"/organizations/{Escape(organizationId)}/devices/uplinksLossAndLatency?timespan={timespanSeconds}",
            apiKey, token);

    /// <summary>スイッチ 1 台のポートの状態（速度・全二重・エラー）。</summary>
    public Task<IReadOnlyList<string>> SwitchPortStatusesAsync(
        string apiKey, string serial, CancellationToken token)
        => GetPagesAsync($"/devices/{Escape(serial)}/switch/ports/statuses", apiKey, token);

    /// <summary>拠点ごとの VPN の状態（AutoVPN とサードパーティ IPsec の到達性）。</summary>
    public Task<IReadOnlyList<string>> VpnStatusesAsync(
        string apiKey, string organizationId, CancellationToken token)
        => GetPagesAsync(
            $"/organizations/{Escape(organizationId)}/appliance/vpn/statuses?perPage=300", apiKey, token);

    /// <summary>ページを最後まで（上限まで）取る。</summary>
    public async Task<IReadOnlyList<string>> GetPagesAsync(string path, string apiKey, CancellationToken token)
    {
        var pages = new List<string>();
        string? url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + path;

        for (int page = 0; page < MaxPages && url is not null; page++)
        {
            (string body, string? next) = await GetOneAsync(url, apiKey, token).ConfigureAwait(false);
            pages.Add(body);

            // 次ページが同じ URL のときは進んでいない。無限に回さない
            url = next == url ? null : next;
        }

        if (url is not null)
            WasTruncated = true;

        return pages;
    }

    /// <summary>
    /// 上限に達して途中で打ち切ったか。一度立つと <see cref="ResetTruncation"/> まで残る
    /// （1 回の「取得」で何本も呼ぶので、まとめて 1 度だけ画面に出すため）。
    /// </summary>
    public bool WasTruncated { get; private set; }

    public void ResetTruncation() => WasTruncated = false;

    private async Task<(string Body, string? NextUrl)> GetOneAsync(
        string url, string apiKey, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // 鍵は既定ヘッダに焼かずリクエストごとに付ける（毎回入力なので取り違えを防ぐ）
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // メッセージにリクエスト（＝ヘッダ）を混ぜない
                throw new MerakiApiException($"接続できませんでした: {ex.Message}");
            }

            using (response)
            {
                int status = (int)response.StatusCode;

                if (IsRedirect(status) && response.Headers.Location is { } location && attempt < MaxRetries)
                {
                    // ヘッダを付け直して自分で追う（自動追従だと鍵が落ちる）
                    url = new Uri(new Uri(url), location).ToString();
                    continue;
                }

                if (status == 429 && attempt < MaxRetries)
                {
                    string? retryAfter = response.Headers.RetryAfter?.Delta is { } delta
                        ? ((int)delta.TotalSeconds).ToString()
                        : response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)
                            ? values.FirstOrDefault()
                            : null;

                    await Task.Delay(TimeSpan.FromSeconds(MerakiCatalog.RetryAfterSeconds(retryAfter)), token)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new MerakiApiException(MerakiCatalog.DescribeFailure(status));

                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                string? next = MerakiCatalog.NextPageUrl(
                    response.Headers.TryGetValues("Link", out IEnumerable<string>? link)
                        ? string.Join(", ", link)
                        : null);

                return (body, next);
            }
        }
    }

    private static bool IsRedirect(int status)
        => status is (int)HttpStatusCode.MovedPermanently
                  or (int)HttpStatusCode.Found
                  or (int)HttpStatusCode.SeeOther
                  or (int)HttpStatusCode.TemporaryRedirect
                  or (int)HttpStatusCode.PermanentRedirect;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    public void Dispose() => _http.Dispose();
}
