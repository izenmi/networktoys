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
    // IP 設定の適用は netsh でなく PowerShell(NetTCPIP)。netsh には DHCP の旗を
    // 直接切り替える命令が無く、旗と実アドレスが食い違った機械では
    // set address source=dhcp が「すでに有効です」の断りから抜けられなかった(2026-08-21)

    [Fact]
    public void 固定の適用は旗を下ろし掃除してから値を入れる()
    {
        IpPlan plan = Parse(index: 12, gateway: "192.168.1.1", dns1: "8.8.8.8", dns2: "8.8.4.4")!;

        Assert.Equal(new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -Dhcp Disabled -PolicyStore ActiveStore",
            "Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -Dhcp Disabled -PolicyStore PersistentStore",
            "Remove-NetIPAddress -InterfaceIndex 12 -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue",
            "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
            "New-NetIPAddress -InterfaceIndex 12 -IPAddress 192.168.1.10 -PrefixLength 24 -DefaultGateway 192.168.1.1 | Out-Null",
            "Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses '8.8.8.8','8.8.4.4'",
        }, plan.ToPowerShellScript());
    }

    [Fact]
    public void ゲートウェイ無しはDefaultGatewayを付けずDNS無しはクリアする()
    {
        IpPlan plan = Parse(index: 12)!;
        IReadOnlyList<string> lines = plan.ToPowerShellScript();

        Assert.Equal("New-NetIPAddress -InterfaceIndex 12 -IPAddress 192.168.1.10 -PrefixLength 24 | Out-Null", lines[5]);
        // DNS 空は「クリア」— プリセットの決定性を優先し、前の現場の DNS を残さない
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 12 -ResetServerAddresses", lines[6]);
    }

    [Fact]
    public void DHCPへ戻すのは旗を上げて掃除するだけ()
    {
        IpPlan plan = Parse(index: 12, dhcp: true)!;

        Assert.Equal(new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -Dhcp Enabled -PolicyStore ActiveStore",
            "Set-NetIPInterface -InterfaceIndex 12 -AddressFamily IPv4 -Dhcp Enabled -PolicyStore PersistentStore",
            "Remove-NetIPAddress -InterfaceIndex 12 -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue",
            "Remove-NetRoute -InterfaceIndex 12 -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue",
            "Set-DnsClientServerAddress -InterfaceIndex 12 -ResetServerAddresses",
        }, plan.ToPowerShellScript());
    }

    [Fact]
    public void 番号が取れないアダプタは名前を単一引用符で渡す()
    {
        // 名前に ' が入っても '' に畳んで壊れない(BOM 付き UTF-8 なので日本語も届く)
        IpPlan plan = Parse(name: "ローカル エリア接続 'テスト'", dhcp: true)!;

        Assert.Equal("Set-NetIPInterface -InterfaceAlias 'ローカル エリア接続 ''テスト''' -AddressFamily IPv4 -Dhcp Enabled -PolicyStore ActiveStore",
            plan.ToPowerShellScript()[1]);
    }

}
