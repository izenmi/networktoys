namespace PingWatcher.Core.Verify;

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

        foreach (string raw in list.Split([';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = raw.Trim();

            // PAC の書式がそのまま来ることもある（"PROXY a:8080"）
            if (entry.Equals("PROXY", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;

            // "PROXY" が付いたまま 1 語で来た場合
            if (entry.StartsWith("PROXY", StringComparison.OrdinalIgnoreCase) && entry.Length > 5)
                entry = entry[5..].Trim();

            if (entry.Length == 0) continue;

            return ProxyListParser.NormalizeProxy(entry);
        }

        return "";
    }

    /// <summary>証跡に出す説明。直接出るときもそう書く（空欄だと抜けに見える）。</summary>
    public static string Describe(string resolved)
        => resolved.Length > 0 ? resolved : "直接（PAC が DIRECT を返しました）";
}
