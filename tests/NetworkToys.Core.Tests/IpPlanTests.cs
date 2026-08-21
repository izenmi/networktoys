using NetworkToys.Core.Addressing;
using Xunit;

namespace NetworkToys.Core.Tests;

public class IpPlanTests
{
    private static IpPlan? Parse(
        string name = "イーサネット", int index = 0, bool isUp = true, bool dhcp = false,
        string address = "192.168.1.10", string mask = "255.255.255.0",
        string gateway = "", string dns1 = "", string dns2 = "",
        string? expectError = null, string? expectWarningContains = null)
    {
        IpPlan? plan = IpPlan.Parse(name, index, isUp, dhcp, address, mask, gateway, dns1, dns2,
            out string? error, out string? warning);

        if (expectError is null)
            Assert.Null(error);
        else
            Assert.Contains(expectError, error ?? "", StringComparison.Ordinal);

        if (expectWarningContains is not null)
            Assert.Contains(expectWarningContains, warning ?? "", StringComparison.Ordinal);

        return plan;
    }

    // ===== Parse =====

    [Theory]
    [InlineData("255.255.255.0")]
    [InlineData("24")]
    [InlineData("/24")]
    public void All_three_mask_forms_yield_the_same_prefix(string mask)
    {
        IpPlan? plan = Parse(mask: mask);

        Assert.NotNull(plan);
        Assert.Equal(24, plan!.PrefixLength);
    }

    [Fact]
    public void Sloppy_addresses_are_rejected()
    {
        // IPAddress.TryParse は "1" を 0.0.0.1 と解釈するが、ここでは拒否する
        Parse(address: "1", expectError: "IP アドレスの形式");
        Parse(address: "192.168.1", expectError: "IP アドレスの形式");
    }

    [Fact]
    public void Discontiguous_masks_are_rejected()
    {
        Parse(mask: "255.0.255.0", expectError: "連続していません");
    }

    [Fact]
    public void Prefix_out_of_range_is_rejected()
    {
        Parse(mask: "0", expectError: "1〜32");
        Parse(mask: "33", expectError: "1〜32");
        Parse(mask: "0.0.0.0", expectError: "1〜32");
    }

    [Fact]
    public void Gateway_outside_the_subnet_warns_but_passes()
    {
        IpPlan? plan = Parse(gateway: "10.0.0.1", expectWarningContains: "サブネットの外");

        Assert.NotNull(plan);
        Assert.Equal("10.0.0.1", plan!.Gateway!.ToString());
    }

    [Fact]
    public void Empty_gateway_and_dns_are_fine()
    {
        IpPlan? plan = Parse();

        Assert.NotNull(plan);
        Assert.Null(plan!.Gateway);
        Assert.Null(plan.Dns1);
        Assert.Null(plan.Dns2);
    }

    [Fact]
    public void Secondary_dns_alone_is_rejected()
    {
        Parse(dns2: "8.8.4.4", expectError: "代替 DNS だけ");
    }

    [Fact]
    public void Empty_or_quoted_adapter_names_are_rejected()
    {
        Parse(name: "", expectError: "アダプタを選んで");
        Parse(name: "イーサ\"ネット", expectError: "引用符");
        Parse(name: "イーサ'ネット", expectError: "引用符");
    }

    [Fact]
    public void Network_and_broadcast_addresses_warn()
    {
        Parse(address: "192.168.1.0", expectWarningContains: "ネットワークアドレス");
        Parse(address: "192.168.1.255", expectWarningContains: "ブロードキャスト");
    }

    [Fact]
    public void Dhcp_ignores_whatever_is_left_in_the_fields()
    {
        IpPlan? plan = Parse(dhcp: true, address: "ごみ", mask: "ごみ", dns2: "ごみ");

        Assert.NotNull(plan);
        Assert.True(plan!.Dhcp);
        Assert.Null(plan.Address);
    }

    // ===== ToPowerShellScript =====
    // 適用の本体は WMI(Win32_NetworkAdapterConfiguration)の EnableStatic / EnableDHCP。
    // netsh(旗を直接切り替えられない)と NetTCPIP(New-NetIPAddress が PersistentStore の
    // DHCP 旗との矛盾で断り続ける)が実機で通らなかったための選択(2026-08-21)。
    // 戻り値 84(IP not enabled)だけは EnableStatic 直後の再初期化で一瞬出るので、
    // インスタンスを取り直しながら再試行する

