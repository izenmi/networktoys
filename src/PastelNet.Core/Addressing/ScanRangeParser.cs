using System.Net;

namespace PastelNet.Core.Addressing;

public sealed class ScanRangeResult
{
    public List<IPAddress> Addresses { get; } = [];

    public List<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public int Count => Addresses.Count;
}

/// <summary>
/// スキャン範囲の指定を解釈する。<see cref="IpRangeParser"/> を並べただけの薄い層だが、
/// 重複の除去と上限の管理をここでまとめて行う。
///
/// 受け付ける書き方（改行・カンマ・空白のいずれでも区切れる）:
///   192.168.1.0/24
///   192.168.1.1-192.168.1.100
///   192.168.1.1-100
///   192.168.1.10
///   # で始まる行は注釈
/// </summary>
public static class ScanRangeParser
{
    private static readonly char[] Separators = [',', '\n', '\r', ' ', '\t'];

    public static ScanRangeResult Parse(string? text, int limit = IpRangeParser.DefaultLimit)
    {
        var result = new ScanRangeResult();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var seen = new HashSet<uint>();

        foreach (string rawLine in text.Split('\n'))
        {
            // 行頭文字の判定は空白を落とす前に行う。
            // Trim() は全角スペースも削るので、先に判定しないと注釈行を取り逃がす。
            string line = rawLine.TrimEnd();

            if (line.Length == 0 || line[0] is '#' or ';' or '\'' or '　')
                continue;

            foreach (string token in line.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (result.Count >= limit)
                {
                    result.Errors.Add($"上限 {limit:N0} 件に達したため、これ以降を読み飛ばしました。");
                    return result;
                }

                IpRangeKind kind = IpRangeParser.TryExpand(token, limit - result.Count, out List<IPAddress> expanded, out string? error);

                switch (kind)
                {
                    case IpRangeKind.Expanded:
                        foreach (IPAddress address in expanded)
                            Add(result, seen, address);
                        break;

                    case IpRangeKind.Invalid:
                        result.Errors.Add(error ?? $"{token} を解釈できませんでした。");
                        break;

                    default:
                        // 単一アドレスなら受け付ける。ホスト名はスキャン対象にできない
                        if (IpRangeParser.TryParseIPv4(token, out IPAddress? single))
                            Add(result, seen, single);
                        else
                            result.Errors.Add($"{token} は IPv4 の範囲として読み取れません。");
                        break;
                }
            }
        }

        return result;
    }

    private static void Add(ScanRangeResult result, HashSet<uint> seen, IPAddress address)
    {
        if (seen.Add(IpMath.ToUInt32(address)))
            result.Addresses.Add(address);
    }
}
