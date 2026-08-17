using System.Globalization;
using System.Text.Json;
using NetworkToys.Core.Design;
using NetworkToys.Core.Net;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Fabric;

/// <summary>ヘルスの 1 行。ファブリック全体・ノード・テナントを同じ形で並べる。</summary>
public sealed record AciHealthRow(
    string Kind,
    string Name,
    int Score,
    string ScoreText,
    string State,
    SeverityKind StateKind);

/// <summary>いま出ている障害（faultInst）の 1 行。</summary>
public sealed record AciFaultRow(
    string Severity,
    SeverityKind SeverityKind,
    string Code,
    string Target,
    string Description,
    string Created,
    string LastTransition,
    string Ack);

/// <summary>履歴（faultRecord / eventRecord）の 1 行。</summary>
public sealed record AciLogRow(
    string Time,
    string Kind,
    string Severity,
    SeverityKind SeverityKind,
    string Target,
    string Text);

/// <summary>物理ポートの 1 行。設定（l1PhysIf）と実際（ethpmPhysIf）を 1 行に畳む。</summary>
public sealed record AciPortRow(
    string Node,
    string Interface,
    string AdminState,
    string OperState,
    SeverityKind OperStateKind,
    string Speed,
    string Usage,
    string PortChannel,
    string Epgs,
    string Vlans,
    string Modes,
    string Reason,
    string LastChange,
    string Description = "")
{
    /// <summary>
    /// 並べ替え用の鍵。<b>eth1/2 は eth1/10 より前</b>に来てほしいので、
    /// 素の文字比較ではなく数字を桁で揃えたものを使う（見出しを押したときもこちらで並ぶ）。
    /// </summary>
    public string InterfaceKey => AciCatalog.PortSortKey(Node, Interface);
}

/// <summary>EPG の 1 行。</summary>
public sealed record AciEpgRow(
    string Tenant,
    string AppProfile,
    string Name,
    string BridgeDomain,
    string Domains,
    int PathCount,
    string Dn);

/// <summary>EPG に結び付いた実際の口（静的パス）の 1 行。</summary>
public sealed record AciEpgMemberRow(
    string EpgDn,
    string Epg,
    string Node,
    string Path,
    string Encap,
    string Mode);

/// <summary>エンドポイント（fvCEp）の 1 行。</summary>
public sealed record AciEndpointRow(
    string Mac,
    string Ip,
    string Tenant,
    string Epg,
    string Encap,
    string Node,
    string Path);

/// <summary>機器一覧の 1 行（<c>fabricNode</c> 1 台ぶん）。台帳として使えるだけの項目を持つ。</summary>
public sealed record AciDeviceRow(
    string Node,
    string Name,
    string Role,
    string Model,
    string Serial,
    string Version,
    string State,
    string Pod)
{
    /// <summary>並べ替え用の鍵。ノード番号は数字なので、文字のままだと 101 が 9 より前に来る。</summary>
    public string NodeKey => AciCatalog.PortSortKey(Node, "");
}

/// <summary>
/// APIC の応答を画面と CSV の行に変換する。
///
/// ここは HTTP に触らない。取ってくるのは App 側の <c>Services/ApicClient</c> で、
/// このクラスは <see cref="AciMo"/> を行にするだけなので、固定の見本でそのまま検証できる
/// （CI が唯一の実行環境というこのリポジトリでは、ここが正しさの拠り所になる）。
/// </summary>
public static class AciCatalog
{
    // ===== ヘルス =====

    /// <summary>
    /// ヘルスを読む。スコアの在り処は 2 通りある — 自分の <c>cur</c>（fabricHealthTotal）と、
    /// 子の <c>healthInst</c> の <c>cur</c>（<c>rsp-subtree-include=health</c> を付けたとき）。
    /// <b>どちらも無ければ「—」にする。0 点と混同させない。</b>
    /// </summary>
    public static IReadOnlyList<AciHealthRow> ParseHealth(string kind, IEnumerable<AciMo> mos)
    {
        var rows = new List<AciHealthRow>();

        foreach (AciMo mo in mos)
        {
            int score = Score(mo);
            (string state, SeverityKind stateKind) = DescribeHealth(score);

            rows.Add(new AciHealthRow(
                Kind: kind,
                Name: NameOf(mo),
                Score: score,
                ScoreText: score < 0 ? "—" : score.ToString(CultureInfo.InvariantCulture),
                State: state,
                StateKind: stateKind));
        }

        return rows;
    }

