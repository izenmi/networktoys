using NetworkToys.Core.Design;
using NetworkToys.Core.Fabric;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// APIC の応答の読み取り。実機も CI も APIC を持たないので、
/// <b>ここが ACI タブの正しさの拠り所</b>になる（偽 Cisco 機器での検査と同じ位置づけ）。
/// 見本の JSON は APIC が返す形をそのまま縮めたもの。
/// </summary>
public class AciCatalogTests
{
    // ===== 見本 =====

    private const string FaultsJson = """
        {"totalCount":"2","imdata":[
          {"faultInst":{"attributes":{
            "severity":"critical","code":"F0532","ack":"no",
            "dn":"topology/pod-1/node-101/sys/phys-[eth1/1]/phys/fault-F0532",
            "descr":"Port is down, reason: sfp-missing","created":"2026-08-17T09:00:00.000+09:00",
            "lastTransition":"2026-08-17T09:05:00.000+09:00"}}},
          {"faultInst":{"attributes":{
            "severity":"warning","code":"F1298","ack":"yes",
            "dn":"uni/tn-Prod/fault-F1298","descr":"Contract not resolved",
            "created":"2026-08-16T22:10:00.000+09:00","lastTransition":"2026-08-16T22:10:00.000+09:00"}}}
        ]}
        """;

    private const string PortsJson = """
        {"totalCount":"3","imdata":[
          {"l1PhysIf":{"attributes":{
            "dn":"topology/pod-1/node-101/sys/phys-[eth1/1]","id":"eth1/1",
            "adminSt":"up","usage":"epg","speed":"inherit"},
           "children":[{"ethpmPhysIf":{"attributes":{
            "operSt":"up","operSpeed":"10G","operStQual":"up",
            "lastLinkStChg":"2026-08-10T11:22:33.000+09:00"}}}]}},
          {"l1PhysIf":{"attributes":{
            "dn":"topology/pod-1/node-101/sys/phys-[eth1/2]","id":"eth1/2",
            "adminSt":"up","usage":"epg","speed":"inherit"},
           "children":[{"ethpmPhysIf":{"attributes":{
            "operSt":"down","operSpeed":"unknown","operStQual":"sfp-absent"}}}]}},
          {"l1PhysIf":{"attributes":{
            "dn":"topology/pod-1/node-102/sys/phys-[eth1/9]","id":"eth1/9",
            "adminSt":"down","usage":"discovery","speed":"inherit"},
           "children":[{"ethpmPhysIf":{"attributes":{"operSt":"down","operStQual":"admin-down"}}}]}}
        ]}
        """;

    private const string EpgsJson = """
        {"totalCount":"2","imdata":[
          {"fvAEPg":{"attributes":{"dn":"uni/tn-Prod/ap-Shop/epg-Web","name":"Web"},
           "children":[
             {"fvRsBd":{"attributes":{"tnFvBDName":"BD-Web"}}},
             {"fvRsDomAtt":{"attributes":{"tDn":"uni/phys-PhysDom"}}},
             {"fvRsPathAtt":{"attributes":{
               "tDn":"topology/pod-1/paths-101/pathep-[eth1/1]","encap":"vlan-100","mode":"regular"}}},
             {"fvRsPathAtt":{"attributes":{
               "tDn":"topology/pod-1/protpaths-103-104/pathep-[VPC-Web]","encap":"vlan-100","mode":"untagged"}}}]}},
          {"fvAEPg":{"attributes":{"dn":"uni/tn-Prod/ap-Shop/epg-Db","name":"Db"},
           "children":[{"fvRsBd":{"attributes":{"tnFvBDName":"BD-Db"}}}]}}
        ]}
        """;

