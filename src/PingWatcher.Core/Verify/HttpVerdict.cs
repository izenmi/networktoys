namespace PingWatcher.Core.Verify;

/// <summary>HTTP の応答から判定に要る分だけ取り出したもの。</summary>
/// <param name="StatusCode">応答コード。届かなかったら 0。</param>
/// <param name="FinalUrl">リダイレクトを追った後の URL。</param>
/// <param name="ServerHeader">Server ヘッダ。誰が返したかの手がかり。</param>
/// <param name="Body">本文（先頭だけ）。期待する文字列を探すのに使う。</param>
/// <param name="Error">通信そのものに失敗した理由。成功なら null。</param>
public sealed record HttpOutcome(
    int StatusCode,
    string FinalUrl,
    string ServerHeader,
    string Body,
    string? Error = null);

/// <summary>
/// HTTP の合否を決める。
///
/// <b>応答コードだけで合格にしない。</b>クラウド型のプロキシ（Zscaler など）は
/// 遮断したときに「アクセスがブロックされました」という<b>自前のページを 200 で返す</b>ことがある。
/// コードだけを見ていると、実際には目的のサイトに届いていないのに合格になる。
///
/// そこで:
/// <list type="bullet">
///   <item>「期待する文字列」が書いてあれば、本文に含まれることまで見る（いちばん確実）</item>
///   <item>書いていなくても、よくある遮断ページの文言が出ていたら不合格にする</item>
///   <item>証跡には<b>最終 URL と Server ヘッダ</b>を必ず残す（誰が返したかが後から分かる）</item>
/// </list>
/// </summary>
public static class HttpVerdict
{
    /// <summary>
    /// 遮断ページによく出る文言。<b>これだけに頼らない</b>（製品も文言も変わる）。
    /// 確実にしたい項目には「期待する文字列」を書いてもらう。
    ///
    /// クラウド型（Zscaler など）もオンプレ型（i-FILTER など）も、遮断したときに
    /// <b>自前のページを HTTP 200 で返す</b>ことがある。応答コードだけを見ていると
    /// 目的のサイトに届いていないのに合格になるので、本文にも目を通す。
    /// </summary>
    private static readonly string[] BlockedSigns =
    [
        // 日本語の製品でよく出る言い回し
        "アクセスがブロック", "アクセスはブロック", "アクセスが禁止", "アクセスできません",
        "閲覧できません", "閲覧が制限", "アクセス制限", "ポリシーにより", "許可されていません",

        // 英語
        "access denied", "access to this site", "blocked by", "web page blocked",
        "your organization", "url filtering", "not permitted",

        // 製品名がそのまま出るもの
        "zscaler", "i-filter", "デジタルアーツ",
    ];

    public static CheckResult Judge(CheckItem item, string proxyName, HttpOutcome outcome, double elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Error is { } error)
            return Fail(item, proxyName, error, elapsedMs);

        string where = Describe(outcome);

        // 2xx と 3xx を通す。3xx はリダイレクトを追い切れなかった場合に残る
        bool codeOk = outcome.StatusCode is >= 200 and < 400;

        if (!codeOk)
            return Fail(item, proxyName, $"HTTP {outcome.StatusCode}{where}", elapsedMs);

        if (item.Expect.Length > 0)
        {
            bool found = outcome.Body.Contains(item.Expect, StringComparison.OrdinalIgnoreCase);

            return found
                ? Pass(item, proxyName, $"HTTP {outcome.StatusCode}・「{item.Expect}」あり{where}", elapsedMs)
                : Fail(item, proxyName,
                       $"HTTP {outcome.StatusCode} は返りましたが「{item.Expect}」が本文にありません{where}", elapsedMs);
        }

        if (LooksBlocked(outcome.Body) is { } sign)
        {
            return Fail(item, proxyName,
                        $"HTTP {outcome.StatusCode} ですが遮断ページのようです（「{sign}」）{where}", elapsedMs);
        }

        return Pass(item, proxyName, $"HTTP {outcome.StatusCode}{where}", elapsedMs);
    }

    /// <summary>遮断ページらしい文言があればそれを返す。無ければ null。</summary>
    public static string? LooksBlocked(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;

        foreach (string sign in BlockedSigns)
        {
            if (body.Contains(sign, StringComparison.OrdinalIgnoreCase))
                return sign;
        }

        return null;
    }

    /// <summary>誰がどこから返したか。証跡としてこれが要る。</summary>
    private static string Describe(HttpOutcome outcome)
    {
        var parts = new List<string>();

        if (outcome.ServerHeader.Length > 0) parts.Add(outcome.ServerHeader);
        if (outcome.FinalUrl.Length > 0) parts.Add(outcome.FinalUrl);

        return parts.Count > 0 ? $"（{string.Join(" / ", parts)}）" : "";
    }

    private static CheckResult Pass(CheckItem item, string proxy, string detail, double ms)
        => new(item.Name, item.Kind, item.Target, proxy, CheckVerdict.Pass, detail, ms);

    private static CheckResult Fail(CheckItem item, string proxy, string detail, double ms)
        => new(item.Name, item.Kind, item.Target, proxy, CheckVerdict.Fail, detail, ms);
}
