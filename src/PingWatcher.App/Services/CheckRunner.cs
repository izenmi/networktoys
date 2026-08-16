using PingWatcher.Core.Metrics;
using PingWatcher.Core.Verify;

namespace PingWatcher.App.Services;

/// <summary>Teams の確認先。実機で要確認なので 1 か所にまとめて変えられるようにする。</summary>
/// <param name="RelayHost">メディア中継の宛先。</param>
/// <param name="Ports">音声・映像が使う UDP のポート。</param>
/// <param name="WebHost">名前解決と TCP 443 を見る相手。</param>
internal sealed record TeamsEndpoints(string RelayHost, IReadOnlyList<int> Ports, string WebHost)
{
    public static TeamsEndpoints Default { get; } =
        new("worldaz.tr.teams.microsoft.com", [3478, 3479, 3480, 3481], "teams.microsoft.com");
}

/// <summary>
/// 試験項目をまとめて実行する。
///
/// <b>プロキシが効くのは HTTP の項目だけ。</b>ほかの種類は直接出るので、
/// プロキシを何本選んでも 1 回しか実行しない（同じ結果が並ぶと証跡が読みにくい）。
/// </summary>
internal static class CheckRunner
{
    /// <summary>接続系の待ち時間。遅い社内サーバでも 5 秒あれば十分。</summary>
    private const int ConnectTimeoutMs = 5000;

    /// <summary>UDP の待ち時間。落ちることが前提なので短くして再送に回す。</summary>
    private const int UdpTimeoutMs = 2000;

    /// <summary>通話品質を測る回数。少なすぎるとゆらぎが出ず、多いと試験が長引く。</summary>
    private const int QualitySamples = 10;

    /// <summary>測る間隔。実際の音声は 20 ミリ秒ごとだが、そこまで詰める必要はない。</summary>
    private const int QualityIntervalMs = 200;

    /// <param name="progress">終わった件数と、いま試している項目名。</param>
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<CheckItem> items,
        IReadOnlyList<ProxyChoice> proxies,
        TeamsEndpoints teams,
        IProgress<(int Done, int Total, string Name)>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(teams);

        // 1 件も選ばれていないなら直接だけ。プロキシ無しの結果は常に要る
        IReadOnlyList<ProxyChoice> targets = proxies.Count > 0 ? proxies : [ProxyChoice.Direct];

        List<(CheckItem Item, ProxyChoice Proxy)> plan = [];
        foreach (CheckItem item in items)
        {
            if (item.UsesProxy)
                plan.AddRange(targets.Select(p => (item, p)));
            else
                plan.Add((item, ProxyChoice.Direct));
        }

        var results = new List<CheckResult>(plan.Count);

        // 逐次に回す。並列にすると、遅いプロキシの試験が速い方の結果に影響しうるし、
        // 現場で「いま何を試しているか」が追えなくなる
        foreach ((CheckItem item, ProxyChoice proxy) in plan)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report((results.Count, plan.Count, item.Name));

