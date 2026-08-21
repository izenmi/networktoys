using System.Globalization;
using System.Net;

namespace NetworkToys.Core.Addressing;

/// <summary>
/// PC の IPv4 設定として適用する内容。ここに入った時点で矛盾はない
/// (画面の文字列は必ず <see cref="Parse"/> を通す)。
///
/// 適用は PowerShell(NetTCPIP)のスクリプト実行(<see cref="ToPowerShellScript"/>)で行う。
/// </summary>
/// <param name="InterfaceIsUp">リンクアップしているか。<b>リンクダウン中は書き方が変わる</b>
/// (WMI は 84 を返して設定できないため、永続ストアへ書いてリンクアップ時に適用させる)。</param>
public sealed record IpPlan(
    string InterfaceName,
    int InterfaceIndex,
    bool InterfaceIsUp,
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
        string interfaceName, int interfaceIndex, bool interfaceIsUp, bool dhcp,
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

        // 引用符のエスケープ仕様(WQL/PowerShell)を発明しない。
        // Windows のアダプタ名に引用符が現れることはまず無い
        if (name.Contains('"') || name.Contains('\''))
        {
            error = "アダプタ名に引用符が含まれていて扱えません。";
            return null;
        }

        if (dhcp)
            return new IpPlan(name, interfaceIndex, interfaceIsUp, true, null, 0, null, null, null);

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

        return new IpPlan(name, interfaceIndex, interfaceIsUp, false, ip, prefix, gw, d1, d2);
    }

    /// <summary>
    /// 適用スクリプト(PowerShell)。<b>本体は WMI(Win32_NetworkAdapterConfiguration)の
    /// EnableStatic / EnableDHCP</b> — DHCP と固定の切り替えを 1 メソッドで原子的に行うので、
    /// ストア間の矛盾という概念が無い。ここに至る脱落の記録(すべて 2026-08-21 実機):
    /// ①netsh は DHCP の旗を直接切り替えられず「すでに有効です」から抜けられない
    /// ②NetTCPIP は旗を両ストアへ明示しても New-NetIPAddress が
    /// 「Inconsistent parameters PolicyStore PersistentStore and Dhcp Enabled」で断り続けた。
    /// 戻り値は数値(0=成功 / 1=要再起動)なのでロケールに依らず成否を取れる。
    ///
    /// <b>戻り値 84(IP not enabled)だけは再試行する</b> — EnableStatic の直後は IP スタックが
    /// 再初期化中で、続く SetDNSServerSearchOrder などが一瞬 84 を返す(実機で発生。
    /// 昔の配備スクリプトが呼び出しの間に sleep を挟んでいたのはこのため)。
    /// インスタンスを取り直しながら最大 10 秒粘る。84 以外は即失敗。
    /// 指定は番号(ifIndex)。取れないアダプタだけ接続名(NetConnectionID)で引く。
    /// </summary>
    public IReadOnlyList<string> ToPowerShellScript()
    {
        if (!InterfaceIsUp)
            return ToPersistentStoreScript();

        string index = InterfaceIndex.ToString(CultureInfo.InvariantCulture);

        string lookup = InterfaceIndex > 0
            ? $"Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'InterfaceIndex={index}'"
            : $"Get-CimInstance Win32_NetworkAdapter -Filter 'NetConnectionID=''{InterfaceName}''' | Get-CimAssociatedInstance -ResultClassName Win32_NetworkAdapterConfiguration";

        string routeTarget = InterfaceIndex > 0
            ? $"-InterfaceIndex {index}"
            : $"-InterfaceAlias '{InterfaceName}'";

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"function Get-Nic {{ {lookup} }}",
            "function Invoke-Nic([string]$Method, [hashtable]$Arguments) {",
            "    $seen = $false",
            "    for ($i = 0; $i -lt 20; $i++) {",
            "        $nic = Get-Nic",
            "        if ($null -ne $nic) {",
            "            $seen = $true",
            "            if ($null -eq $Arguments) { $r = ($nic | Invoke-CimMethod -MethodName $Method).ReturnValue }",
            "            else { $r = ($nic | Invoke-CimMethod -MethodName $Method -Arguments $Arguments).ReturnValue }",
            "            if ($r -le 1) { return }",
            "            if ($r -ne 84) { throw \"${Method}: code $r\" }",
            "        }",
            "        Start-Sleep -Milliseconds 500",
            "    }",
            "    if (-not $seen) { throw 'アダプタが見つかりません。' }",
            "    throw \"${Method}: code 84 (IP の有効化を待ちましたが時間切れです)\"",
            "}",
            // 古い既定ルートは名指しで掃除する(EnableDHCP が静的 GW を残す環境があるため)。
            // 無ければ無いでよいので失敗にしない
            $"Remove-NetRoute {routeTarget} -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
        };

        if (Dhcp)
        {
            lines.Add("Invoke-Nic 'EnableDHCP' $null");
            // 引数なし = DNS も DHCP から受ける
            lines.Add("Invoke-Nic 'SetDNSServerSearchOrder' $null");
            return lines;
        }

        string mask = IpMath.FromUInt32(IpMath.PrefixToMask(PrefixLength)).ToString();
        lines.Add($"Invoke-Nic 'EnableStatic' @{{ IPAddress = @('{Address}'); SubnetMask = @('{mask}') }}");

        if (Gateway is not null)
            lines.Add($"Invoke-Nic 'SetGateways' @{{ DefaultIPGateway = @('{Gateway}') }}");

        // DNS 空は「クリア」($null) — プリセットは「選べば同じ状態になる」決定性を優先し、
        // 前の現場の DNS を残さない(netsh 時代からの決まり)
        lines.Add(Dns1 is null
            ? "Invoke-Nic 'SetDNSServerSearchOrder' $null"
            : Dns2 is null
                ? $"Invoke-Nic 'SetDNSServerSearchOrder' @{{ DNSServerSearchOrder = @('{Dns1}') }}"
                : $"Invoke-Nic 'SetDNSServerSearchOrder' @{{ DNSServerSearchOrder = @('{Dns1}','{Dns2}') }}");

        return lines;
    }

    /// <summary>
    /// <b>リンクダウン中のアダプタ用</b>: 永続ストア(PersistentStore)へ明示的に書く。
    /// リンクが無いあいだ WMI の EnableStatic/EnableDHCP は 84(IP not enabled)を返して
    /// 一切設定できない(2026-08-21 実機。ユーザーの本来の使い方は「つなぐ前に仕込む」)。
    /// 永続ストアへの書き込みはリンクアップした瞬間に Windows が適用する。
    /// DHCP の旗も同じストアへ先に書くので、New-NetIPAddress の
    /// 「Inconsistent parameters PolicyStore PersistentStore and Dhcp Enabled」も起きない。
    /// </summary>
    private IReadOnlyList<string> ToPersistentStoreScript()
    {
        string target = InterfaceIndex > 0
            ? $"-InterfaceIndex {InterfaceIndex.ToString(CultureInfo.InvariantCulture)}"
            : $"-InterfaceAlias '{InterfaceName}'";

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"Set-NetIPInterface {target} -AddressFamily IPv4 -Dhcp {(Dhcp ? "Enabled" : "Disabled")} -PolicyStore PersistentStore",
            $"Remove-NetIPAddress {target} -AddressFamily IPv4 -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue",
            $"Remove-NetRoute {target} -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue",
        };

        if (Dhcp)
        {
            lines.Add($"Set-DnsClientServerAddress {target} -ResetServerAddresses");
            return lines;
        }

        // 通常書きの前に、両ストアの残骸も掃除する(上の掃除は永続ストア限定のため)
        lines.Add($"Remove-NetIPAddress {target} -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue");
        lines.Add($"Remove-NetRoute {target} -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue");

        // New-NetIPAddress は永続ストア単独への作成(-PolicyStore PersistentStore)を
        // 「Invalid parameter」で受け付けない(2026-08-21 実機。経路を分けても同じ)。
        // 通常書き(両ストア)にする — 以前これが「Inconsistent parameters」で落ちたのは
        // 永続ストアの DHCP 旗が有効のままだったからで、上の行で旗を下ろした今は通る
        string newAddress =
            $"New-NetIPAddress {target} -IPAddress {Address} -PrefixLength {PrefixLength.ToString(CultureInfo.InvariantCulture)}";
        if (Gateway is not null)
            newAddress += $" -DefaultGateway {Gateway}";
        lines.Add(newAddress + " | Out-Null");

        // DNS 空は「クリア」— プリセットの決定性を優先し、前の現場の DNS を残さない
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
