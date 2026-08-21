using NetworkToys.Core.Addressing;
using Xunit;

namespace NetworkToys.Core.Tests;

public class IpPlanTests
{
    private static IpPlan? Parse(
        string name = "イーサネット", int index = 0, bool dhcp = false,
        string address = "192.168.1.10", string mask = "255.255.255.0",
        string gateway = "", string dns1 = "", string dns2 = "",
        string? expectError = null, string? expectWarningContains = null)
    {
        IpPlan? plan = IpPlan.Parse(name, index, dhcp, address, mask, gateway, dns1, dns2,
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
    // DHCP 旗との矛盾で断り続ける)が実機で通らなかったための選択(2026-08-21)

    [Fact]
    public void 固定の適用はEnableStaticとSetGatewaysとDNSの3手()
    {
        IpPlan plan = Parse(index: 12, gateway: "192.168.1.1", dns1: "8.8.8.8", dns2: "8.8.4.4")!;

        Assert.Equal(new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$nic = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'InterfaceIndex=12'",
            "if ($null -eq $nic) { throw 'アダプタが見つかりません。' }",
            "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
            "$r = ($nic | Invoke-CimMethod -MethodName EnableStatic -Arguments @{ IPAddress = @('192.168.1.10'); SubnetMask = @('255.255.255.0') }).ReturnValue; if ($r -gt 1) { throw \"EnableStatic: code $r\" }",
            "$r = ($nic | Invoke-CimMethod -MethodName SetGateways -Arguments @{ DefaultIPGateway = @('192.168.1.1') }).ReturnValue; if ($r -gt 1) { throw \"SetGateways: code $r\" }",
            "$r = ($nic | Invoke-CimMethod -MethodName SetDNSServerSearchOrder -Arguments @{ DNSServerSearchOrder = @('8.8.8.8','8.8.4.4') }).ReturnValue; if ($r -gt 1) { throw \"SetDNSServerSearchOrder: code $r\" }",
        }, plan.ToPowerShellScript());
    }

    [Fact]
    public void ゲートウェイ無しはSetGatewaysを呼ばずDNS無しは引数なしでクリアする()
    {
        IpPlan plan = Parse(index: 12)!;
        IReadOnlyList<string> lines = plan.ToPowerShellScript();

        Assert.DoesNotContain(lines, l => l.Contains("SetGateways", StringComparison.Ordinal));
        // DNS 空は「クリア」— プリセットの決定性を優先し、前の現場の DNS を残さない
        Assert.Equal("$r = ($nic | Invoke-CimMethod -MethodName SetDNSServerSearchOrder).ReturnValue; if ($r -gt 1) { throw \"SetDNSServerSearchOrder: code $r\" }",
            lines[^1]);
    }

    [Fact]
    public void DHCPへ戻すのはEnableDHCPとDNSクリアの2手()
    {
        IpPlan plan = Parse(index: 12, dhcp: true)!;

        Assert.Equal(new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$nic = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'InterfaceIndex=12'",
            "if ($null -eq $nic) { throw 'アダプタが見つかりません。' }",
            "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
            "$r = ($nic | Invoke-CimMethod -MethodName EnableDHCP).ReturnValue; if ($r -gt 1) { throw \"EnableDHCP: code $r\" }",
            "$r = ($nic | Invoke-CimMethod -MethodName SetDNSServerSearchOrder).ReturnValue; if ($r -gt 1) { throw \"SetDNSServerSearchOrder: code $r\" }",
        }, plan.ToPowerShellScript());
    }

    [Fact]
    public void 番号が取れないアダプタは接続名で引く()
    {
        IpPlan plan = Parse(name: "ローカル エリア接続 2", dhcp: true)!;
        IReadOnlyList<string> lines = plan.ToPowerShellScript();

        Assert.Equal("$nic = Get-CimInstance Win32_NetworkAdapter -Filter 'NetConnectionID=''ローカル エリア接続 2''' | Get-CimAssociatedInstance -ResultClassName Win32_NetworkAdapterConfiguration",
            lines[1]);
        Assert.Contains("-InterfaceAlias 'ローカル エリア接続 2'", lines[3], StringComparison.Ordinal);
    }

}
