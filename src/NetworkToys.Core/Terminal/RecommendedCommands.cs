namespace NetworkToys.Core.Terminal;

/// <summary>
/// 収集タブの初期値。
///
/// 並びは <b>素性 → 設定 → インターフェイス → 近接/L2 → L3 → 資源 → ログ</b>。
/// 時刻を最初に取るのは、以降の出力に出る時刻を読むのに要るから。
/// ログを最後にするのは、いちばん長く、途中で打ち切られても他が揃うようにするため。
///
/// <b>初期値はコマンドだけを並べる</b>（ユーザー指示）。手で注釈を書きたい人のために、
/// 行頭 <c>!</c> を注釈として読み飛ばす仕組みは残してある（行の途中の <c>!</c> は
/// <c>show run | include !</c> のように正当なコマンドの一部なので解釈しない）。
///
/// 機種違いのコマンドは混ぜない（<c>% Invalid input</c> の山で出力が濁るだけ）。
/// 代わりに機種ごとの定数を用意して、画面のコンボで丸ごと差し替える。
/// <c>show tech-support</c> は入れない（数分・数万行で上限に引っかかるだけ）。
/// </summary>
public static class RecommendedCommands
{
    public const string Ios = """
        show clock
        show version
        show inventory
        show running-config
        show ip interface brief
        show interfaces status
        show interfaces description
        show interfaces counters errors
        show cdp neighbors detail
        show lldp neighbors detail
        show mac address-table
        show vlan brief
        show spanning-tree summary
        show etherchannel summary
        show ip route
        show ip arp
        show processes cpu sorted
        show environment all
        show logging
        """;

    public const string Asa = """
        show clock
        show version
        show running-config
        show interface ip brief
        show route
        show failover
        show conn count
        show cpu usage
        show memory
        show logging
        """;

    public const string NxOs = """
        show clock
        show version
        show running-config
        show interface brief
        show cdp neighbors detail
        show ip route vrf all
        show vpc
        show port-channel summary
        show module
        show logging last 200
        """;

    /// <summary>画面のコンボに出す機種の選択肢。</summary>
    public static IReadOnlyList<(string Name, string Commands)> Presets =>
    [
        ("IOS / IOS-XE", Ios),
        ("ASA", Asa),
        ("NX-OS", NxOs),
    ];
}
