using System.Globalization;
using System.Net;

namespace NetworkToys.Core.Addressing;

/// <summary>
/// PC の IPv4 設定として適用する内容。ここに入った時点で矛盾はない
/// (画面の文字列は必ず <see cref="Parse"/> を通す)。
///
/// 適用は PowerShell(NetTCPIP)のスクリプト実行(<see cref="ToPowerShellScript"/>)で行う。
/// </summary>
public sealed record IpPlan(
    string InterfaceName,
    int InterfaceIndex,
    bool Dhcp,
    IPAddress? Address,
    int PrefixLength,
    IPAddress? Gateway,
    IPAddress? Dns1,
    IPAddress? Dns2)
{
    /// <summary>
    /// 画面の入力から適用内容を組み立てる。error 非 null なら失敗(最初の 1 件のみ)。
    /// warning は「適用はできるが確認してほしい」(ゲートウェイがサブネット外、など)。
    /// </summary>
    public static IpPlan? Parse(
        string interfaceName, int interfaceIndex, bool dhcp,
        string address, string mask, string gateway, string dns1, string dns2,
        out string? error, out string? warning)
    {
        error = null;
        warning = null;

        string name = (interfaceName ?? "").Trim();
        if (name.Length == 0)
        {
            error = "アダプタを選んでください。";
            return null;
        }

        // netsh のクォート内エスケープ仕様は文書化されていないので、発明せず拒否する。
        // Windows のアダプタ名に引用符が現れることはまず無い
        if (name.Contains('"'))
        {
            error = "アダプタ名に引用符が含まれていて扱えません。";
            return null;
        }

        if (dhcp)
            return new IpPlan(name, interfaceIndex, true, null, 0, null, null, null);

        address = (address ?? "").Trim();
        mask = (mask ?? "").Trim();
        gateway = (gateway ?? "").Trim();
        dns1 = (dns1 ?? "").Trim();
        dns2 = (dns2 ?? "").Trim();

        if (!IpRangeParser.TryParseIPv4(address, out IPAddress? ip))
        {
            error = "IP アドレスの形式が正しくありません(例: 192.168.1.10)。";
            return null;
        }

        int prefix = ParseMask(mask, out error);
        if (error is not null)
            return null;

        IPAddress? gw = null;
        if (gateway.Length > 0)
        {
            if (!IpRangeParser.TryParseIPv4(gateway, out gw))
            {
                error = "ゲートウェイの形式が正しくありません。";
                return null;
            }

            // 現場では意図的にサブネット外の GW を書くことがあるので、止めずに知らせる
            if (!IpMath.IsSameSubnet(ip, gw, prefix))
                warning = "ゲートウェイが指定のサブネットの外にあります。";
        }

        IPAddress? d1 = null;
        IPAddress? d2 = null;

        if (dns1.Length > 0 && !IpRangeParser.TryParseIPv4(dns1, out d1))
        {
            error = "優先 DNS の形式が正しくありません。";
            return null;
        }

        if (dns2.Length > 0)
        {
            if (d1 is null)
            {
                error = "代替 DNS だけの指定はできません。優先 DNS に入れてください。";
                return null;
            }

            if (!IpRangeParser.TryParseIPv4(dns2, out d2))
            {
                error = "代替 DNS の形式が正しくありません。";
                return null;
            }

            if (d2!.Equals(d1))
                warning ??= "優先と代替の DNS が同じです。";
        }

        // ネットワーク/ブロードキャストは意図的に使う現場もあるため warning 止まり
        if (prefix <= 30)
        {
            if (ip.Equals(IpMath.NetworkAddress(ip, prefix)))
                warning ??= "ネットワークアドレスを指定しています。";
            else if (ip.Equals(IpMath.BroadcastAddress(ip, prefix)))
                warning ??= "ブロードキャストアドレスを指定しています。";
        }

        return new IpPlan(name, interfaceIndex, false, ip, prefix, gw, d1, d2);
    }

    /// <summary>
    /// 適用スクリプト(PowerShell / NetTCPIP)。<b>IP 設定に netsh を使わない</b> —
    /// netsh には「DHCP の旗」を直接切り替える命令が無く、旗と実アドレスが食い違った
    /// 機械では <c>set address source=dhcp</c> が「すでに有効です」の断りだけで
    /// 何もできなかった(static の入れ直しでも旗は下りない。2026-08-21 実機)。
    /// <c>Set-NetIPInterface -Dhcp</c> は旗そのものを明示的に切り替えるので、
    /// どんな状態からでも同じ結果に揃う。指定は番号(ifIndex)で行い、
    /// 取れないアダプタだけ名前(単一引用符・'' エスケープ)で指定する。
    /// </summary>
    public IReadOnlyList<string> ToPowerShellScript()
    {
        string target = InterfaceIndex > 0
            ? $"-InterfaceIndex {InterfaceIndex.ToString(CultureInfo.InvariantCulture)}"
            : $"-InterfaceAlias '{InterfaceName.Replace("'", "''", StringComparison.Ordinal)}'";

        // 旗 → 掃除(古いアドレスと既定ルート) → 新しい値、の順。掃除は無くても失敗にしない。
        // 旗は両ストアに明示して書く — Set-NetIPInterface は -PolicyStore 省略時
        // ActiveStore しか書き換えず、PersistentStore に DHCP 有効が残ると
        // New-NetIPAddress が「Inconsistent parameters PolicyStore PersistentStore and
        // Dhcp Enabled」で断る(2026-08-21 実機で発生。混成状態の根もこれ)
        string dhcpFlag = Dhcp ? "Enabled" : "Disabled";
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"Set-NetIPInterface {target} -AddressFamily IPv4 -Dhcp {dhcpFlag} -PolicyStore ActiveStore",
            $"Set-NetIPInterface {target} -AddressFamily IPv4 -Dhcp {dhcpFlag} -PolicyStore PersistentStore",
            $"Remove-NetIPAddress {target} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue",
            $"Remove-NetRoute {target} -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
        };

        if (Dhcp)
        {
            lines.Add($"Set-DnsClientServerAddress {target} -ResetServerAddresses");
            return lines;
        }

        string newAddress = $"New-NetIPAddress {target} -IPAddress {Address} -PrefixLength {PrefixLength.ToString(CultureInfo.InvariantCulture)}";
        if (Gateway is not null)
            newAddress += $" -DefaultGateway {Gateway}";
        lines.Add(newAddress + " | Out-Null");

        // DNS 空は「クリア」— プリセットは「選べば同じ状態になる」決定性を優先し、
        // 前の現場の DNS を残さない(netsh 時代からの決まり)
        lines.Add(Dns1 is null
            ? $"Set-DnsClientServerAddress {target} -ResetServerAddresses"
            : Dns2 is null
                ? $"Set-DnsClientServerAddress {target} -ServerAddresses '{Dns1}'"
                : $"Set-DnsClientServerAddress {target} -ServerAddresses '{Dns1}','{Dns2}'");

        return lines;
    }

    /// <summary>マスク欄は「255.255.255.0」「24」「/24」の 3 形を受ける。</summary>
    private static int ParseMask(string mask, out string? error)
    {
        error = null;

        if (mask.Length == 0)
        {
            error = "サブネットマスク(またはプレフィクス長)を入れてください。";
            return 0;
        }

        string text = mask.StartsWith('/') ? mask[1..] : mask;

        if (int.TryParse(text, out int prefix))
        {
            if (prefix is >= 1 and <= 32)
                return prefix;

            error = "プレフィクス長は 1〜32 で指定してください。";
            return 0;
        }

        if (IpRangeParser.TryParseIPv4(mask, out IPAddress? parsed))
        {
            try
            {
                int fromMask = IpMath.MaskToPrefix(parsed);
                if (fromMask is >= 1 and <= 32)
                    return fromMask;

                error = "プレフィクス長は 1〜32 で指定してください。";
                return 0;
            }
            catch (ArgumentException)
            {
                error = "サブネットマスクのビットが連続していません。";
                return 0;
            }
        }

        error = "サブネットマスクの形式が正しくありません(例: 255.255.255.0 または 24)。";
        return 0;
    }
}
