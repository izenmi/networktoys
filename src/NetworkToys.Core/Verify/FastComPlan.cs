using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetworkToys.Core.Verify;

/// <summary>
/// fast.com（Netflix）から測定用の URL を得るまでの<b>文字列の扱いだけ</b>を担う。
///
/// <b>公開 API ではない。</b>ブラウザで動く JS がページ内の <c>app-*.js</c> に
/// 埋め込まれたトークンを使って <c>api.fast.com</c> を叩く仕組みなので、
/// アプリから使うにはトークンを<b>正規表現で抜き出す</b>ことになる。
///
/// <b>そのため Netflix 側の書き方が変われば黙って壊れる。</b>
/// このアプリは自己診断から外部へ出さない方針なので、壊れても気づけるのは現場。
/// せめて<b>解析だけは Core に置いて CI で固める</b>ことで、
/// 「取れなかった」のか「取れたのに読めなかった」のかを切り分けられるようにする。
///
/// 通信は App 側が行い、ここは受け取った文字列を解くだけ。
/// </summary>
public static partial class FastComPlan
{
    /// <summary>fast.com のページから、トークンを含む JS のパスを拾う。</summary>
    [GeneratedRegex(@"/app-[0-9a-fA-F]+\.js", RegexOptions.None, 1000)]
    private static partial Regex ScriptPath();

    /// <summary>JS の中の <c>token:"…"</c>。引用符は " と ' の両方がありうる。</summary>
    [GeneratedRegex(@"token\s*:\s*[""']([0-9a-zA-Z]+)[""']", RegexOptions.None, 1000)]
    private static partial Regex TokenValue();

    /// <summary>ページの HTML から JS のパスを取り出す。見つからなければ null。</summary>
    public static string? FindScriptPath(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        Match match = ScriptPath().Match(html);

        return match.Success ? match.Value : null;
    }

    /// <summary>JS の中身からトークンを取り出す。見つからなければ null。</summary>
    public static string? FindToken(string? script)
    {
        if (string.IsNullOrEmpty(script)) return null;

        Match match = TokenValue().Match(script);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>測定用 URL を求める API のアドレスを組み立てる。</summary>
    /// <param name="urlCount">欲しい URL の数。並列に流すぶんだけ要る。</param>
    public static string BuildApiUrl(string token, int urlCount)
        => "https://api.fast.com/netflix/speedtest/v2"
         + $"?https=true&token={Uri.EscapeDataString(token)}&urlCount={urlCount}";

    /// <summary>
    /// API の応答（JSON）から測定用の URL を取り出す。
    ///
    /// 応答の形は <c>{"targets":[{"url":"https://…"}, …]}</c>。
    /// <b>項目の増減があっても落ちないよう、要る所だけ拾う</b>（Meraki と同じ方針）。
    /// </summary>
    public static IReadOnlyList<string> ParseTargets(string? json)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return urls;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("targets", out JsonElement targets)
                || targets.ValueKind != JsonValueKind.Array)
                return urls;

            foreach (JsonElement target in targets.EnumerateArray())
            {
                if (target.TryGetProperty("url", out JsonElement url)
                    && url.ValueKind == JsonValueKind.String
                    && url.GetString() is { Length: > 0 } text)
                {
                    urls.Add(text);
                }
            }
        }
        catch (JsonException)
        {
            // 読めない応答は「0 件」として扱う。呼ぶ側が理由を出す
        }

        return urls;
    }

    /// <summary>どこで躓いたかを、次に何を見ればよいか分かる文言にする。</summary>
    public static string DescribeFailure(FastComStep step) => step switch
    {
        FastComStep.Page => "fast.com のページを取得できませんでした。",
        FastComStep.Script => "fast.com のページからスクリプトの場所を読み取れませんでした（先方の作りが変わった可能性があります）。",
        FastComStep.Token => "スクリプトからトークンを読み取れませんでした（先方の作りが変わった可能性があります）。",
        FastComStep.Api => "測定用の URL を取得できませんでした。",
        _ => "測定用の URL が 1 件も得られませんでした。",
    };
}

/// <summary>fast.com の手順のどこまで進んだか。躓いた場所を言い分けるために持つ。</summary>
public enum FastComStep
{
    Page,
    Script,
    Token,
    Api,
    Targets,
}
