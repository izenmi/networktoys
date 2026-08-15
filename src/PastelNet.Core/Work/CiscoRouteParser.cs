using System.Text.RegularExpressions;

namespace PastelNet.Core.Work;

/// <param name="Prefix">宛先（10.1.1.0/24 など）。</param>
/// <param name="Protocol">学習元（O, O IA, B, S*, C, L, D EX など）。</param>
/// <param name="AdminDistance">アドミニストレイティブディスタンス。</param>
/// <param name="Metric">メトリック。</param>
/// <param name="NextHops">ネクストホップ。等コスト複数経路なら複数入る。</param>
/// <param name="Interfaces">出力インターフェース。</param>
/// <param name="RawLine">元の行。表示に使う。</param>
public sealed record CiscoRoute(
    string Prefix,
    string Protocol,
    int? AdminDistance,
    int? Metric,
    IReadOnlyList<string> NextHops,
    IReadOnlyList<string> Interfaces,
    string RawLine)
{
    public string NextHopText => NextHops.Count > 0 ? string.Join(", ", NextHops) : "直結";

    public string InterfaceText => string.Join(", ", Interfaces);

    public string MetricText => AdminDistance is { } ad && Metric is { } metric ? $"[{ad}/{metric}]" : string.Empty;
}

/// <summary>
/// Cisco IOS の <c>show ip route</c> の出力を経路として読み取る。
///
/// <b>テキストの差分では使い物にならないから構造化する。</b>出力には経過時間
/// （<c>00:15:23</c> や <c>2d10h</c>）が入っているため、設定を何も変えていなくても
/// 動的経路の行はすべて差分として出てしまう。
/// </summary>
public static partial class CiscoRouteParser
{
    public static IReadOnlyList<CiscoRoute> Parse(string? text)
    {
        var routes = new List<CiscoRoute>();

        if (string.IsNullOrWhiteSpace(text))
            return routes;

        // 直前の経路。インデントだけの継続行（等コスト複数経路）をここへ足す
        CiscoRoute? previous = null;
        var nextHops = new List<string>();
        var interfaces = new List<string>();

        void Flush()
        {
            if (previous is null) return;

            routes.Add(previous with
            {
                NextHops = [.. nextHops],
                Interfaces = [.. interfaces],
            });

            previous = null;
            nextHops.Clear();
            interfaces.Clear();
        }

        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.Length == 0 || IsNoise(line))
                continue;

            Match header = RouteHeader().Match(line);

            if (header.Success)
            {
                Flush();

                (int? ad, int? metric, string? hop, string? iface) = ParseBody(header.Groups["body"].Value);

                previous = new CiscoRoute(
                    NormalizePrefix(header.Groups["prefix"].Value),
                    NormalizeProtocol(header.Groups["code"].Value),
                    ad,
                    metric,
                    [],
                    [],
                    line.Trim());

                if (hop is not null) nextHops.Add(hop);
                if (iface is not null) interfaces.Add(iface);

                continue;
            }

            // 継続行。プレフィックスを持たず、直前の経路の別パスを表す
            if (previous is not null && ContinuationLine().IsMatch(line))
            {
                (int? _, int? _, string? hop, string? iface) = ParseBody(line.Trim());

                if (hop is not null) nextHops.Add(hop);
                if (iface is not null) interfaces.Add(iface);
            }
        }

        Flush();
        return routes;
    }

    /// <summary>経路ではない行。</summary>
    private static bool IsNoise(string line)
    {
        string trimmed = line.TrimStart();

        // 凡例、最終手段のゲートウェイ、サブネット化の説明、プロンプトやコメント
        return trimmed.StartsWith("Codes:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Gateway of last resort", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains(" is subnetted", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains(" is variably subnetted", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith('!');
    }

    /// <summary>
    /// 経路の中身を読む。
    /// <c>[110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/1</c> または
    /// <c>is directly connected, GigabitEthernet0/0</c>。
    /// <b>経過時間は読み捨てる</b>（毎回変わるので比較に使えない）。
    /// </summary>
    private static (int? Ad, int? Metric, string? NextHop, string? Interface) ParseBody(string body)
    {
        int? ad = null;
        int? metric = null;

        Match distance = Distance().Match(body);
        if (distance.Success)
        {
            ad = int.Parse(distance.Groups["ad"].Value);
            metric = int.Parse(distance.Groups["metric"].Value);
        }

        string? nextHop = null;
        Match via = Via().Match(body);
        if (via.Success)
            nextHop = via.Groups["hop"].Value;

        string? iface = null;
        Match connected = DirectlyConnected().Match(body);
        if (connected.Success)
        {
            iface = connected.Groups["iface"].Value.Trim();
        }
        else
        {
            // "via 10.0.0.1, 00:15:23, GigabitEthernet0/1" の最後の要素
            Match trailing = TrailingInterface().Match(body);
            if (trailing.Success)
                iface = trailing.Groups["iface"].Value.Trim();
        }

        return (ad, metric, nextHop, iface);
    }

    /// <summary>マスクの無い表記でも比べられるよう、書き方を揃える。</summary>
    private static string NormalizePrefix(string prefix) => prefix.Trim();

    /// <summary>"O IA" のような複数語のコードから余分な空白を落とす。</summary>
    private static string NormalizeProtocol(string code)
        => string.Join(' ', code.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    // 行頭のコード（B, O, O IA, D EX, S*, i L1 など）とプレフィックス
    [GeneratedRegex(@"^\s*(?<code>[A-Za-z](?:\*|\s+[A-Za-z0-9*]{1,2})*)\s+(?<prefix>\d{1,3}(?:\.\d{1,3}){3}(?:/\d{1,2})?)\s+(?<body>.*)$")]
    private static partial Regex RouteHeader();

    // インデントだけの継続行（[110/2] via ... で始まる）
    [GeneratedRegex(@"^\s{6,}\[\d+/\d+\]\s+via\s")]
    private static partial Regex ContinuationLine();

    [GeneratedRegex(@"\[(?<ad>\d+)/(?<metric>\d+)\]")]
    private static partial Regex Distance();

    [GeneratedRegex(@"via\s+(?<hop>\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex Via();

    [GeneratedRegex(@"is\s+directly\s+connected,\s*(?<iface>[A-Za-z][\w\-./]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectlyConnected();

    [GeneratedRegex(@",\s*(?<iface>[A-Za-z][\w\-./]*)\s*$")]
    private static partial Regex TrailingInterface();
}
