namespace NetworkToys.Core.Net;

/// <summary>
/// HTTPS で機器を叩く画面の共通の小物。
///
/// APIC は「手で打った接続先」と「証明書の指紋」を組で扱う必要がある。
/// <b>指紋の書き方が画面ごとに違うと、人が見比べられなくなる</b>ので 1 か所に置く。
/// </summary>
public static class HttpsHost
{
    /// <summary>
    /// 接続先の書き方を整える。<c>https://</c> を付けて渡されても、末尾に <c>/</c> が
    /// 付いていても同じ形にする（毎回手で打つ欄なので、揺れは受け側で吸収する）。
    /// </summary>
    public static string Normalize(string? host)
    {
        string value = (host ?? "").Trim();

        foreach (string scheme in (string[])["https://", "http://"])
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
                break;
            }
        }

        return value.TrimEnd('/');
    }

    /// <summary>
    /// 証明書の指紋を人が見比べられる形にする。機器の画面に出るのと同じ
    /// 大文字 16 進のコロン区切り。
    /// </summary>
    public static string Fingerprint(byte[]? sha256)
    {
        if (sha256 is null || sha256.Length == 0) return "";

        return "SHA256:" + Convert.ToHexString(sha256).Chunk(2)
            .Select(pair => new string(pair))
            .Aggregate((left, right) => $"{left}:{right}");
    }
}