    private static int Score(AciMo mo)
    {
        if (Int(mo["cur"]) is { } own) return own;
        if (mo.FirstChild("healthInst") is { } health && Int(health["cur"]) is { } child) return child;

        return -1;
    }

    private static string NameOf(AciMo mo)
    {
        string name = mo["name"];
        if (name.Length > 0) return name;

        // fabricHealthTotal のように名前を持たないものは DN で見せる（捏造しない）
        return mo["dn"];
    }

    /// <summary>スコアの読み方。ACI の目安（95 以上は健全、80 未満は要確認）に合わせる。</summary>
    public static (string Text, SeverityKind Kind) DescribeHealth(int score) => score switch
    {
        < 0 => ("—", SeverityKind.Muted),
        >= 95 => ("● 良好", SeverityKind.Ok),
        >= 80 => ("⊘ 注意", SeverityKind.Notice),
        _ => ("✕ 不良", SeverityKind.Alert),
    };

    // ===== Faults =====

    public static IReadOnlyList<AciFaultRow> ParseFaults(IEnumerable<AciMo> mos)
    {
        var rows = new List<AciFaultRow>();

        foreach (AciMo mo in mos)
        {
            (string severity, SeverityKind kind) = DescribeSeverity(mo["severity"]);

            rows.Add(new AciFaultRow(
                Severity: severity,
                SeverityKind: kind,
                Code: mo["code"],
                Target: Or(mo["dn"], mo["affected"]),
                Description: mo["descr"],
                Created: mo["created"],
                LastTransition: mo["lastTransition"],
                Ack: mo["ack"] == "yes" ? "済" : "—"));
        }

        return rows;
    }

    /// <summary>
    /// 重大度。<b>知らない値は言い換えずそのまま出す</b>（Meraki と同じ決まり）。
    /// </summary>
    public static (string Text, SeverityKind Kind) DescribeSeverity(string? severity) => severity switch
    {
        "critical" => ("✕ 重大", SeverityKind.Alert),
        "major" => ("✕ 大", SeverityKind.Alert),
        "minor" => ("⊘ 小", SeverityKind.Notice),
        "warning" => ("⊘ 警告", SeverityKind.Notice),
        "info" => ("● 情報", SeverityKind.Ok),
        "cleared" => ("● 解消", SeverityKind.Ok),
        null or "" => ("—", SeverityKind.Muted),
        _ => (severity, SeverityKind.Muted),
    };

    /// <summary>重大度の重い順。並べ替えの既定に使う（文字列順では意味を成さない）。</summary>
    public static int SeverityRank(string? severity) => severity switch
    {
        "critical" => 0,
        "major" => 1,
        "minor" => 2,
        "warning" => 3,
        "info" => 4,
        "cleared" => 5,
        _ => 6,
    };

    // ===== ログ（faultRecord / eventRecord） =====

    public static IReadOnlyList<AciLogRow> ParseLog(string kind, IEnumerable<AciMo> mos)
    {
        var rows = new List<AciLogRow>();

        foreach (AciMo mo in mos)
        {
            (string severity, SeverityKind severityKind) = DescribeSeverity(mo["severity"]);

            rows.Add(new AciLogRow(
                Time: Or(mo["created"], mo["lastTransition"]),
                Kind: kind,
                Severity: severity,
                SeverityKind: severityKind,
                Target: Or(mo["affected"], mo["dn"]),
                Text: Or(mo["descr"], mo["cause"])));
        }

        return rows;
    }

    // ===== ポート =====

