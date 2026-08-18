namespace NetworkToys.Core.Verify;

/// <summary>
/// PAC を評価した結果（Windows が返すプロキシの並び）を解く。
///
/// PAC の <c>FindProxyForURL</c> は <c>"PROXY a:8080; PROXY b:8080; DIRECT"</c> のように
/// <b>候補を優先順に並べて</b>返す。Windows の WinHTTP はこれを
/// <c>"a:8080 b:8080"</c> の形（空白または <c>;</c> 区切り）に均してから寄こす。
///
/// 試験では<b>先頭の 1 つだけ</b >を使う。実際の通信も原則そこへ行くし、
/// 証跡に「どのプロキシへ行ったか」を 1 つに定めたいため。
/// </summary>
public static class PacProxy
{
    /// <summary>
    /// 並びから最初のプロキシを取り出し、<c>http://host:port</c> の形にする。
    /// 空、または <c>DIRECT</c> しか無ければ空文字（＝直接出る）。
    /// </summary>
    public static string FirstProxy(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return "";

        // 並びは「種別 アドレス」を ; で区切ったもの（"PROXY a:8080; DIRECT"）。
        // 種別の語は捨て、アドレスだけを採る
        foreach (string part in list.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] words = part.Split(
                [' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length == 0) continue;

            // DIRECT は「直接出る」。アドレスを持たない
            if (words[0].Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;

            // 種別（PROXY / SOCKS / SOCKS5 / HTTPS…）が付いていれば落とす。
            // <b>語を残すと "http://SOCKS5 host:1080" になり、URI として読めない</b>
            // （2026-08-18 に「Invalid URI」で試験が止まると報告された）。
            // 種別が無い並び（WinHTTP が均した "a:8080 b:8080"）は先頭を採る
            string address = IsKind(words[0]) ? (words.Length > 1 ? words[1] : "") : words[0];

            if (address.Length == 0 || address.Contains(' ', StringComparison.Ordinal)) continue;

            return ProxyListParser.NormalizeProxy(address);
        }

        return "";
    }

    /// <summary>PAC が書く種別の語か（アドレスではない）。</summary>
    private static bool IsKind(string word) => word.ToUpperInvariant() is
        "PROXY" or "SOCKS" or "SOCKS4" or "SOCKS5" or "HTTP" or "HTTPS" or "DIRECT";

    /// <summary>証跡に出す説明。直接出るときもそう書く（空欄だと抜けに見える）。</summary>
    public static string Describe(string resolved)
        => resolved.Length > 0 ? resolved : "直接（PAC が DIRECT を返しました）";
}
