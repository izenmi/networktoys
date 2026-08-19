using System.Diagnostics;
using NetworkToys.Core.Metrics;
using NetworkToys.Core.Verify;

namespace NetworkToys.App.Services;

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
    /// <summary>
    /// 接続系の待ち時間。<b>HTTP と揃えて 10 秒</b>（2026-08-18 ユーザー指示）。
    /// 種類ごとに違うと「どれくらい待たされるか」が読めない。
    /// </summary>
    private const int ConnectTimeoutMs = 10_000;

    /// <summary>UDP の待ち時間。落ちることが前提なので短くして再送に回す。</summary>
    private const int UdpTimeoutMs = 2000;

    /// <summary>通話品質を測る回数。少なすぎるとゆらぎが出ず、多いと試験が長引く。</summary>
    private const int QualitySamples = 10;

    /// <summary>測る間隔。実際の音声は 20 ミリ秒ごとだが、そこまで詰める必要はない。</summary>
    private const int QualityIntervalMs = 200;

    /// <param name="progress">終わった件数と、いま試している項目名。</param>
    /// <param name="finished">
    /// 1 件終わるたびに、その結果。<b>全部終わるのを待たせない</b>ための口。
    /// 項目数とプロキシの本数によっては何分もかかるので、済んだところから見せる。
    /// </param>
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        IReadOnlyList<CheckItem> items,
        IReadOnlyList<ProxyChoice> proxies,
        TeamsEndpoints teams,
        IProgress<(int Done, int Total, string Name)>? progress,
        CancellationToken token,
        IProgress<CheckResult>? finished = null)
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

            CheckResult result = await RunOneAsync(item, proxy, teams, token).ConfigureAwait(false);

            results.Add(result);
            finished?.Report(result);
        }

        progress?.Report((results.Count, plan.Count, ""));

        return results;
    }

    private static async Task<CheckResult> RunOneAsync(
        CheckItem item, ProxyChoice proxy, TeamsEndpoints teams, CancellationToken token)
    {
        // 種類ごとに要るものが揃っていなければ、試験せずにそう言う。
        // 空欄のまま「不合格」にすると、設定漏れと本当の異常が混ざる
        // 宛先が要らない種類。fast.com と Teams は決まった相手へ行き、
        // DNS は空なら自分のホスト名を引く
        if (item.Kind is not (CheckKind.Teams or CheckKind.FastCom or CheckKind.Dns)
            && item.Target.Trim().Length == 0)
            return Skip(item, "宛先が空です");

        return item.Kind switch
        {
            CheckKind.Http => await RunHttpAsync(item, proxy, token).ConfigureAwait(false),
            CheckKind.Dns => await RunDnsAsync(item, token).ConfigureAwait(false),
            CheckKind.Teams => await RunTeamsAsync(item, proxy, teams, token).ConfigureAwait(false),
            CheckKind.Download or CheckKind.Upload or CheckKind.FastCom
                => await RunSpeedAsync(item, proxy, token).ConfigureAwait(false),
            CheckKind.Manual => OpenForPerson(item),
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

    /// <summary>
    /// 速度を測る。<b>プロキシ経由で測れる</b>ので、どちらがボトルネックかが分かる。
    /// </summary>
    private static async Task<CheckResult> RunSpeedAsync(
        CheckItem item, ProxyChoice proxy, CancellationToken token)
    {
        // fast.com だけは 1 回で上りも測れる。ほかは指定された向きだけ
        if (item.Kind == CheckKind.FastCom)
        {
            (SpeedSample down, SpeedSample up, string via) =
                await FastComCheck.RunAsync(proxy, token).ConfigureAwait(false);

            return SpeedVerdict.Judge(item, via.Length > 0 ? via : proxy.Name, down, up);
        }

        (SpeedSample sample, string used) = item.Kind == CheckKind.Upload
            ? await SpeedCheck.UploadAsync(item.Target, proxy, token).ConfigureAwait(false)
            : await SpeedCheck.DownloadAsync(item.Target, proxy, token).ConfigureAwait(false);

        return SpeedVerdict.Judge(item, used.Length > 0 ? used : proxy.Name, sample);
    }

    /// <summary>
    /// 名前が引けるか。<b>宛先が空なら自分のホスト名を引く。</b>
    ///
    /// 自分の名前が引けるかは、DNS への動的登録が効いているか・逆に古い登録が
    /// 残っていないかを見るのに要る。IP を変えた直後は特に外しやすい。
    /// </summary>
    private static async Task<CheckResult> RunDnsAsync(CheckItem item, CancellationToken token)
    {
        string name = item.Target.Trim();
        bool self = name.Length == 0;

        if (self) name = Environment.MachineName;

        // 端末のアプリが実際に体験する解決を見たいので、システム既定のリゾルバを使う
        DnsLookupResult result = await DnsProbe
            .QueryAsync(DnsProbe.SystemResolver, name, "A", ConnectTimeoutMs, token)
            .ConfigureAwait(false);

        string what = self ? $"自分（{name}）" : name;

        if (!result.Success)
            return Fail(item, $"{what} を解決できませんでした（{result.Message}）", result.ElapsedMs);

        if (result.Records.Count == 0)
            return Fail(item, $"{what} の応答はありましたが答えが 0 件でした", result.ElapsedMs);

        string addresses = string.Join(", ", result.Records.Take(3).Select(r => r.Value));

        return Pass(item, $"{what} → {addresses}", result.ElapsedMs);
    }

    /// <summary>
    /// ブラウザで開いて、人の判定を待つ。
    ///
    /// ログインが要る・証明書を選ぶ・JavaScript で描画される、といったページは
    /// HTTP で叩いても意味のある答えが返らない。<b>開くところまでを肩代わりして、
    /// 合否は人に付けてもらう。</b>
    ///
    /// 既定のブラウザで開くので、<b>Windows のプロキシ設定が効く</b>。
    /// 試験タブで選んだプロキシとは別物なので、その断りも書いておく。
    /// </summary>
    internal static CheckResult OpenForPerson(CheckItem item, ProxyChoice? proxy = null, string? proxyError = null)
    {
        string url = item.Target.Trim();

        if (url.Length == 0)
            return Skip(item, "開く URL が空です");

        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Fail(item, $"ブラウザで開けませんでした: {ex.Message}", 0);
        }

        string what = item.Expect.Length > 0 ? $"「{item.Expect}」を確認してください。" : "";

        // どのプロキシで開いたのかを必ず添える。証跡に「どちらで見たか」が残らないと意味がない
        string through = proxy is null
            ? "（既定のブラウザなので Windows のプロキシ設定に従います）"
            : proxyError is null
                ? $"（Windows のプロキシ設定を「{proxy.ShortName}」に切り替えて開いています）"
                : $"（プロキシを切り替えられませんでした: {proxyError}。いまの設定のまま開いています）";

        return new CheckResult(item.Name, item.Kind, item.Target, proxy?.ShortName ?? "",
                               CheckVerdict.AwaitingPerson,
                               $"ブラウザで開きました。{what}{through}");
    }

    /// <summary>
    /// Teams は <b>HTTPS がプロキシ越しに通るか</b>と <b>音声の UDP が通るか</b>で判定する。
    /// 名前解決も見るが<b>合否には使わない</b> — プロキシ環境では名前を引くのはプロキシ側で、
    /// この PC で引けないのが普通だから（引けなくても Teams は使える）。
    ///
    /// <b>署名・チャット・在席は HTTPS なのでプロキシを通る。</b>だから選ばれた
    /// プロキシごとに試す。素の TCP で繋ぐとプロキシを迂回してしまい、
    /// 実際の Teams と違う経路を見ることになる。
    ///
    /// 一方<b>音声・映像の UDP はプロキシを通らない</b>ので、どのプロキシでも同じ経路。
    /// 同じ数字が並ぶが、それぞれの行を単独で読めるように毎回測る。
    /// </summary>
    private static async Task<CheckResult> RunTeamsAsync(
        CheckItem item, ProxyChoice proxy, TeamsEndpoints teams, CancellationToken token)
    {
        var notes = new List<string>();

        DnsLookupResult dns = await DnsProbe
            .QueryAsync(DnsProbe.SystemResolver, teams.WebHost, "A", ConnectTimeoutMs, token)
            .ConfigureAwait(false);

        string via = proxy.Name;

        // 名前解決は<b>手がかりであって合否ではない</b>（2026-08-19 ユーザー指示）。
        // プロキシを使う環境では宛先の名前を引くのはプロキシの仕事で、
        // この PC では引けないのが普通。引けないだけで不合格にすると、
        // 実際には Teams が使える環境が落ちる。
        // 本当に繋がらなければ、この後の HTTPS と UDP で必ず落ちる。
        bool resolved = dns.Success && dns.Records.Count > 0;

        notes.Add(resolved
            ? "名前解決 ○"
            : $"名前解決 ✕（この PC では {teams.WebHost} を引けず。プロキシ経由なら問題ありません）");

        // ここはプロキシを通す。応答コードが何であれ、返ってきた時点で経路は通っている
        // （Teams はサインイン画面へ飛ばすので 200 とは限らない）
        (HttpOutcome web, double webMs, string used) = await HttpCheck
            .RunAsync($"https://{teams.WebHost}/", proxy, token).ConfigureAwait(false);

        if (used.Length > 0) via = used;

        if (web.Error is { } webError)
        {
            // 名前も引けていなければ、その事実も添える（切り分けの手がかりになる）
            string name = resolved ? "" : $"／{teams.WebHost} の名前もこの PC では引けていません";

            return Fail(item, $"HTTPS 443 に繋がりません（{webError}）{name}", webMs, via);
        }

        notes.Add($"HTTPS 443 ○（HTTP {web.StatusCode}）");

        // 宛先を書いてあれば、そこを音声のリレーとして使う（2026-08-19 ユーザー指示）。
        // 既定のリレー名は社内 DNS で引けないことがある（実機で SERVFAIL を確認）。
        // UDP はプロキシを通らないので、引けない限り確かめようがない —
        // 通話中の相手 IP をそのまま書いてもらえば、その道で確かめられる。
        // host:port の形なら、そのポートだけを見る
        (string relayHost, int onePort) = ParseRelay(item.Target, teams);

        IReadOnlyList<int> ports = onePort > 0 ? [onePort] : teams.Ports;

        // ここからが本題。ここが塞がれていると通話だけできない
        var blocked = new List<int>();
        var reasons = new List<string>();
        bool anySent = false;
        double udpMs = 0;

        foreach (int port in ports)
        {
            StunOutcome stun = await StunProbe
                .RunAsync(relayHost, port, UdpTimeoutMs, token).ConfigureAwait(false);

            udpMs += stun.ElapsedMs;

            if (stun.Reachable)
            {
                notes.Add($"UDP {port} ○");
                if (stun.SeenAddress is { } seen) notes.Add($"外から見えるアドレス {seen.Address}");

                // 通る道が分かったので、その道で通話品質まで測る。
                // ICMP ではなく音声が実際に使う UDP で測ることに意味がある
                RttStatistics stats = await StunProbe.MeasureAsync(
                    relayHost, port, QualitySamples, QualityIntervalMs, UdpTimeoutMs, token)
                    .ConfigureAwait(false);

                (bool acceptable, string quality) = CallQuality.Judge(stats);
                notes.Add(quality);
                notes.Add("音声の UDP はプロキシを通りません");

                string detail = string.Join(" / ", notes);

                // 目安を割っていても通話はできる。不合格ではなく注意にする
                return acceptable
                    ? Pass(item, detail, udpMs, via)
                    : Warn(item, detail, udpMs, via);
            }

            blocked.Add(port);
            anySent |= stun.Sent;

            // 理由は捨てない。「名前を引けない」と「投げたが返らない」は別物で、
            // まとめて「応答がありません」と書くと切り分けができない
            // （2026-08-19: 通話はできているのに不合格になる、と報告された）
            if (stun.Problem is { Length: > 0 } why) reasons.Add($"UDP {port}: {why}");
        }

        string summary = $"（{string.Join(" / ", notes)}）"
                         + (reasons.Count > 0 ? $"　{string.Join(" / ", reasons.Distinct())}" : "");

        // 1 バイトも送れていないなら「塞がれている」とは言えない。
        // プロキシ環境ではリレーの名前をこの PC では引けないことがあり、
        // それでも Teams 本体は通話できる。確かめられなかったものを不合格にしない
        if (!anySent)
        {
            return Warn(item,
                        $"音声の UDP を確かめられませんでした。{relayHost} へ問い合わせを送れていません"
                        + "（この PC で名前を引けないなど）。通話ができているなら差し支えありません。"
                        + $"宛先の欄に、通話中の相手のアドレスを書くとその道で確かめられます{summary}",
                        udpMs, via);
        }

        return Fail(item,
                    $"{relayHost} の UDP {string.Join("・", blocked)} のいずれも応答がありません。"
                    + $"通話の音声が通りません{summary}",
                    udpMs, via);
    }

    /// <summary>
    /// Teams の宛先欄。空なら既定のリレー、書いてあればそれを使う
    /// （<c>host</c> か <c>host:port</c>。ポートを書けばその 1 本だけを見る）。
    /// </summary>
    private static (string Host, int Port) ParseRelay(string? target, TeamsEndpoints teams)
    {
        string text = (target ?? "").Trim();

        if (text.Length == 0) return (teams.RelayHost, 0);

        (string host, int port) = CheckListParser.SplitTarget(text, CheckKind.Tcp);

        // SplitTarget は種類ごとの既定ポートを足すので、書かれていないときは 0 に戻す
        return (host, text.Contains(':', StringComparison.Ordinal) ? port : 0);
    }

    private static CheckResult Pass(CheckItem item, string detail, double ms, string proxy = "")
        => new(item.Name, item.Kind, item.Target, proxy, CheckVerdict.Pass, detail, ms);

    private static CheckResult Fail(CheckItem item, string detail, double ms, string proxy = "")
        => new(item.Name, item.Kind, item.Target, proxy, CheckVerdict.Fail, detail, ms);

    private static CheckResult Warn(CheckItem item, string detail, double ms, string proxy = "")
        => new(item.Name, item.Kind, item.Target, proxy, CheckVerdict.Warn, detail, ms);

    private static CheckResult Skip(CheckItem item, string reason)
        => new(item.Name, item.Kind, item.Target, "", CheckVerdict.Skipped, reason);
}
