using PastelNet.Core.Models;
using Xunit;

namespace PastelNet.Core.Tests;

public class ProfileMatcherTests
{
    private static Profile Office() => new()
    {
        Name = "本社",
        SubnetCidr = "192.168.1.0/24",
        GatewayAddress = "192.168.1.1",
        GatewayMac = "00-15-5D-01-02-03",
        Ssid = "office-wifi",
    };

    private static NetworkFingerprint AtOffice() =>
        new("192.168.1.0/24", "192.168.1.1", "00-15-5D-01-02-03", "office-wifi");

    [Fact]
    public void Everything_matching_scores_highest()
        => Assert.Equal(9, ProfileMatcher.Score(Office(), AtOffice()));

    [Fact]
    public void Nothing_matching_scores_zero()
    {
        var elsewhere = new NetworkFingerprint("10.0.0.0/24", "10.0.0.1", "AA-BB-CC-DD-EE-FF", "other");

        Assert.Equal(0, ProfileMatcher.Score(Office(), elsewhere));
    }

    [Fact]
    public void Wired_connections_still_match_without_an_ssid()
    {
        // 位置情報が未許可だと SSID は取れない。それでも判定が成立すること
        var wired = new NetworkFingerprint("192.168.1.0/24", "192.168.1.1", "00-15-5D-01-02-03", null);

        Assert.True(ProfileMatcher.Score(Office(), wired) >= ProfileMatcher.MatchThreshold);
        Assert.NotNull(ProfileMatcher.FindBest([Office()], wired));
    }

    [Fact]
    public void An_ssid_alone_is_not_enough()
    {
        // SSID は現場をまたいで使い回されることがあるので、単独では決め手にしない
        var ssidOnly = new NetworkFingerprint(null, null, null, "office-wifi");

        Assert.True(ProfileMatcher.Score(Office(), ssidOnly) < ProfileMatcher.MatchThreshold);
        Assert.Null(ProfileMatcher.FindBest([Office()], ssidOnly));
    }

    [Fact]
    public void The_gateway_mac_distinguishes_sites_sharing_a_subnet()
    {
        // 192.168.1.0/24 はどこの現場でも使われる。ゲートウェイの MAC で見分ける
        var siteA = new Profile { Name = "A社", SubnetCidr = "192.168.1.0/24", GatewayMac = "00-15-5D-01-02-03" };
        var siteB = new Profile { Name = "B社", SubnetCidr = "192.168.1.0/24", GatewayMac = "AA-BB-CC-11-22-33" };

        var atSiteB = new NetworkFingerprint("192.168.1.0/24", "192.168.1.1", "AA-BB-CC-11-22-33", null);

        Assert.Equal("B社", ProfileMatcher.FindBest([siteA, siteB], atSiteB)?.Name);
    }

    [Fact]
    public void Case_differences_in_a_mac_do_not_matter()
    {
        var lowercase = new NetworkFingerprint(null, null, "00-15-5d-01-02-03", null);

        Assert.Equal(3, ProfileMatcher.Score(Office(), lowercase));
    }

    [Fact]
    public void Empty_values_never_count_as_a_match()
    {
        var blank = new Profile { Name = "空", SubnetCidr = "", GatewayMac = null };
        var nothing = new NetworkFingerprint(null, null, null, null);

        Assert.Equal(0, ProfileMatcher.Score(blank, nothing));
        Assert.Null(ProfileMatcher.FindBest([blank], nothing));
    }

    [Fact]
    public void Profiles_need_a_name_and_at_least_one_key()
    {
        Assert.False(new Profile { Name = "名前だけ" }.IsValid());
        Assert.False(new Profile { SubnetCidr = "192.168.1.0/24" }.IsValid());
        Assert.True(new Profile { Name = "現場", SubnetCidr = "192.168.1.0/24" }.IsValid());
    }

    [Fact]
    public void The_best_match_wins_when_several_are_close()
    {
        var loose = new Profile { Name = "ゆるい", SubnetCidr = "192.168.1.0/24" };
        var exact = new Profile
        {
            Name = "厳密",
            SubnetCidr = "192.168.1.0/24",
            GatewayAddress = "192.168.1.1",
            GatewayMac = "00-15-5D-01-02-03",
        };

        Assert.Equal("厳密", ProfileMatcher.FindBest([loose, exact], AtOffice())?.Name);
    }
}
