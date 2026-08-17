using System.Globalization;
using System.Text.Json;

namespace NetworkToys.Core.Wireless;

/// <summary>
/// C9800 の RESTCONF 応答をほどく小物。
///
/// APIC と違い、RESTCONF は<b>コンテナごとに形が違う</b>ので、
/// 1 種類の受け皿（ACI の <c>AciMo</c> にあたるもの）は作らない。
/// 代わりに「封筒をほどく」「候補名を順に見る」の 2 つだけを用意する。
///
/// <b>リーフ名は IOS-XE の版で動く。</b>そのため値の取り出しは必ず候補を並べて渡し、
/// どれにも当たらなければ<b>空文字</b>を返す（例外にしない。1 つの思い違いで
/// 一覧が丸ごと消える方が困る）。実機で外れたら候補を 1 つ足せば直る。
/// </summary>
public static class WlcYang
{
    /// <summary>
    /// 封筒をほどいて中の並びを返す。
    ///
    /// RESTCONF の応答は <c>{"モジュール:リスト":[ … ]}</c> の形だが、
    /// <b>モジュール接頭辞は版や augment で変わる</b>ので名前で照合しない。
    /// 根の最初の配列（またはオブジェクト）をそのまま中身とみなす。
    ///
    /// <b>空（204 No Content）は 0 件であって失敗ではない。</b>
    /// 壊れた JSON も 0 件にする（読めない応答のために取得全体を失わない）。
    /// </summary>
    public static IReadOnlyList<JsonElement> Rows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array) return Clone(root);
            if (root.ValueKind != JsonValueKind.Object) return [];

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array) return Clone(property.Value);

                // キー付きで 1 件だけ引いたときはオブジェクトが返る
                if (property.Value.ValueKind == JsonValueKind.Object) return [property.Value.Clone()];
            }

            return [];
        }
    }

    /// <summary>
    /// 候補を順に見て、最初に取れた値を返す。無ければ空文字。
    /// 候補は <c>"device-detail/static-info/board-data/wtp-serial-num"</c> のように
    /// <c>/</c> で下れる（途中が配列なら先頭を見る）。
    /// </summary>
    public static string First(JsonElement node, params string[] paths)
    {
        foreach (string path in paths)
        {
            if (Find(node, path) is { } found && Scalar(found) is { Length: > 0 } value)
                return value;
        }

        return "";
    }

    /// <summary>
    /// 数値で欲しいところ用。<b>数値でも文字列でも受ける</b>
    /// （版によって <c>"-58"</c> と <c>-58</c> が混ざる）。読めなければ null。
    /// </summary>
    public static int? Int(JsonElement node, params string[] paths)
    {
        string value = First(node, paths);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>候補の道をたどる。無ければ null。</summary>
    private static JsonElement? Find(JsonElement node, string path)
    {
        JsonElement current = node;

        foreach (string name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                JsonElement.ArrayEnumerator items = current.EnumerateArray();
                if (!items.MoveNext()) return null;

                current = items.Current;
            }

            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(name, out JsonElement next)) return null;

            current = next;
        }

        return current;
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    /// <summary><see cref="JsonDocument"/> を閉じた後も使えるように複製する。</summary>
    private static IReadOnlyList<JsonElement> Clone(JsonElement array)
    {
        var rows = new List<JsonElement>();

        foreach (JsonElement item in array.EnumerateArray())
            rows.Add(item.Clone());

        return rows;
    }
}