            results.Add(await RunOneAsync(item, proxy, teams, token).ConfigureAwait(false));
        }

        progress?.Report((results.Count, plan.Count, ""));

        return results;
    }

    private static async Task<CheckResult> RunOneAsync(
        CheckItem item, ProxyChoice proxy, TeamsEndpoints teams, CancellationToken token)
    {
        // 種類ごとに要るものが揃っていなければ、試験せずにそう言う。
        // 空欄のまま「不合格」にすると、設定漏れと本当の異常が混ざる
        if (item.Kind != CheckKind.Teams && item.Target.Trim().Length == 0)
            return Skip(item, "宛先が空です");

        return item.Kind switch
        {
            CheckKind.Http => await RunHttpAsync(item, proxy, token).ConfigureAwait(false),
            CheckKind.Dns => await RunDnsAsync(item, token).ConfigureAwait(false),
            CheckKind.Teams => await RunTeamsAsync(item, teams, token).ConfigureAwait(false),
            _ => await RunConnectAsync(item, token).ConfigureAwait(false),
        };
    }

    private static async Task<CheckResult> RunHttpAsync(
        CheckItem item, ProxyChoice proxy, CancellationToken token)
    {
        (HttpOutcome outcome, double ms, string used) =
            await HttpCheck.RunAsync(item.Target, proxy, token).ConfigureAwait(false);

        return HttpVerdict.Judge(item, used.Length > 0 ? used : proxy.Name, outcome, ms);
    }

    private static async Task<CheckResult> RunConnectAsync(CheckItem item, CancellationToken token)
    {
        (string host, int port) = CheckListParser.SplitTarget(item.Target, item.Kind);

        if (port == 0)
            return Skip(item, "ポート番号が指定されていません（host:port の形で書いてください）");

        bool banner = item.Kind is CheckKind.Smtp or CheckKind.Imap or CheckKind.Pop3;

        ConnectOutcome outcome = await BannerProbe
            .RunAsync(host, port, banner, ConnectTimeoutMs, token).ConfigureAwait(false);

        if (!outcome.Connected)
            return Fail(item, outcome.Problem ?? "接続できませんでした", outcome.ElapsedMs);

        if (!banner)
            return Pass(item, $"TCP {port} に接続できました", outcome.ElapsedMs);

        string summary = BannerCheck.Summarize(outcome.Banner);

        return BannerCheck.Matches(item.Kind, outcome.Banner)
            ? Pass(item, $"応答あり: {summary}", outcome.ElapsedMs)
            : Fail(item,
                   $"接続はできましたが、応答が「{BannerCheck.ExpectedPrefix(item.Kind)}」で始まりません: {summary}",
                   outcome.ElapsedMs);
    }

    private static async Task<CheckResult> RunDnsAsync(CheckItem item, CancellationToken token)
    {
        // 端末のアプリが実際に体験する解決を見たいので、システム既定のリゾルバを使う
        DnsLookupResult result = await DnsProbe
            .QueryAsync(DnsProbe.SystemResolver, item.Target, "A", ConnectTimeoutMs, token)
            .ConfigureAwait(false);

        if (!result.Success)
            return Fail(item, result.Message ?? "名前を解決できませんでした", result.ElapsedMs);

        if (result.Records.Count == 0)
            return Fail(item, "応答はありましたが答えが 0 件でした", result.ElapsedMs);

        string addresses = string.Join(", ", result.Records.Take(3).Select(r => r.Value));

        return Pass(item, $"{result.Records.Count} 件: {addresses}", result.ElapsedMs);
    }

    /// <summary>
    /// Teams は 3 つ確かめる。名前が引けるか・TCP 443 が通るか・<b>音声の UDP が通るか</b>。
    /// UDP は 1 つでも応答が返れば通話の道はある（Teams も空いている口を選ぶ）。
    /// </summary>
    private static async Task<CheckResult> RunTeamsAsync(
        CheckItem item, TeamsEndpoints teams, CancellationToken token)
    {
        var notes = new List<string>();

        DnsLookupResult dns = await DnsProbe
            .QueryAsync(DnsProbe.SystemResolver, teams.WebHost, "A", ConnectTimeoutMs, token)
            .ConfigureAwait(false);

        if (!dns.Success || dns.Records.Count == 0)
            return Fail(item, $"{teams.WebHost} の名前を解決できませんでした", dns.ElapsedMs);

        notes.Add("名前解決 ○");

        ConnectOutcome tcp = await BannerProbe
            .RunAsync(teams.WebHost, 443, readBanner: false, ConnectTimeoutMs, token).ConfigureAwait(false);

        if (!tcp.Connected)
            return Fail(item, $"TCP 443 に繋がりません（{tcp.Problem}）", tcp.ElapsedMs);

        notes.Add("TCP 443 ○");

        // ここからが本題。ここが塞がれていると通話だけできない
        var blocked = new List<int>();
        double udpMs = 0;

        foreach (int port in teams.Ports)
        {
            StunOutcome stun = await StunProbe
                .RunAsync(teams.RelayHost, port, UdpTimeoutMs, token).ConfigureAwait(false);

            udpMs += stun.ElapsedMs;

            if (stun.Reachable)
            {
                notes.Add($"UDP {port} ○");
                if (stun.SeenAddress is { } seen) notes.Add($"外から見えるアドレス {seen.Address}");

                // 通る道が分かったので、その道で通話品質まで測る。
                // ICMP ではなく音声が実際に使う UDP で測ることに意味がある
                RttStatistics stats = await StunProbe.MeasureAsync(
                    teams.RelayHost, port, QualitySamples, QualityIntervalMs, UdpTimeoutMs, token)
                    .ConfigureAwait(false);

                (bool acceptable, string quality) = CallQuality.Judge(stats);
                notes.Add(quality);

                string detail = string.Join(" / ", notes);

                // 目安を割っていても通話はできる。不合格ではなく注意にする
                return acceptable
                    ? Pass(item, detail, udpMs)
                    : Warn(item, detail, udpMs);
            }

            blocked.Add(port);
        }

        return Fail(item,
                    $"UDP {string.Join("・", blocked)} のいずれも応答がありません。"
                    + $"通話の音声が通りません（{string.Join(" / ", notes)}）",
                    udpMs);
    }

    private static CheckResult Pass(CheckItem item, string detail, double ms)
        => new(item.Name, item.Kind, item.Target, "", CheckVerdict.Pass, detail, ms);

    private static CheckResult Fail(CheckItem item, string detail, double ms)
        => new(item.Name, item.Kind, item.Target, "", CheckVerdict.Fail, detail, ms);

    private static CheckResult Warn(CheckItem item, string detail, double ms)
        => new(item.Name, item.Kind, item.Target, "", CheckVerdict.Warn, detail, ms);

    private static CheckResult Skip(CheckItem item, string reason)
        => new(item.Name, item.Kind, item.Target, "", CheckVerdict.Skipped, reason);
}