    /// <summary>
    /// 物理ポート。<c>l1PhysIf</c>（設定）の子に <c>ethpmPhysIf</c>（実際）が付いてくる形で引く。
    /// 子が無いときは実際の状態を「—」にする。<b>設定の値で埋めない</b> —
    /// 「上げてあるのに上がっていない」を見るための画面なので、そこを混ぜると意味が消える。
    /// </summary>
    /// <summary>
    /// 物理ポートの一覧。<b>ポートチャネル(束)そのものも同じ表に出す</b>。
    ///
    /// <paramref name="members"/> は EPG 側の静的パス。「どの口にどの EPG と VLAN が
    /// 載っているか」は EPG 側にしか無いので、突き合わせて埋める。
    /// <paramref name="bundles"/> は <c>pcAggrIf</c>。
    /// </summary>
    public static IReadOnlyList<AciPortRow> ParsePorts(
        IEnumerable<AciMo> mos,
        IEnumerable<AciEpgMemberRow>? members = null,
        IEnumerable<AciMo>? bundles = null)
    {
        IReadOnlyList<AciEpgMemberRow> paths = members is null ? [] : [.. members];
        IReadOnlyList<AciBundle> aggregates = ReadBundles(bundles);

        Dictionary<string, AciBundle> byMemberPort = new(StringComparer.OrdinalIgnoreCase);

        foreach (AciBundle bundle in aggregates)
        {
            foreach (string port in bundle.Members)
                byMemberPort[PortKey(bundle.Node, port)] = bundle;
        }

        var rows = new List<AciPortRow>();

        foreach (AciMo mo in mos)
        {
            string node = AciDn.Node(mo["dn"]);
            string port = Or(mo["id"], AciDn.Port(mo["dn"]));

            byMemberPort.TryGetValue(PortKey(node, port), out AciBundle? bundle);

            rows.Add(PortRow(mo, "ethpmPhysIf", node, port, bundle?.Label ?? "",
                             Bound(paths, node, port, bundle)));
        }

        // 束そのもの。EPG は束の側に付くので、この行が無いと割り当てが見えない
        foreach (AciBundle bundle in aggregates)
        {
            rows.Add(PortRow(bundle.Mo, "ethpmAggrIf", bundle.Node, bundle.Id,
                             bundle.Members.Count > 0 ? "メンバー: " + string.Join(", ", bundle.Members) : "",
                             Bound(paths, bundle.Node, bundle.Id, bundle)));
        }

        // 応答の順は APIC 任せ（eth1/10 が eth1/2 より前に来る）。口の番号どおりに並べる
        return [.. rows.OrderBy(r => r.InterfaceKey, StringComparer.Ordinal)];
    }

    /// <summary>
    /// ノードと口の名前から並べ替え用の鍵を作る。<b>数字は 6 桁に揃える</b>ので、
    /// eth1/2 → eth000001/000002 となり、eth1/10 より前に来る。
    /// ノードも数字なので同じ規則で先頭に置く（101 と 1001 が混ざらない）。
    /// </summary>
    public static string PortSortKey(string? node, string? port)
    {
        var text = new System.Text.StringBuilder();

        foreach (string part in new[] { node ?? "", port ?? "" })
        {
            for (int i = 0; i < part.Length;)
            {
                if (char.IsDigit(part[i]))
                {
                    int start = i;
                    while (i < part.Length && char.IsDigit(part[i])) i++;

                    text.Append(part[start..i].PadLeft(6, '0'));
                }
                else
                {
                    text.Append(char.ToUpperInvariant(part[i]));
                    i++;
                }
            }

            text.Append('\u0000');   // ノードと口の区切り。混ざらないように
        }

        return text.ToString();
    }

    private static AciPortRow PortRow(
        AciMo mo, string operClass, string node, string port, string portChannel, AciEpgMemberRow[] bound)
    {
        AciMo? actual = mo.FirstChild(operClass);
        string operSt = actual?["operSt"] ?? "";

        (string operText, SeverityKind operKind) = DescribeOperState(operSt, mo["adminSt"]);

        return new AciPortRow(
            Node: node,
            Interface: port,
            AdminState: DescribeAdminState(mo["adminSt"]),
            OperState: operText,
            OperStateKind: operKind,
            Speed: DescribeSpeed(actual),
            Usage: DescribeUsage(mo["usage"]),
            PortChannel: portChannel,
            Epgs: Join(bound.Select(m => m.Epg)),
            Vlans: Join(bound.Select(m => m.Encap)),
            Modes: Join(bound.Select(m => m.Mode)),
            Reason: actual?["operStQual"] ?? "",
            LastChange: actual?["lastLinkStChg"] ?? "",
            Description: mo["descr"]);
    }

