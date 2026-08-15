namespace PastelNet.Core.Work;

public enum RouteChangeKind
{
    /// <summary>経路が消えた。</summary>
    Removed,

    /// <summary>経路が増えた。</summary>
    Added,

    /// <summary>学習元が変わった（OSPF から スタティックへ、など）。</summary>
    ProtocolChanged,

    /// <summary>ネクストホップが変わった。<b>経路が別の方向へ向いた</b>ということ。</summary>
    NextHopChanged,

    /// <summary>距離やメトリックだけが変わった。</summary>
    MetricChanged,
}

/// <param name="Prefix">宛先。</param>
/// <param name="Before">作業前の経路。</param>
/// <param name="After">作業後の経路。</param>
/// <param name="Kind">何が変わったか。</param>
/// <param name="Description">そのまま読める説明。</param>
public sealed record RouteTableChange(
    string Prefix,
    CiscoRoute? Before,
    CiscoRoute? After,
    RouteChangeKind Kind,
    string Description);

/// <param name="BeforeCount">作業前の経路数。</param>
/// <param name="AfterCount">作業後の経路数。</param>
/// <param name="Changes">変化した経路。</param>
public sealed record RouteTableSummary(int BeforeCount, int AfterCount, IReadOnlyList<RouteTableChange> Changes)
{
    public int Removed => Changes.Count(c => c.Kind == RouteChangeKind.Removed);

    public int Added => Changes.Count(c => c.Kind == RouteChangeKind.Added);

    public int Modified => Changes.Count - Removed - Added;

    public bool HasChanges => Changes.Count > 0;

    public string Headline => HasChanges
        ? $"経路 {BeforeCount} → {AfterCount} 本／消えた {Removed}・増えた {Added}・変わった {Modified}"
        : $"経路 {AfterCount} 本、変化はありません";
}

/// <summary>
/// 経路表を経路の単位で突き合わせる。
/// 行の差分ではなく集合として比べるので、<b>経過時間の違いが差分に出ない</b>。
/// </summary>
public static class RouteTableDiff
{
    public static RouteTableSummary Compare(IReadOnlyList<CiscoRoute> before, IReadOnlyList<CiscoRoute> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeByPrefix = ToLookup(before);
        var afterByPrefix = ToLookup(after);

        var changes = new List<RouteTableChange>();

        foreach ((string prefix, CiscoRoute route) in beforeByPrefix)
        {
            if (!afterByPrefix.TryGetValue(prefix, out CiscoRoute? current))
            {
                changes.Add(new RouteTableChange(prefix, route, null, RouteChangeKind.Removed,
                    $"{prefix} への経路が無くなりました（作業前: {route.Protocol} 経由 {route.NextHopText}）。"));
                continue;
            }

            if (Describe(route, current) is { } change)
                changes.Add(change);
        }

        foreach ((string prefix, CiscoRoute route) in afterByPrefix)
        {
            if (!beforeByPrefix.ContainsKey(prefix))
            {
                changes.Add(new RouteTableChange(prefix, null, route, RouteChangeKind.Added,
                    $"{prefix} への経路が増えました（{route.Protocol} 経由 {route.NextHopText}）。"));
            }
        }

        // 消えた経路を最初に。次に向き先が変わったもの
        return new RouteTableSummary(
            before.Count,
            after.Count,
            [.. changes.OrderBy(c => (int)c.Kind).ThenBy(c => c.Prefix, StringComparer.OrdinalIgnoreCase)]);
    }

    private static RouteTableChange? Describe(CiscoRoute before, CiscoRoute after)
    {
        if (!string.Equals(before.Protocol, after.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteTableChange(after.Prefix, before, after, RouteChangeKind.ProtocolChanged,
                $"{after.Prefix} の学習元が {before.Protocol} から {after.Protocol} に変わりました。");
        }

        if (!SameHops(before.NextHops, after.NextHops))
        {
            return new RouteTableChange(after.Prefix, before, after, RouteChangeKind.NextHopChanged,
                $"{after.Prefix} の向き先が {before.NextHopText} から {after.NextHopText} に変わりました。");
        }

        if (before.AdminDistance != after.AdminDistance || before.Metric != after.Metric)
        {
            return new RouteTableChange(after.Prefix, before, after, RouteChangeKind.MetricChanged,
                $"{after.Prefix} の距離が {before.MetricText} から {after.MetricText} に変わりました。");
        }

        return null;
    }

    /// <summary>等コスト複数経路は順序が入れ替わることがあるので、集合として比べる。</summary>
    private static bool SameHops(IReadOnlyList<string> before, IReadOnlyList<string> after)
        => before.Count == after.Count
           && before.OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(after.OrderBy(h => h, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>同じ宛先が複数出た場合は最初のものを使う。</summary>
    private static Dictionary<string, CiscoRoute> ToLookup(IReadOnlyList<CiscoRoute> routes)
    {
        var map = new Dictionary<string, CiscoRoute>(StringComparer.OrdinalIgnoreCase);

        foreach (CiscoRoute route in routes)
            map.TryAdd(route.Prefix, route);

        return map;
    }
}
