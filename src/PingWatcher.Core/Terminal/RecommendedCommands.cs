namespace PingWatcher.Core.Terminal;

/// <summary>
/// 収集タブの初期値。
///
/// 並びは <b>素性 → 設定 → インターフェイス → 近接/L2 → L3 → 資源 → ログ</b>。
/// 時刻を最初に取るのは、以降の出力に出る時刻を読むのに要るから。
/// ログを最後にするのは、いちばん長く、途中で打ち切られても他が揃うようにするため。
///
/// <b>注釈はコマンドの上の行に <c>!</c> で書く。</b>行内注釈にすると
/// <c>show run | include !</c> のような正当なコマンドを壊す。
///
/// 機種違いのコマンドは混ぜない（<c>% Invalid input</c> の山で出力が濁るだけ）。
/// 代わりに機種ごとの定数を用意して、画面のコンボで丸ごと差し替える。
/// <c>show tech-support</c> は入れない（数分・数万行で上限に引っかかるだけ）。
/// </summary>
public static class RecommendedCommands
{
    public const string Ios = """
        ! ---- 素性 ----
        ! いまの機器の時刻。以降の出力とログの時刻を読むのに要る
        show clock
        ! 機種・IOS 版・稼働時間・前回の再起動理由
        show version
        ! 型番とシリアル(保守の照会と部材手配に使う)
        show inventory

        ! ---- 設定 ----
        ! 現在の設定。証跡の本体
        show running-config

        ! ---- インターフェイス ----
        ! IP と up/down の一覧。まずここを見る
        show ip interface brief
        ! 速度・Duplex・VLAN・状態(Catalyst 系)
        show interfaces status
        ! ポートの説明。どこに何が刺さっているか
        show interfaces description
        ! CRC・入力エラー。物理の疑いはここで裏が取れる
        show interfaces counters errors

        ! ---- 近接と L2 ----
        ! つながっている相手(機種と IP まで分かる)
        show cdp neighbors detail
        ! CDP を出さない相手用
        show lldp neighbors detail
        ! どの MAC がどのポートに居るか(大きな機器では数千行になる)
        show mac address-table
        ! VLAN の割り当て
        show vlan brief
        ! STP の役割と TC 回数。ループ疑いの入口
        show spanning-tree summary
        ! ポートチャネルの状態
        show etherchannel summary

        ! ---- L3 ----
        ! 経路表
        show ip route
        ! ARP。IP と MAC の対応
        show ip arp

        ! ---- 資源と環境 ----
        ! CPU の上位(全プロセスは出さない)
        show processes cpu sorted
        ! 電源・ファン・温度
        show environment all

        ! ---- ログ(いちばん長いので最後) ----
        show logging

        ! ---- 要るときだけ先頭の ! を外す ----
        ! 起動時設定。保存漏れを見るときだけ(出力が倍になる)
        ! show startup-config
        ! PoE の給電状況(PoE スイッチのみ)
        ! show power inline
        ! ルーティングプロトコルを使っている環境
        ! show ip ospf neighbor
        ! show ip bgp summary
        ! 冗長構成を組んでいる環境
        ! show standby brief

        """;

    public const string Asa = """
        ! ---- 素性 ----
        show clock
        show version
        ! 現在の設定
        show running-config

        ! ---- インターフェイス ----
        show interface ip brief
        ! ---- L3 ----
        show route
        ! ---- 冗長 ----
        show failover
        ! ---- 資源 ----
        ! コネクション数。セッション溢れの確認
        show conn count
        show cpu usage
        show memory

        ! ---- ログ(バッファが大きい機器では長くなる) ----
        show logging

        """;

    public const string NxOs = """
        ! ---- 素性 ----
        show clock
        show version
        show running-config

        ! ---- インターフェイス ----
        show interface brief
        ! ---- 近接 ----
        show cdp neighbors detail
        ! ---- L3 ----
        show ip route vrf all
        ! ---- 冗長と束ね ----
        show vpc
        show port-channel summary
        ! ---- 筐体 ----
        show module

        ! ---- ログ(件数を絞る) ----
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