    private const string EndpointsJson = """
        {"totalCount":"2","imdata":[
          {"fvCEp":{"attributes":{
            "dn":"uni/tn-Prod/ap-Shop/epg-Web/cep-00:50:56:AA:BB:CC",
            "mac":"00:50:56:AA:BB:CC","ip":"192.168.10.50","encap":"vlan-100"},
           "children":[{"fvRsCEpToPathEp":{"attributes":{
             "tDn":"topology/pod-1/paths-101/pathep-[eth1/1]"}}}]}},
          {"fvCEp":{"attributes":{
            "dn":"uni/tn-Prod/ap-Shop/epg-Db/cep-00:50:56:11:22:33",
            "mac":"00:50:56:11:22:33","ip":"0.0.0.0","encap":"vlan-200"},
           "children":[{"fvIp":{"attributes":{"addr":"192.168.20.9"}}}]}}
        ]}
        """;

    private const string NodesJson = """
        {"totalCount":"2","imdata":[
          {"fabricNode":{"attributes":{
            "dn":"topology/pod-1/node-101","name":"leaf-101","role":"leaf","fabricSt":"active",
            "model":"N9K-C93180YC-EX","serial":"FDO12345678","version":"n9000-15.2(4e)"},
           "children":[{"healthInst":{"attributes":{"cur":"98"}}}]}},
          {"fabricNode":{"attributes":{
            "dn":"topology/pod-1/node-102","name":"leaf-102","role":"leaf","fabricSt":"inactive",
            "model":"N9K-C93180YC-EX","serial":"FDO87654321","version":"n9000-15.2(4e)"}}}
        ]}
        """;

    private static IReadOnlyList<AciMo> Mos(string json) => AciMoReader.Parse(json);

    // ===== 応答をほどく =====

    [Fact]
    public void Imdata_is_flattened_with_class_names_and_children()
    {
        IReadOnlyList<AciMo> mos = Mos(PortsJson);

        Assert.Equal(3, mos.Count);
        Assert.Equal("l1PhysIf", mos[0].ClassName);
        Assert.Equal("eth1/1", mos[0]["id"]);
        Assert.Equal("up", mos[0].FirstChild("ethpmPhysIf")?["operSt"]);
    }

    [Fact]
    public void Missing_attributes_are_empty_not_an_exception()
    {
        AciMo mo = Mos(NodesJson)[1];

        // 2 台目はヘルスの子を持たない。属性名の思い違いでも落とさない
        Assert.Equal("", mo["oobMgmtAddr"]);
        Assert.Null(mo.FirstChild("healthInst"));
    }

    [Fact]
    public void Broken_pages_are_skipped_not_fatal()
    {
        IReadOnlyList<AciMo> mos = AciMoReader.Parse([FaultsJson, "{ broken", "", PortsJson]);

        Assert.Equal(5, mos.Count);
    }

    [Fact]
    public void Total_count_is_read_from_the_string_apic_returns()
    {
        Assert.Equal(2, AciMoReader.TotalCount(FaultsJson));
        Assert.Equal(-1, AciMoReader.TotalCount("{}"));
        Assert.Equal(-1, AciMoReader.TotalCount("not json"));
    }

    [Theory]
    [InlineData(0, 1)]      // 分からない・0 件でも 1 ページは投げる
    [InlineData(-1, 1)]
    [InlineData(200, 1)]
    [InlineData(201, 2)]
    [InlineData(4001, 21)]
    public void Page_count_covers_the_total(int totalCount, int expected)
        => Assert.Equal(expected, AciCatalog.PageCount(totalCount));

    // ===== DN の読み取り =====

    [Fact]
    public void Dn_segments_do_not_split_inside_brackets()
    {
        // pathep-[eth1/1] の中の / で割ると eth1 と 1] に壊れる
        Assert.Equal("eth1/1", AciDn.Port("topology/pod-1/paths-101/pathep-[eth1/1]"));
        Assert.Equal("101", AciDn.Node("topology/pod-1/paths-101/pathep-[eth1/1]"));
        Assert.Equal("101", AciDn.Node("topology/pod-1/node-101/sys/phys-[eth1/1]"));
    }

    [Fact]
    public void Vpc_paths_keep_both_node_numbers()
        => Assert.Equal("103-104", AciDn.Node("topology/pod-1/protpaths-103-104/pathep-[VPC-Web]"));

