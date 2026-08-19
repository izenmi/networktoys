using System.Text.Json;

namespace NetworkToys.Core.Verify;

/// <summary>
/// <b>HTTPS で名前を引く</b>ための組み立てと読み取り（DNS over HTTPS）。
///
/// 社内 DNS が外の名前を引けない環境（実機で <c>SERVFAIL</c> を確認）でも、
/// <b>HTTPS はプロキシを通る</b>ので名前だけは引ける。UDP はプロキシを通らないため、
/// Teams の音声を確かめるには「名前 → アドレス」をどこかで得るしかない。
///
/// <b>素の DNS（UDP 53）は使わない。</b>外向きの経路が無い環境が実際にあり
/// （8.8.8.8 へは経路が無いと報告された）、そこでは意味が無いため。
///
/// 通信そのものは画面側（<c>HttpCheck</c>）に任せ、ここは<b>URL の組み立てと
/// 応答の読み取りだけ</b>を持つ（＝CI で固められる形にする）。
/// </summary>
public static class DohLookup
{
    /// <summary>
    /// 問い合わせ先。<b>JSON で答える口</b>だけを使う（RFC 8484 のバイナリ形式は
    /// 独自ヘッダが要るぶん、プロキシ環境で通りにくい）。
    /// </summary>
    public const string GoogleResolver = "https://dns.google/resolve";

    /// <summary>問い合わせの URL。<b>A レコードだけ</b>引く（音声は IPv4 で見る）。</summary>
    public static string UrlFor(string resolver, string name)
        => $"{resolver}?name={Uri.EscapeDataString(name ?? "")}&type=A";

    /// <summary>
    /// 応答（JSON）から <b>A レコードのアドレスだけ</b>取り出す。
    /// <b>読めない応答は空で返す。例外にしない</b> — 相手の作りが変わっても、
    /// 「引けなかった」として先へ進めるようにする（遮断ページが返ることもある）。
    /// </summary>
    public static IReadOnlyList<string> ReadAddresses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Answer", out JsonElement answers)
                || answers.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var found = new List<string>();

            foreach (JsonElement answer in answers.EnumerateArray())
            {
                // type 1 が A。CNAME（5）は途中経過なので拾わない
                if (!answer.TryGetProperty("type", out JsonElement type) || type.GetInt32() != 1) continue;

                if (answer.TryGetProperty("data", out JsonElement data)
                    && data.GetString() is { Length: > 0 } address
                    && System.Net.IPAddress.TryParse(address, out _))
                {
                    found.Add(address);
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