    /// <summary>
    /// 速度。<b>設定値ではなく、いまリンクしている速度</b>を出す。
    ///
    /// 設定側（<c>l1PhysIf.speed</c>）は <c>inherit</c> のことが多く、見ても分からない。
    /// 稼働側（<c>ethpmPhysIf</c> / <c>ethpmAggrIf</c>）の <c>operSpeed</c> を読むが、
    /// <b>版によってはそこにも <c>inherit</c> や <c>unknown</c> が入る</b>ので、
    /// 中身の無い値はまとめて「—」にする（2026-08-17 に実機で inherit が出ると報告された）。
    /// 属性名の候補も値の候補も、直すときはここ 1 か所で足りる。
    /// </summary>
    private static string DescribeSpeed(AciMo? actual)
    {
        if (actual is null) return "—";

        foreach (string name in (string[])["operSpeed", "speed"])
        {
            string value = actual[name];

            if (value.Length == 0) continue;

            // 「決まっていない」を意味する値。速度として出しても読む人には何も伝わらない
            if (value is "inherit" or "unknown" or "auto" or "0" or "unspecified") continue;

            return value;
        }

        return "—";
    }

    /// <summary>その口に載っている EPG。束の口は、束の側に付いたものも自分のものとして数える。</summary>
    private static AciEpgMemberRow[] Bound(
        IReadOnlyList<AciEpgMemberRow> paths, string node, string port, AciBundle? bundle)
        => [.. paths.Where(m => Matches(m, node, port, bundle))];

    /// <summary>ポートチャネル。<c>Names</c> は EPG の静的パスが指しうる名前（番号と名前の両方）。</summary>
    private sealed record AciBundle(
        AciMo Mo,
        string Node,
        string Id,
        string Label,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> Members);

    /// <summary>
    /// <c>pcAggrIf</c> をほどく。子の <c>pcRsMbrIfs</c> がメンバーの物理インターフェースを指している。
    /// </summary>
    private static IReadOnlyList<AciBundle> ReadBundles(IEnumerable<AciMo>? bundles)
    {
        if (bundles is null) return [];

        var list = new List<AciBundle>();

        foreach (AciMo aggregate in bundles)
        {
            string dn = aggregate["dn"];
            string node = AciDn.Node(dn);
            string id = Or(aggregate["id"], AciDn.Value(dn, "aggr"));
            string name = aggregate["name"];

            // 束ね方(LACP かどうか)は束の側にしか出ない。分かる範囲で添える
            string label = aggregate["pcMode"] is { Length: > 0 } mode && mode != "off"
                ? $"{id}（{mode}）"
                : id;

            string[] memberPorts =
            [
                .. aggregate.ChildrenOf("pcRsMbrIfs")
                    .Select(m => AciDn.Port(m["tDn"]))
                    .Where(p => p.Length > 0),
            ];

            // 静的パスは番号(po1)でも名前(ポリシーグループ名)でも指してくる
            string[] names = [id, name];

            list.Add(new AciBundle(
                Mo: aggregate,
                Node: node,
                Id: id,
                Label: label,
                Names: [.. names.Where(n => n.Length > 0)],
                Members: memberPorts));
        }

        return list;
    }