    [Theory]
    [InlineData("uni/tn-Prod/ap-Shop/epg-Web", "tn", "Prod")]
    [InlineData("uni/tn-Prod/ap-Shop/epg-Web", "epg", "Web")]
    [InlineData("uni/tn-Prod/ap-Shop/epg-Web", "bd", "")]
    public void Dn_values_are_taken_by_prefix(string dn, string prefix, string expected)
        => Assert.Equal(expected, AciDn.Value(dn, prefix));

    // ===== 行に変換する =====

    [Fact]
    public void Faults_become_rows_with_severity_and_target()
    {
        IReadOnlyList<AciFaultRow> rows = AciCatalog.ParseFaults(Mos(FaultsJson));

        Assert.Equal(2, rows.Count);
        Assert.Equal("✕ 重大", rows[0].Severity);
        Assert.Equal(SeverityKind.Alert, rows[0].SeverityKind);
        Assert.Equal("F0532", rows[0].Code);
        Assert.Equal("—", rows[0].Ack);
        Assert.Equal("済", rows[1].Ack);
    }

    [Fact]
    public void A_port_that_is_off_on_purpose_is_not_shown_as_broken()
    {
        IReadOnlyList<AciPortRow> rows = AciCatalog.ParsePorts(Mos(PortsJson));

        Assert.Equal("● 稼働", rows[0].OperState);

        // 上げてあるのに落ちている = 見たいもの
        Assert.Equal("✕ 停止", rows[1].OperState);
        Assert.Equal(SeverityKind.Alert, rows[1].OperStateKind);
        Assert.Equal("sfp-absent", rows[1].Reason);

        // わざと落としてある = 埋もれさせない
        Assert.Equal("◌ 無効", rows[2].OperState);
        Assert.Equal(SeverityKind.Muted, rows[2].OperStateKind);
        Assert.Equal("102", rows[2].Node);
    }

    [Fact]
    public void A_port_shows_which_epg_and_vlan_are_bound_to_it()
    {
        // 「どの口にどの EPG が載っているか」は EPG 側の静的パスにしかない
        IReadOnlyList<AciEpgMemberRow> members = AciCatalog.ParseEpgMembers(Mos(EpgsJson));
        IReadOnlyList<AciPortRow> rows = AciCatalog.ParsePorts(Mos(PortsJson), members);

        Assert.Equal("Web", rows[0].Epgs);
        Assert.Equal("vlan-100", rows[0].Vlans);
        Assert.Equal("タグ付き", rows[0].Modes);

        // 何も載っていない口は空。0 や「—」で埋めない
        Assert.Equal("", rows[1].Epgs);
    }

    [Fact]
    public void A_port_in_a_bundle_shows_the_port_channel_and_the_epg_bound_to_the_bundle()
    {
        // vPC のバインドは束(VPC-Web)に付く。メンバーの口には直接ぶら下がらないので、
        // 束の名前を辿らないと「この口に何も載っていない」ように見えてしまう
        const string bundles = """
            {"totalCount":"1","imdata":[
              {"pcAggrIf":{"attributes":{
                "dn":"topology/pod-1/node-103/sys/aggr-[po1]","id":"po1","name":"VPC-Web",
                "pcMode":"active","adminSt":"up"},
               "children":[
                 {"pcRsMbrIfs":{"attributes":{"tDn":"topology/pod-1/node-103/sys/phys-[eth1/10]"}}},
                 {"ethpmAggrIf":{"attributes":{"operSt":"up","operSpeed":"20G"}}}]}}
            ]}
            """;

        const string ports = """
            {"totalCount":"1","imdata":[
              {"l1PhysIf":{"attributes":{
                "dn":"topology/pod-1/node-103/sys/phys-[eth1/10]","id":"eth1/10","adminSt":"up","usage":"epg"},
               "children":[{"ethpmPhysIf":{"attributes":{"operSt":"up"}}}]}}
            ]}
            """;

        IReadOnlyList<AciEpgMemberRow> members = AciCatalog.ParseEpgMembers(Mos(EpgsJson));
        IReadOnlyList<AciPortRow> rows = AciCatalog.ParsePorts(Mos(ports), members, Mos(bundles));

        Assert.Equal(2, rows.Count);

        // メンバーの口
        Assert.Equal("eth1/10", rows[0].Interface);
        Assert.Equal("po1（active）", rows[0].PortChannel);

        // protpaths-103-104 は 103 と 104 の両方の口の話
        Assert.Equal("Web", rows[0].Epgs);
        Assert.Equal("vlan-100", rows[0].Vlans);
        Assert.Equal("タグなし", rows[0].Modes);

        // 束そのものも 1 行として出す（EPG は束の側に付くので、無いと割り当てが見えない）
        Assert.Equal("po1", rows[1].Interface);
        Assert.Equal("メンバー: eth1/10", rows[1].PortChannel);
        Assert.Equal("Web", rows[1].Epgs);
        Assert.Equal("● 稼働", rows[1].OperState);
    }

