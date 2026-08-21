using NetworkToys.Core.Addressing;
using Xunit;

namespace NetworkToys.Core.Tests;

public class IpPlanTests
{
    private static IpPlan? Parse(
        string name = "イーサネット", int index = 0, bool dhcp = false,
        string address = "192.168.1.10", string mask = "255.255.255.0",
        string gateway = "", string dns1 = "", string dns2 = "",
        System.Net.IPAddress? currentManualAddress = null, int currentManualPrefix = 0,
        string? expectError = null, string? expectWarningContains = null)
    {
        IpPlan? plan = IpPlan.Parse(name, index, dhcp, address, mask, gateway, dns1, dns2,
            currentManualAddress, currentManualPrefix, out string? error, out string? warning);

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

    // ===== ToNetshScript =====

    [Fact]
    public void 番号が取れたアダプタは名前でなく番号で流す()
    {
        // 日本語のアダプタ名は文字コード事故の入口(netsh が ERROR_INVALID_NAME を返した実例あり)。
        // 番号は純 ASCII なので、スクリプトの文字コードに依らず届く
        IpPlan plan = Parse(index: 12, gateway: "192.168.1.1", dns1: "8.8.8.8", dns2: "8.8.4.4")!;

        Assert.Equal(new[]
        {
            "interface ipv4 set address name=12 static 192.168.1.10 255.255.255.0 192.168.1.1",
            "interface ipv4 set dnsservers name=12 static 8.8.8.8 primary validate=no",
            "interface ipv4 add dnsservers name=12 8.8.4.4 index=2 validate=no",
        }, plan.ToNetshScript());

        IpPlan viaDhcp = Parse(index: 12, dhcp: true)!;

        Assert.Equal(new[]
        {
            "interface ipv4 set dnsservers name=12 source=dhcp",
            "interface ipv4 set address name=12 source=dhcp",
        }, viaDhcp.ToNetshScript());
    }

    [Fact]
    public void DHCPへ戻すときは一度staticを明示して旗を下ろしてから切り替える()
    {
        // Windows は「DHCP の旗が立ったまま手動アドレスが付いている」混成状態になることがあり、
        // その状態の set address source=dhcp は「すでに有効」のエラーで何もしない
        // (delete address でも旗は下りず、ゲートウェイも残る)。
        // static を明示すると旗が確実に下り(冪等・GW も none で剥がれ)、次の dhcp が必ず通る
        IpPlan plan = Parse(index: 12, dhcp: true,
            currentManualAddress: System.Net.IPAddress.Parse("192.168.1.10"), currentManualPrefix: 16)!;

        Assert.Equal(new[]
        {
            "interface ipv4 set dnsservers name=12 source=dhcp",
            "interface ipv4 set address name=12 static 192.168.1.10 255.255.0.0 none",
            "interface ipv4 set address name=12 source=dhcp",
        }, plan.ToNetshScript());

        // プレフィクス長が取れていなければ /24 とみなす(次の行で dhcp に戻るので実害はない)
        IpPlan noPrefix = Parse(index: 12, dhcp: true,
            currentManualAddress: System.Net.IPAddress.Parse("192.168.1.10"))!;

        Assert.Contains("static 192.168.1.10 255.255.255.0 none", noPrefix.ToNetshScript()[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Full_static_config_produces_three_lines()
    {
        IpPlan plan = Parse(gateway: "192.168.1.1", dns1: "8.8.8.8", dns2: "8.8.4.4")!;

        Assert.Equal(new[]
        {
            "interface ipv4 set address name=\"イーサネット\" static 192.168.1.10 255.255.255.0 192.168.1.1",
            "interface ipv4 set dnsservers name=\"イーサネット\" static 8.8.8.8 primary validate=no",
            "interface ipv4 add dnsservers name=\"イーサネット\" 8.8.4.4 index=2 validate=no",
        }, plan.ToNetshScript());
    }

    [Fact]
    public void Without_gateway_the_address_line_ends_at_the_mask()
    {
        IpPlan plan = Parse(dns1: "192.168.1.1")!;

        Assert.Equal(new[]
        {
            "interface ipv4 set address name=\"イーサネット\" static 192.168.1.10 255.255.255.0",
            "interface ipv4 set dnsservers name=\"イーサネット\" static 192.168.1.1 primary validate=no",
        }, plan.ToNetshScript());
    }

    [Fact]
    public void Without_dns_the_servers_are_cleared_not_left_over()
    {
        IpPlan plan = Parse(gateway: "192.168.1.1")!;

        Assert.Equal(new[]
        {
            "interface ipv4 set address name=\"イーサネット\" static 192.168.1.10 255.255.255.0 192.168.1.1",
            "interface ipv4 set dnsservers name=\"イーサネット\" static none validate=no",
        }, plan.ToNetshScript());
    }

    [Fact]
    public void Prefix_masks_are_normalized_to_dotted_form()
    {
        IpPlan plan = Parse(mask: "/16")!;

        Assert.Contains("static 192.168.1.10 255.255.0.0", plan.ToNetshScript()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Dhcp_produces_two_lines()
    {
        IpPlan plan = Parse(name: "ローカル エリア接続 2", dhcp: true)!;

        Assert.Equal(new[]
        {
            "interface ipv4 set dnsservers name=\"ローカル エリア接続 2\" source=dhcp",
            "interface ipv4 set address name=\"ローカル エリア接続 2\" source=dhcp",
        }, plan.ToNetshScript());
    }
}
