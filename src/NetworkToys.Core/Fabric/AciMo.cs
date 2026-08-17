using System.Text;
using System.Text.Json;

namespace NetworkToys.Core.Fabric;

/// <summary>
/// APIC が返す管理オブジェクト（MO）1 個。
///
/// APIC の応答は<b>クラスが何であっても同じ形</b>をしている。
/// <code>
/// {"totalCount":"2","imdata":[{"faultInst":{"attributes":{…},"children":[…]}}]}
/// </code>
/// だからクラスごとに型を作らない（Meraki と同じ判断。属性はクラスごとに違い、
/// 版によって増減もするので、決め打ちにすると 1 項目の想定外で一覧が丸ごと落ちる）。
/// 受け皿はこの 1 種だけにして、画面の行は <see cref="AciCatalog"/> がここから組み立てる。
/// </summary>
public sealed record AciMo(
    string ClassName,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<AciMo> Children)
{
    /// <summary>属性。<b>無ければ空文字</b>（欠けている属性は珍しくないので例外にしない）。</summary>
    public string this[string name] => Attributes.TryGetValue(name, out string? value) ? value : "";

    /// <summary>そのクラスの子だけ。</summary>
    public IEnumerable<AciMo> ChildrenOf(string className)
        => Children.Where(c => string.Equals(c.ClassName, className, StringComparison.Ordinal));

    /// <summary>そのクラスの子の 1 つめ。無ければ null。</summary>
    public AciMo? FirstChild(string className) => ChildrenOf(className).FirstOrDefault();
}

/// <summary>APIC の応答（JSON）を <see cref="AciMo"/> にほどく。HTTP には触らない。</summary>
public static class AciMoReader
{
    /// <summary>
    /// 取ってきたページを順に読み、<c>imdata</c> の中身を平らに返す。
    ///
    /// <b>壊れたページは捨てる。例外にしない。</b>1 ページのために取得全体を失う方が困る
    /// （読めない行を捨てる FTP の一覧解釈と同じ判断）。
    /// </summary>
    public static IReadOnlyList<AciMo> Parse(IEnumerable<string> pages)
    {
        var mos = new List<AciMo>();

        foreach (string page in pages)
        {
            if (string.IsNullOrWhiteSpace(page)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(page);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!document.RootElement.TryGetProperty("imdata", out JsonElement imdata)) continue;
                if (imdata.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement item in imdata.EnumerateArray())
                {
                    if (Read(item) is { } mo) mos.Add(mo);
                }
            }
        }

        return mos;
    }

    /// <summary>1 ページだけ読む。</summary>
    public static IReadOnlyList<AciMo> Parse(string page) => Parse([page]);

    /// <summary>
    /// <c>totalCount</c>。ページングの続行判断に使う。
    /// APIC は<b>数値ではなく文字列</b>で返す。読めなければ -1（＝分からない）。
    /// </summary>
    public static int TotalCount(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return -1;

        try
        {
            using JsonDocument document = JsonDocument.Parse(page);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return -1;
            if (!document.RootElement.TryGetProperty("totalCount", out JsonElement total)) return -1;

            return total.ValueKind switch
            {
                JsonValueKind.String => int.TryParse(total.GetString(), out int parsed) ? parsed : -1,
                JsonValueKind.Number => total.TryGetInt32(out int number) ? number : -1,
                _ => -1,
            };
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    /// <summary>
    /// <c>{"クラス名":{"attributes":{…},"children":[…]}}</c> を 1 個ほどく。
    /// 形が違えば null（捏造しない）。
    /// </summary>
    private static AciMo? Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

            if (property.Value.TryGetProperty("attributes", out JsonElement attrs)
                && attrs.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty attribute in attrs.EnumerateObject())
                    attributes[attribute.Name] = Scalar(attribute.Value);
            }

            var children = new List<AciMo>();

            if (property.Value.TryGetProperty("children", out JsonElement kids)
                && kids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement kid in kids.EnumerateArray())
                {
                    if (Read(kid) is { } child) children.Add(child);
                }
            }

            return new AciMo(property.Name, attributes, children);
        }

        return null;
    }

    /// <summary>
    /// 属性の値。APIC は基本すべて文字列で返すが、版によって数値や真偽が混じるので
    /// そのまま受ける（Meraki の vlan と同じ用心）。
    /// </summary>
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };
}

/// <summary>
/// DN（識別名）から欲しい部分を取り出す。
///
/// DN は <c>uni/tn-Prod/ap-Web/epg-Front</c> のように区切られるが、
/// <b><c>pathep-[eth1/1]</c> のように角かっこの中に区切り文字が入る。</b>
/// 素直に <c>Split('/')</c> すると <c>eth1</c> と <c>1]</c> に割れて壊れるので、
/// 角かっこの深さを見ながら切る。
/// </summary>
public static class AciDn
{
    /// <summary>角かっこの中の <c>/</c> では切らずに、DN を区切りごとの並びにする。</summary>
    public static IReadOnlyList<string> Segments(string? dn)
    {
        if (string.IsNullOrEmpty(dn)) return [];

        var segments = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        foreach (char c in dn)
        {
            switch (c)
            {
                case '[':
                    depth++;
                    current.Append(c);
                    break;
                case ']':
                    if (depth > 0) depth--;
                    current.Append(c);
                    break;
                case '/' when depth == 0:
                    if (current.Length > 0) segments.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0) segments.Add(current.ToString());

        return segments;
    }

    /// <summary>
    /// <c>tn-Prod</c> の <c>Prod</c> のように、接頭辞の付いた区切りの中身を取る。
    /// 見つからなければ空文字。
    /// </summary>
    public static string Value(string? dn, string prefix)
    {
        string head = prefix + "-";

        foreach (string segment in Segments(dn))
        {
            if (segment.StartsWith(head, StringComparison.Ordinal))
                return Unwrap(segment[head.Length..]);
        }

        return "";
    }

    /// <summary>ノード番号。<c>node-101</c> と <c>paths-101</c>、<c>protpaths-101-102</c> を見る。</summary>
    public static string Node(string? dn)
    {
        string node = Value(dn, "node");
        if (node.Length > 0) return node;

        string paths = Value(dn, "paths");
        if (paths.Length > 0) return paths;

        // vPC は 2 台に跨がる。101-102 のまま出す（どちらか片方に決められない）
        string pair = Value(dn, "protpaths");
        return pair;
    }

    /// <summary>ポート。<c>pathep-[eth1/1]</c> や <c>phys-[eth1/1]</c> の中身。</summary>
    public static string Port(string? dn)
    {
        foreach (string prefix in (string[])["pathep", "phys", "extpathep"])
        {
            string port = Value(dn, prefix);
            if (port.Length > 0) return port;
        }

        return "";
    }

    /// <summary>角かっこで囲まれていれば外す。</summary>
    private static string Unwrap(string value)
        => value.Length >= 2 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;
}