    [Fact]
    public void Ports_without_the_epg_list_still_come_out()
    {
        // EPG を渡さなくても（先に取っていなくても）ポートの一覧そのものは出る
        IReadOnlyList<AciPortRow> rows = AciCatalog.ParsePorts(Mos(PortsJson));

        Assert.Equal(3, rows.Count);
        Assert.Equal("", rows[0].Epgs);
        Assert.Equal("", rows[0].PortChannel);
    }

    [Fact]
    public void Ports_show_the_speed_they_actually_linked_at()
    {
        IReadOnlyList<AciPortRow> rows = AciCatalog.ParsePorts(Mos(PortsJson));

        Assert.Equal("10G", rows[0].Speed);

        // 設定値(speed)は "inherit" のことが多く、見ても分からない。落ちている口は「—」
        // unknown / inherit のような「決まっていない」値は速度として出さない
        Assert.Equal("—", rows[1].Speed);
        Assert.Equal("—", rows[2].Speed);
    }

    /// <summary>
    /// 稼働側にも <c>inherit</c> が入る版がある（実機で出た）。
    /// <b>設定値だろうと稼働値だろうと、決まっていない値は画面に出さない。</b>
    /// </summary>
    [Fact]
    public void 速度が_inherit_のときは出さない()
    {
        const string json = """
            {"totalCount":"1","imdata":[
              {"l1PhysIf":{"attributes":{
                "dn":"topology/pod-1/node-101/sys/phys-[eth1/3]","id":"eth1/3",
                "adminSt":"up","speed":"inherit"},
               "children":[{"ethpmPhysIf":{"attributes":{"operSt":"up","operSpeed":"inherit"}}}]}}
            ]}
            """;

        AciPortRow row = Assert.Single(AciCatalog.ParsePorts(AciMoReader.Parse(json)));

        Assert.Equal("—", row.Speed);
    }

    [Fact]
    public void Epgs_without_a_static_path_are_still_listed()
    {
        IReadOnlyList<AciEpgRow> rows = AciCatalog.ParseEpgs(Mos(EpgsJson));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Prod", rows[0].Tenant);
        Assert.Equal("Shop", rows[0].AppProfile);
        Assert.Equal("BD-Web", rows[0].BridgeDomain);
        Assert.Equal("PhysDom", rows[0].Domains);
        Assert.Equal(2, rows[0].PathCount);

        // 「EPG が無い」と「メンバーが無い」は別物
        Assert.Equal("Db", rows[1].Name);
        Assert.Equal(0, rows[1].PathCount);
    }

    [Fact]
    public void Epg_members_carry_the_node_port_and_vlan()
    {
        IReadOnlyList<AciEpgMemberRow> rows = AciCatalog.ParseEpgMembers(Mos(EpgsJson));

        Assert.Equal(2, rows.Count);
        Assert.Equal("101", rows[0].Node);
        Assert.Equal("eth1/1", rows[0].Path);
        Assert.Equal("vlan-100", rows[0].Encap);
        Assert.Equal("タグ付き", rows[0].Mode);
        Assert.Equal("103-104", rows[1].Node);
        Assert.Equal("タグなし", rows[1].Mode);
    }