    /// <summary>
    /// その静的パスがこの口のものか。素の口は名前で、束の口は束の名前で当てる。
    /// vPC の DN はノードを 2 つ持つ（<c>protpaths-101-102</c>）ので、どちらかに含まれれば当たり。
    /// </summary>
    private static bool Matches(AciEpgMemberRow member, string node, string port, AciBundle? bundle)
    {
        if (!NodeMatches(member.Node, node)) return false;

        if (string.Equals(member.Path, port, StringComparison.OrdinalIgnoreCase)) return true;

        return bundle is not null
               && bundle.Names.Any(n => string.Equals(n, member.Path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool NodeMatches(string pathNode, string node)
    {
        if (pathNode.Length == 0 || node.Length == 0) return false;
        if (string.Equals(pathNode, node, StringComparison.Ordinal)) return true;

        // vPC は "101-102" の形。どちらかのノードなら、その口の話
        return pathNode.Split('-').Any(part => string.Equals(part, node, StringComparison.Ordinal));
    }

    private static string PortKey(string node, string port) => $"{node}|{port}";

    /// <summary>重複を畳んで並べる。空は落とす（「—」を並べても読めない）。</summary>
    private static string Join(IEnumerable<string> values)
        => string.Join(", ", values.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal));

    public static string DescribeAdminState(string? adminSt) => adminSt switch
    {
        "up" => "有効",
        "down" => "無効",
        null or "" => "—",
        _ => adminSt,
    };

    /// <summary>
    /// 実際の状態。<b>「落ちている」と「わざと落としてある」を書き分ける</b> —
    /// 一緒にすると、確認したい 1 本が無効ポートの海に埋もれる。
    /// </summary>
    public static (string Text, SeverityKind Kind) DescribeOperState(string? operSt, string? adminSt)
        => operSt switch
        {
            "up" => ("● 稼働", SeverityKind.Ok),
            "down" when adminSt == "down" => ("◌ 無効", SeverityKind.Muted),
            "down" => ("✕ 停止", SeverityKind.Alert),
            null or "" => ("—", SeverityKind.Muted),
            _ => (operSt, SeverityKind.Muted),
        };

    public static string DescribeUsage(string? usage) => usage switch
    {
        "epg" => "EPG",
        "fabric" => "ファブリック",
        "discovery" => "検出",
        "infra" => "インフラ",
        "controller" => "APIC",
        null or "" => "—",
        _ => usage,
    };

    // ===== EPG とそのメンバー =====

    public static IReadOnlyList<AciEpgRow> ParseEpgs(IEnumerable<AciMo> mos)
    {
        var rows = new List<AciEpgRow>();

        foreach (AciMo mo in mos)
        {
            string dn = mo["dn"];

            string domains = string.Join(", ", mo.ChildrenOf("fvRsDomAtt")
                .Select(d => DomainName(d["tDn"]))
                .Where(d => d.Length > 0));

            rows.Add(new AciEpgRow(
                Tenant: AciDn.Value(dn, "tn"),
                AppProfile: AciDn.Value(dn, "ap"),
                Name: Or(mo["name"], AciDn.Value(dn, "epg")),
                BridgeDomain: mo.FirstChild("fvRsBd")?["tnFvBDName"] ?? "",
                Domains: domains,
                PathCount: mo.ChildrenOf("fvRsPathAtt").Count(),
                Dn: dn));
        }

        return rows;
    }

    /// <summary>
    /// EPG にぶら下がる静的パスを、EPG をまたいで全部返す。
    /// 画面側で選ばれた EPG の <c>Dn</c> で絞る（取得のたびに引き直さない）。
    /// </summary>
    public static IReadOnlyList<AciEpgMemberRow> ParseEpgMembers(IEnumerable<AciMo> mos)
    {
        var rows = new List<AciEpgMemberRow>();

        foreach (AciMo mo in mos)
        {
            string dn = mo["dn"];
            string epg = Or(mo["name"], AciDn.Value(dn, "epg"));

            foreach (AciMo path in mo.ChildrenOf("fvRsPathAtt"))
            {
                string target = path["tDn"];

                rows.Add(new AciEpgMemberRow(
                    EpgDn: dn,
                    Epg: epg,
                    Node: AciDn.Node(target),
                    Path: Or(AciDn.Port(target), target),
                    Encap: path["encap"],
                    Mode: DescribeMode(path["mode"])));
            }
        }

        return rows;
    }

    /// <summary>静的パスのタグ付け。APIC の値は言葉が短すぎて意味が取れないので言い換える。</summary>
    public static string DescribeMode(string? mode) => mode switch
    {
        "regular" => "タグ付き",
        "native" => "ネイティブ",
        "untagged" => "タグなし",
        null or "" => "—",
        _ => mode,
    };

    /// <summary>ドメインの DN から見せる名前だけ取る。</summary>
    private static string DomainName(string? tDn)
    {
        foreach (string prefix in (string[])["phys", "l3dom", "vmmp", "dom"])
        {
            string name = AciDn.Value(tDn, prefix);
            if (name.Length > 0) return name;
        }

        return tDn ?? "";
    }

    // ===== エンドポイント =====

    public static IReadOnlyList<AciEndpointRow> ParseEndpoints(IEnumerable<AciMo> mos)
    {
        var rows = new List<AciEndpointRow>();

        foreach (AciMo mo in mos)
        {
            string dn = mo["dn"];

            // IP は属性に出ないことがあり、そのときは子の fvIp が持っている
            string ip = mo["ip"];
            if (ip.Length == 0 || ip == "0.0.0.0")
            {
                ip = string.Join(", ", mo.ChildrenOf("fvIp").Select(i => i["addr"]).Where(a => a.Length > 0));
            }

            string path = mo.FirstChild("fvRsCEpToPathEp")?["tDn"] ?? "";

            rows.Add(new AciEndpointRow(
                Mac: mo["mac"],
                Ip: ip,
                Tenant: AciDn.Value(dn, "tn"),
                Epg: AciDn.Value(dn, "epg"),
                Encap: mo["encap"],
                Node: AciDn.Node(path),
                Path: Or(AciDn.Port(path), path)));
        }

        return rows;
    }

    // ===== 機器一覧 =====

    /// <summary>
    /// ファブリックの機器（<c>fabricNode</c>）。<b>型番・シリアル・版はここにしか無い</b>
    /// （<c>topSystem</c> はヘルスとポートの絞り込み向け。用途を混ぜない）。
    /// <b>ノード番号順</b>に並べる — 応答の順は APIC 任せ。
    /// </summary>
    public static IReadOnlyList<AciDeviceRow> ParseDevices(IEnumerable<AciMo> mos)
    {
        var rows = new List<AciDeviceRow>();

        foreach (AciMo mo in mos)
        {
            string dn = mo["dn"];

            rows.Add(new AciDeviceRow(
                Node: Or(mo["id"], AciDn.Value(dn, "node")),
                Name: mo["name"],
                Role: DescribeNodeRole(mo["role"]),
                Model: mo["model"],
                Serial: mo["serial"],
                Version: Or(mo["version"], mo["fwVer"]),
                State: DescribeFabricState(mo["fabricSt"]),
                Pod: AciDn.Value(dn, "pod")));
        }

        return [.. rows.OrderBy(r => r.NodeKey, StringComparer.Ordinal)];
    }

    public static string DescribeNodeRole(string? role) => role switch
    {
        "controller" => "APIC",
        "leaf" => "リーフ",
        "spine" => "スパイン",
        null or "" => "ノード",
        _ => role,
    };

    public static string DescribeFabricState(string? state) => state switch
    {
        "active" => "● 稼働",
        "inactive" => "✕ 停止",
        "disabled" => "◌ 無効",
        "discovering" => "◌ 検出中",
        "undiscovered" => "✕ 未検出",
        null or "" => "—",
        _ => state,
    };



    public static string DescribeJobState(string? state) => state switch
    {
        "success" or "successful" => "● 成功",
        "failed" => "✕ 失敗",
        "running" => "◌ 実行中",
        "scheduled" => "◌ 待ち",
        null or "" => "—",
        _ => state,
    };

    // ===== CSV / Excel =====

    public static CsvTable ToCsv(IReadOnlyList<AciHealthRow> rows) => new(
        ["種別", "名前", "スコア", "状態"],
        [.. rows.Select(r => new[] { r.Kind, r.Name, r.ScoreText, r.State })]);

    public static CsvTable ToCsv(IReadOnlyList<AciFaultRow> rows) => new(
        ["重大度", "コード", "対象", "説明", "発生", "最終遷移", "確認"],
        [.. rows.Select(r => new[] { r.Severity, r.Code, r.Target, r.Description, r.Created, r.LastTransition, r.Ack })]);

    public static CsvTable ToCsv(IReadOnlyList<AciLogRow> rows) => new(
        ["時刻", "種別", "重大度", "対象", "内容"],
        [.. rows.Select(r => new[] { r.Time, r.Kind, r.Severity, r.Target, r.Text })]);

    public static CsvTable ToCsv(IReadOnlyList<AciPortRow> rows) => new(
        ["ノード", "インターフェース", "説明", "管理", "状態", "速度", "用途", "ポートチャネル", "EPG", "VLAN", "タグ", "理由", "最終変化"],
        [.. rows.Select(r => new[]
        {
            r.Node, r.Interface, r.Description, r.AdminState, r.OperState, r.Speed, r.Usage,
            r.PortChannel, r.Epgs, r.Vlans, r.Modes, r.Reason, r.LastChange,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<AciEpgRow> rows) => new(
        ["テナント", "アプリ", "EPG", "ブリッジドメイン", "ドメイン", "静的パス"],
        [.. rows.Select(r => new[]
        {
            r.Tenant, r.AppProfile, r.Name, r.BridgeDomain, r.Domains,
            r.PathCount.ToString(CultureInfo.InvariantCulture),
        })]);

    public static CsvTable ToCsv(IReadOnlyList<AciEpgMemberRow> rows) => new(
        ["EPG", "ノード", "パス", "VLAN", "モード"],
        [.. rows.Select(r => new[] { r.Epg, r.Node, r.Path, r.Encap, r.Mode })]);

    public static CsvTable ToCsv(IReadOnlyList<AciEndpointRow> rows) => new(
        ["MAC", "IP", "テナント", "EPG", "VLAN", "ノード", "パス"],
        [.. rows.Select(r => new[] { r.Mac, r.Ip, r.Tenant, r.Epg, r.Encap, r.Node, r.Path })]);

    public static CsvTable ToCsv(IReadOnlyList<AciDeviceRow> rows) => new(
        ["ノード", "名前", "役割", "型番", "シリアル", "版", "状態", "Pod"],
        [.. rows.Select(r => new[]
        {
            r.Node, r.Name, r.Role, r.Model, r.Serial, r.Version, r.State, r.Pod,
        })]);

    // ===== 問い合わせ先の組み立て（HTTP そのものには触らない） =====

    /// <summary>1 回の要求で取る件数。APIC の既定上限に合わせる。</summary>
    public const int PageSize = 200;

    /// <summary>
    /// Faults の既定の絞り込み。<b>info を落とす</b> — 情報レベルまで出すと数千行になり、
    /// 見たい重大なものが埋もれる。重大度の選択欄は作らない（既定を正しくする方が早い）。
    /// </summary>
    public const string FaultFilter = "query-target-filter=ne(faultInst.severity,\"info\")";

    /// <summary>
    /// クラス問い合わせのパス。<paramref name="scopeDn"/> を渡すと、その配下だけに絞る
    /// （<b>ポートは必ず絞ること</b>。ファブリック全体の l1PhysIf は数万行になる）。
    /// </summary>
    public static string ClassPath(string className, string? options = null, string? scopeDn = null)
    {
        string path = string.IsNullOrEmpty(scopeDn)
            ? $"/api/node/class/{className}.json"
            : $"/api/node/class/{scopeDn}/{className}.json";

        return string.IsNullOrEmpty(options) ? path : $"{path}?{options}";
    }

    /// <summary>
    /// テナント 1 つの設定を、枝ごと丸ごと引くパス。作業前後の見比べに使う。
    ///
    /// <b><c>rsp-prop-include=config-only</c> が肝。</b>これを付けないと稼働値
    /// （カウンタ・時刻・状態）まで混ざり、<b>設定を何も変えていなくても差分だらけになる</b>
    /// （show ip route の経過時間と同じ問題）。
    ///
    /// <b>ページングは効かない。</b>枝を丸ごと 1 応答で返す問い合わせなので、
    /// <see cref="PagePath"/> を重ねないこと。
    /// </summary>
    public static string TenantExportPath(string tenantName)
        => $"/api/mo/uni/tn-{Uri.EscapeDataString(tenantName)}.json"
           + "?rsp-subtree=full&rsp-prop-include=config-only";

    /// <summary>ページの指定を足す。すでに絞り込みが付いていれば <c>&amp;</c> で継ぐ。</summary>
    public static string PagePath(string path, int page, int pageSize = PageSize)
        => $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}page={page}&page-size={pageSize}";

    /// <summary>
    /// 総件数から必要なページ数を出す。<b>総件数が読めなかった(-1)ときは 1 ページ</b>
    /// （分からないまま何十回も投げない）。
    /// </summary>
    public static int PageCount(int totalCount, int pageSize = PageSize)
    {
        if (totalCount <= 0 || pageSize <= 0) return 1;

        return (totalCount + pageSize - 1) / pageSize;
    }

    /// <summary>
    /// 接続先の書き方を整える。<c>https://</c> を付けて渡されても、末尾に <c>/</c> が
    /// 付いていても同じ形にする（毎回手で打つ欄なので、揺れは受け側で吸収する）。
    /// </summary>
    public static string NormalizeHost(string? host) => HttpsHost.Normalize(host);

    // ===== ログイン =====

    /// <summary>
    /// ログイン名。ローカル以外の認証（RADIUS/TACACS+/AD）を使うときは
    /// <c>apic:&lt;ドメイン&gt;\&lt;ユーザー&gt;</c> の形でないと通らない。
    /// </summary>
    public static string LoginName(string? domain, string? user)
    {
        string name = (user ?? "").Trim();
        string realm = (domain ?? "").Trim();

        return realm.Length == 0 ? name : $"apic:{realm}\\{name}";
    }

    /// <summary>ログイン要求の本文。</summary>
    public static string LoginBody(string name, string password)
        => JsonSerializer.Serialize(new
        {
            aaaUser = new { attributes = new { name, pwd = password } },
        });

    /// <summary>
    /// ログイン応答からトークンと寿命を取る。読めなければトークンは空
    /// （呼び側が「ログインできなかった」として扱う）。
    /// </summary>
    public static (string Token, int RefreshSeconds) ParseLogin(string? body)
    {
        AciMo? login = AciMoReader.Parse(body ?? "").FirstOrDefault(m => m.ClassName == "aaaLogin");

        if (login is null) return ("", 0);

        int seconds = Int(login["refreshTimeoutSeconds"]) ?? 0;

        return (login["token"], seconds);
    }

    /// <summary>
    /// 証明書の指紋を人が見比べられる形にする。APIC の画面に出るのと同じ
    /// 大文字 16 進のコロン区切り。
    /// </summary>
    public static string FormatFingerprint(byte[]? sha256) => HttpsHost.Fingerprint(sha256);

    // ===== 応答コード =====

    /// <summary>
    /// 応答コードを日本語にする。<b>本文は読まない</b> —
    /// APIC のエラー本文には DN や設定の断片が入るので、画面やログに流さない。
    /// </summary>
    public static string DescribeFailure(int statusCode) => statusCode switch
    {
        400 => "要求の内容が正しくありません（400）。",
        401 => "ログインできませんでした（401）。ユーザー名・パスワード・ログインドメインを確認してください。",
        403 => "このユーザーでは参照できません（403）。読み取りの権限を確認してください。",
        404 => "見つかりませんでした（404）。APIC の版でこのクラスを引けない可能性があります。",
        >= 500 and < 600 => $"APIC 側で処理できませんでした（{statusCode}）。時間をおいて試してください。",
        _ => $"取得できませんでした（HTTP {statusCode}）。",
    };

    // ===== 小物 =====

    private static string Range(string from, string to)
    {
        if (from.Length == 0) return "";

        return to.Length == 0 || to == from ? from : $"{from}〜{to}";
    }

    private static int? Int(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;

    private static string Or(string first, string second) => first.Length > 0 ? first : second;
}
