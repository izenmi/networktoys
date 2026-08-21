namespace NetworkToys.Core.Work;

/// <summary>貼り付けた出力の種類。何を構造化し、何を捨てるかが変わる。</summary>
public enum DeviceOutputKind
{
    /// <summary>show ip route。経路として突き合わせる。</summary>
    RouteTable,

    /// <summary>show ip interface brief。ポートの状態を突き合わせる。</summary>
    InterfaceBrief,

    /// <summary>show cdp neighbors。隣接機器と挿し口を突き合わせる。</summary>
    CdpNeighbors,

    /// <summary>show mac address-table。MAC がどのポートに見えるかを突き合わせる。</summary>
    MacTable,

    /// <summary>show run。行として突き合わせる。</summary>
    Configuration,

    /// <summary>その他。素の行差分だけ。</summary>
    PlainText,
}

/// <summary>
/// 貼り付けられた出力を、種類に応じて構造化して比べる。
///
/// 行の差分は別に取る（<see cref="SideBySideDiff"/>）。ここが返すのは
/// 「何が変わったのか」を言葉にしたもの。
/// </summary>
public static class DeviceComparison
{
    /// <summary>
    /// その種類を取るための <c>show</c>。機器から直に取ってくるときの既定として使う。
    ///
    /// <b>「そのまま比較」には決まった形が無い</b>ので空文字を返す（何を取るかは人が決める）。
    /// ここで返すのはあくまで既定で、打ち替えられる前提。
    /// </summary>
    public static string CommandFor(DeviceOutputKind kind) => kind switch
    {
        DeviceOutputKind.RouteTable => "show ip route",
        DeviceOutputKind.InterfaceBrief => "show ip interface brief",
        DeviceOutputKind.CdpNeighbors => "show cdp neighbors detail",
        DeviceOutputKind.MacTable => "show mac address-table",
        DeviceOutputKind.Configuration => "show running-config",
        _ => "",
    };

    public static DeviceCompareOutcome? Compare(DeviceOutputKind kind, string? before, string? after) => kind switch
    {
        DeviceOutputKind.RouteTable => FromRouteTable(before, after),
        DeviceOutputKind.InterfaceBrief => InterfaceBriefDiff.Compare(before, after),
        DeviceOutputKind.CdpNeighbors => CdpNeighborDiff.Compare(before, after),
        DeviceOutputKind.MacTable => MacTableDiff.Compare(before, after),

        // 設定とその他は行の差分だけで足りる
        _ => null,
    };

    /// <summary>行差分でノイズを落とすべき対象か。</summary>
    public static DiffNoiseFilter? NoiseFilterFor(DeviceOutputKind kind) => kind switch
    {
        DeviceOutputKind.Configuration => DiffNoiseFilter.CiscoConfig,
        DeviceOutputKind.RouteTable => DiffNoiseFilter.CiscoRoutes,
        _ => null,
    };

    /// <summary>
    /// 経路表の比較結果を共通の形へ移す。
    /// 既存の <see cref="RouteTableDiff"/> はそのまま残す（テストがその型を見ているため）。
    /// </summary>
    private static DeviceCompareOutcome FromRouteTable(string? before, string? after)
    {
        IReadOnlyList<CiscoRoute> beforeRoutes = CiscoRouteParser.Parse(before);
        IReadOnlyList<CiscoRoute> afterRoutes = CiscoRouteParser.Parse(after);

        if (beforeRoutes.Count == 0 && afterRoutes.Count == 0)
            return new DeviceCompareOutcome("経路を読み取れませんでした。show ip route の出力か確かめてください。", []);

        RouteTableSummary summary = RouteTableDiff.Compare(beforeRoutes, afterRoutes);

        var changes = new List<DeviceChange>(summary.Changes.Count);

        foreach (RouteTableChange change in summary.Changes)
        {
            ChangeSeverity severity = change.Kind switch
            {
                RouteChangeKind.Removed => ChangeSeverity.Critical,
                RouteChangeKind.NextHopChanged => ChangeSeverity.Critical,
                RouteChangeKind.ProtocolChanged => ChangeSeverity.Warning,
                RouteChangeKind.MetricChanged => ChangeSeverity.Info,
                _ => ChangeSeverity.Info,
            };

            string kind = change.Kind switch
            {
                RouteChangeKind.Removed => "消えた",
                RouteChangeKind.Added => "増えた",
                RouteChangeKind.NextHopChanged => "向き先が変わった",
                RouteChangeKind.ProtocolChanged => "学習元が変わった",
                _ => "距離が変わった",
            };

            changes.Add(new DeviceChange(change.Prefix, kind, change.Description, severity));
        }

        return new DeviceCompareOutcome(summary.Headline, DeviceCompareOutcome.Order(changes));
    }
}