    [Fact]
    public void Endpoints_fall_back_to_the_child_address_when_ip_is_unset()
    {
        IReadOnlyList<AciEndpointRow> rows = AciCatalog.ParseEndpoints(Mos(EndpointsJson));

        Assert.Equal("192.168.10.50", rows[0].Ip);
        Assert.Equal("Web", rows[0].Epg);
        Assert.Equal("101", rows[0].Node);
        Assert.Equal("eth1/1", rows[0].Path);

        // 0.0.0.0 は「まだ分からない」の意味。子に本当のアドレスが居る
        Assert.Equal("192.168.20.9", rows[1].Ip);
    }

    [Fact]
    public void Health_reads_the_child_score_and_says_when_there_is_none()
    {
        IReadOnlyList<AciHealthRow> rows = AciCatalog.ParseHealth("ノード", Mos(NodesJson));

        Assert.Equal("98", rows[0].ScoreText);
        Assert.Equal("● 良好", rows[0].State);

        // スコアが無いのと 0 点は違う
        Assert.Equal("—", rows[1].ScoreText);
        Assert.Equal(SeverityKind.Muted, rows[1].StateKind);
    }

    [Fact]
    public void Node_health_is_read_from_topsystem_which_is_where_it_actually_hangs()
    {
        // ノードのヘルスは fabricNode ではなく sys(topSystem)にぶら下がる。
        // dn がそのままポートの絞り込みに使える形(…/sys)なのも topSystem の側
        const string json = """
            {"totalCount":"2","imdata":[
              {"topSystem":{"attributes":{
                "dn":"topology/pod-1/node-101/sys","name":"LF-101","role":"leaf","id":"101"},
               "children":[{"healthInst":{"attributes":{"cur":"95"}}}]}},
              {"topSystem":{"attributes":{
                "dn":"topology/pod-1/node-1/sys","name":"apic1","role":"controller","id":"1"}}}
            ]}
            """;

        IReadOnlyList<AciHealthRow> rows = AciCatalog.ParseHealth("ノード", Mos(json));

        Assert.Equal("LF-101", rows[0].Name);
        Assert.Equal("95", rows[0].ScoreText);
        Assert.Equal("● 良好", rows[0].State);

        // ヘルスを持たないものも一覧から落とさない（落とすとノードを選べなくなる）
        Assert.Equal("apic1", rows[1].Name);
        Assert.Equal("—", rows[1].ScoreText);

        Assert.Equal("101", AciDn.Value(Mos(json)[0]["dn"], "node"));
    }

    [Fact]
    public void Fabric_config_keeps_model_serial_and_version()
    {
        IReadOnlyList<AciConfigRow> rows = AciCatalog.ParseFabricConfig(Mos(NodesJson));

        Assert.Equal("リーフ", rows[0].Kind);
        Assert.Equal("leaf-101", rows[0].Name);
        Assert.Equal("pod-1", rows[0].Parent);
        Assert.Equal("● 稼働", rows[0].State);
        Assert.Equal("N9K-C93180YC-EX / FDO12345678 / n9000-15.2(4e)", rows[0].Note);
        Assert.Equal("✕ 停止", rows[1].State);
    }

    [Fact]
    public void Tenant_config_names_the_class_in_japanese()
    {
        const string json = """
            {"totalCount":"2","imdata":[
              {"fvTenant":{"attributes":{"dn":"uni/tn-Prod","name":"Prod","descr":"本番"}}},
              {"fvBD":{"attributes":{"dn":"uni/tn-Prod/BD-Web","name":"BD-Web"}}}
            ]}
            """;

        IReadOnlyList<AciConfigRow> rows = AciCatalog.ParseTenantConfig(Mos(json));

        Assert.Equal("テナント", rows[0].Kind);
        Assert.Equal("本番", rows[0].Note);
        Assert.Equal("ブリッジドメイン", rows[1].Kind);
        Assert.Equal("Prod", rows[1].Parent);
    }

