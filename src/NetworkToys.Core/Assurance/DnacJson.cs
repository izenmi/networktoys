using System.Globalization;
using System.Text.Json;

namespace NetworkToys.Core.Assurance;

/// <summary>
/// Catalyst Center の応答をほどく小物。
///
/// 応答は <c>{"response": …, "version": "1.0"}</c> で包まれている（配列のことも
/// オブジェクトのこともある）。<b>包みの中身だけを取り出して同じように回せる</b>ようにする。
///
/// <b>リーフ名は版で動く。</b>値の取り出しは必ず候補を並べて渡し、どれにも当たらなければ
/// <b>空文字</b>を返す（例外にしない。1 つの思い違いで一覧が丸ごと消える方が困る）。
/// ACI の <c>AciMo</c>・WLC の <c>WlcYang</c> と同じ考え方。
/// </summary>
public static class DnacJson
{
    /// <summary>
    /// 包みをほどいて中の並びを返す。オブジェクト 1 個で返ってきたら 1 件として扱う。
    /// 空・壊れた JSON は 0 件（読めない応答のために取得全体を失わない）。
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

            // 入れ物の名前は問い合わせ先で違う（response のほか、
            // Endpoint Analytics は items で返す）
            if (root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("response", out JsonElement response)
                    || root.TryGetProperty("items", out response)))
                root = response;

            if (root.ValueKind == JsonValueKind.Array) return Clone(root);
            if (root.ValueKind == JsonValueKind.Object) return [root.Clone()];

            return [];
        }
    }

    /// <summary>包みの中身を 1 個として取る（配列なら先頭）。無ければ null。</summary>
    public static JsonElement? One(string? json)
    {
        IReadOnlyList<JsonElement> rows = Rows(json);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// 候補を順に見て、最初に取れた値を返す。無ければ空文字。
    /// 候補は <c>"deviceDetails/name"</c> のように <c>/</c> で下れる（途中が配列なら先頭を見る）。
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

    /// <summary>数値で欲しいところ用。<b>数値でも文字列でも受ける</b>（版で混ざる）。</summary>
    public static int? Int(JsonElement node, params string[] paths)
    {
        string value = First(node, paths);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>時刻はミリ秒のエポックで返ることが多い。</summary>
    public static long? Long(JsonElement node, params string[] paths)
    {
        string value = First(node, paths);

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    /// <summary>候補の下にある並び（配列）を返す。無ければ空。</summary>
    public static IReadOnlyList<JsonElement> Children(JsonElement node, params string[] paths)
    {
        foreach (string path in paths)
        {
            if (Find(node, path) is { ValueKind: JsonValueKind.Array } array)
                return Clone(array);
        }

        return [];
    }

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

    private static IReadOnlyList<JsonElement> Clone(JsonElement array)
    {
        var rows = new List<JsonElement>();

        foreach (JsonElement item in array.EnumerateArray())
            rows.Add(item.Clone());

        return rows;
    }
}