    private static readonly string[] ScriptHeader =
    [
        "$ErrorActionPreference = 'Stop'",
        "function Get-Nic { Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'InterfaceIndex=12' }",
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
        "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
    ];

    [Fact]
    public void 固定の適用はEnableStaticとSetGatewaysとDNSの3手()
    {
        IpPlan plan = Parse(index: 12, gateway: "192.168.1.1", dns1: "8.8.8.8", dns2: "8.8.4.4")!;

        string[] expected =
        [
            .. ScriptHeader,
            "Invoke-Nic 'EnableStatic' @{ IPAddress = @('192.168.1.10'); SubnetMask = @('255.255.255.0') }",
            "Invoke-Nic 'SetGateways' @{ DefaultIPGateway = @('192.168.1.1') }",
            "Invoke-Nic 'SetDNSServerSearchOrder' @{ DNSServerSearchOrder = @('8.8.8.8','8.8.4.4') }",
        ];
        Assert.Equal(expected, plan.ToPowerShellScript());
    }

    [Fact]
    public void ゲートウェイ無しはSetGatewaysを呼ばずDNS無しはクリアする()
    {
        IpPlan plan = Parse(index: 12)!;
        IReadOnlyList<string> lines = plan.ToPowerShellScript();

        Assert.DoesNotContain(lines, l => l.Contains("SetGateways", StringComparison.Ordinal));
        // DNS 空は「クリア」— プリセットの決定性を優先し、前の現場の DNS を残さない
        Assert.Equal("Invoke-Nic 'SetDNSServerSearchOrder' $null", lines[^1]);
    }

    [Fact]
    public void DHCPへ戻すのはEnableDHCPとDNSクリアの2手()
    {
        IpPlan plan = Parse(index: 12, dhcp: true)!;

        string[] expected =
        [
            .. ScriptHeader,
            "Invoke-Nic 'EnableDHCP' $null",
            "Invoke-Nic 'SetDNSServerSearchOrder' $null",
        ];
        Assert.Equal(expected, plan.ToPowerShellScript());
    }

    [Fact]
    public void リンクダウン中は永続ストアへ書いてリンクアップ時に適用させる()
    {
        // リンクが無いあいだ WMI は 84(IP not enabled)を返して一切設定できない(2026-08-21 実機)。
        // 「つなぐ前に仕込む」が本来の使い方なので、PersistentStore へ明示的に書く。
        // DHCP の旗を同じストアへ先に書くので New-NetIPAddress の矛盾エラーも起きない
        IpPlan plan = Parse(index: 12, isUp: false, gateway: "192.168.1.1", dns1: "8.8.8.8")!;

        Assert.Equal(new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -Dhcp Disabled -PolicyStore PersistentStore",
            "Remove-NetIPAddress -InterfaceIndex 12 -AddressFamily IPv4 -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue",
            "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue",
            "New-NetIPAddress -InterfaceIndex 12 -IPAddress 192.168.1.10 -PrefixLength 24 -PolicyStore PersistentStore -DefaultGateway 192.168.1.1 | Out-Null",
            "Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses '8.8.8.8'",
        }, plan.ToPowerShellScript());
    }

    [Fact]
    public void リンクダウン中のDHCP化も永続ストアだけで完結する()
    {
        IpPlan plan = Parse(index: 12, isUp: false, dhcp: true)!;

        Assert.Equal(new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -Dhcp Enabled -PolicyStore PersistentStore",
            "Remove-NetIPAddress -InterfaceIndex 12 -AddressFamily IPv4 -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue",
            "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue",
            "Set-DnsClientServerAddress -InterfaceIndex 12 -ResetServerAddresses",
        }, plan.ToPowerShellScript());
    }

    [Fact]
    public void 番号が取れないアダプタは接続名で引く()
    {
        IpPlan plan = Parse(name: "ローカル エリア接続 2", dhcp: true)!;
        IReadOnlyList<string> lines = plan.ToPowerShellScript();

        Assert.Equal("function Get-Nic { Get-CimInstance Win32_NetworkAdapter -Filter 'NetConnectionID=''ローカル エリア接続 2''' | Get-CimAssociatedInstance -ResultClassName Win32_NetworkAdapterConfiguration }",
            lines[1]);
        Assert.Contains("-InterfaceAlias 'ローカル エリア接続 2'",
            Assert.Single(lines, l => l.StartsWith("Remove-NetRoute", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

}