    [Fact]
    public void Vlan_pool_ranges_are_shown_because_that_is_what_is_asked()
    {
        const string json = """
            {"totalCount":"1","imdata":[
              {"fvnsVlanInstP":{"attributes":{"dn":"uni/infra/vlanns-[Pool]-static","name":"Pool",
                "allocMode":"static"},
               "children":[
                 {"fvnsEncapBlk":{"attributes":{"from":"vlan-100","to":"vlan-199"}}},
                 {"fvnsEncapBlk":{"attributes":{"from":"vlan-300","to":"vlan-300"}}}]}}
            ]}
            """;

        IReadOnlyList<AciConfigRow> rows = AciCatalog.ParseInterfacePolicy(Mos(json));

        Assert.Equal("VLAN プール", rows[0].Kind);
        Assert.Equal("vlan-100〜vlan-199, vlan-300", rows[0].Note);
    }

    [Fact]
    public void Log_rows_take_the_target_from_whichever_field_is_filled()
    {
        const string json = """
            {"totalCount":"2","imdata":[
              {"faultRecord":{"attributes":{"severity":"major","created":"2026-08-17T01:00:00.000+09:00",
                "affected":"topology/pod-1/node-101/sys","descr":"Link down"}}},
              {"eventRecord":{"attributes":{"severity":"info","created":"2026-08-17T02:00:00.000+09:00",
                "dn":"uni/tn-Prod","descr":"Config applied"}}}
            ]}
            """;

        IReadOnlyList<AciLogRow> rows = AciCatalog.ParseLog("履歴", Mos(json));

        Assert.Equal("topology/pod-1/node-101/sys", rows[0].Target);
        Assert.Equal("uni/tn-Prod", rows[1].Target);
        Assert.Equal("履歴", rows[0].Kind);
    }

    // ===== 文字起こし =====

    [Theory]
    [InlineData("critical", SeverityKind.Alert)]
    [InlineData("major", SeverityKind.Alert)]
    [InlineData("minor", SeverityKind.Notice)]
    [InlineData("warning", SeverityKind.Notice)]
    [InlineData("info", SeverityKind.Ok)]
    [InlineData("", SeverityKind.Muted)]
    public void Severity_is_mapped_to_one_of_four_levels(string severity, SeverityKind expected)
        => Assert.Equal(expected, AciCatalog.DescribeSeverity(severity).Kind);

    [Fact]
    public void Unknown_values_are_shown_as_they_came()
    {
        Assert.Equal("brand-new", AciCatalog.DescribeSeverity("brand-new").Text);
        Assert.Equal("brand-new", AciCatalog.DescribeUsage("brand-new"));
        Assert.Equal("brand-new", AciCatalog.DescribeOperState("brand-new", "up").Text);
    }

    [Theory]
    [InlineData(100, "● 良好")]
    [InlineData(95, "● 良好")]
    [InlineData(94, "⊘ 注意")]
    [InlineData(80, "⊘ 注意")]
    [InlineData(79, "✕ 不良")]
    [InlineData(-1, "—")]
    public void Health_score_bands(int score, string expected)
        => Assert.Equal(expected, AciCatalog.DescribeHealth(score).Text);

    [Theory]
    [InlineData("critical", 0)]
    [InlineData("warning", 3)]
    [InlineData("nonsense", 6)]
    public void Severity_sorts_by_weight_not_by_spelling(string severity, int expected)
        => Assert.Equal(expected, AciCatalog.SeverityRank(severity));

    // ===== 問い合わせ先とログイン =====

    [Fact]
    public void Class_paths_scope_to_a_node_when_asked()
    {
        Assert.Equal("/api/node/class/faultInst.json", AciCatalog.ClassPath("faultInst"));

        Assert.Equal("/api/node/class/faultInst.json?" + AciCatalog.FaultFilter,
                     AciCatalog.ClassPath("faultInst", AciCatalog.FaultFilter));

        // ポートは必ず絞る。全ファブリックの l1PhysIf は数万行になる
        Assert.Equal("/api/node/class/topology/pod-1/node-101/sys/l1PhysIf.json?rsp-subtree=children",
                     AciCatalog.ClassPath("l1PhysIf", "rsp-subtree=children", "topology/pod-1/node-101/sys"));
    }

    [Fact]
    public void Page_parameters_join_with_the_right_separator()
    {
        Assert.Equal("/api/node/class/fvCEp.json?page=0&page-size=200",
                     AciCatalog.PagePath("/api/node/class/fvCEp.json", 0));

        Assert.Equal("/api/node/class/fvCEp.json?rsp-subtree=children&page=2&page-size=200",
                     AciCatalog.PagePath("/api/node/class/fvCEp.json?rsp-subtree=children", 2));
    }

    [Theory]
    [InlineData("https://apic.example.jp/", "apic.example.jp")]
    [InlineData("http://10.1.1.1", "10.1.1.1")]
    [InlineData("  10.1.1.1  ", "10.1.1.1")]
    public void Host_is_accepted_however_it_was_typed(string typed, string expected)
        => Assert.Equal(expected, AciCatalog.NormalizeHost(typed));

    [Theory]
    [InlineData("", "admin", "admin")]
    [InlineData("RADIUS", "admin", @"apic:RADIUS\admin")]
    public void Login_name_carries_the_domain_when_there_is_one(string domain, string user, string expected)
        => Assert.Equal(expected, AciCatalog.LoginName(domain, user));

    [Fact]
    public void Login_response_gives_the_token_and_its_lifetime()
    {
        const string json = """
            {"totalCount":"1","imdata":[{"aaaLogin":{"attributes":{
              "token":"GhostToken","refreshTimeoutSeconds":"600","maximumLifetimeSeconds":"86400"}}}]}
            """;

        (string token, int seconds) = AciCatalog.ParseLogin(json);

        Assert.Equal("GhostToken", token);
        Assert.Equal(600, seconds);
    }

    [Fact]
    public void A_login_that_cannot_be_read_gives_no_token()
        => Assert.Equal("", AciCatalog.ParseLogin("""{"imdata":[]}""").Token);

    [Fact]
    public void Password_never_appears_outside_the_login_body()
    {
        string body = AciCatalog.LoginBody("admin", "s3cret");

        Assert.Contains("\"pwd\":\"s3cret\"", body, StringComparison.Ordinal);

        // 失敗の文言は応答本文を読まずに組み立てる（DN や設定を画面へ流さない）
        Assert.DoesNotContain("s3cret", AciCatalog.DescribeFailure(401), StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_is_formatted_the_way_apic_shows_it()
        => Assert.Equal("SHA256:AB:CD:EF", AciCatalog.FormatFingerprint([0xAB, 0xCD, 0xEF]));

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(503)]
    [InlineData(418)]
    public void Failures_are_described_in_japanese_with_the_code(int status)
    {
        string message = AciCatalog.DescribeFailure(status);

        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        message, StringComparison.Ordinal);
    }

    // ===== CSV =====

    [Fact]
    public void Every_table_keeps_one_column_per_header()
    {
        CsvTable[] tables =
        [
            AciCatalog.ToCsv(AciCatalog.ParseHealth("ノード", Mos(NodesJson))),
            AciCatalog.ToCsv(AciCatalog.ParseFaults(Mos(FaultsJson))),
            AciCatalog.ToCsv(AciCatalog.ParseLog("履歴", Mos(FaultsJson))),
            AciCatalog.ToCsv(AciCatalog.ParsePorts(Mos(PortsJson))),
            AciCatalog.ToCsv(AciCatalog.ParseEpgs(Mos(EpgsJson))),
            AciCatalog.ToCsv(AciCatalog.ParseEpgMembers(Mos(EpgsJson))),
            AciCatalog.ToCsv(AciCatalog.ParseEndpoints(Mos(EndpointsJson))),
            AciCatalog.ToCsv(AciCatalog.ParseFabricConfig(Mos(NodesJson))),
        ];

        foreach (CsvTable table in tables)
        {
            Assert.NotEmpty(table.Rows);
            Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
        }
    }
}
