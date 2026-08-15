using PastelNet.Core.Work;
using Xunit;

namespace PastelNet.Core.Tests;

public class TextDiffTests
{
    [Fact]
    public void Identical_text_has_no_changes()
    {
        TextDiffResult result = TextDiff.Compare("a\nb\nc", "a\nb\nc");

        Assert.False(result.HasChanges);
        Assert.All(result.Lines, l => Assert.Equal(DiffKind.Unchanged, l.Kind));
    }

    [Fact]
    public void An_added_line_is_marked()
    {
        TextDiffResult result = TextDiff.Compare("a\nc", "a\nb\nc");

        DiffLine added = Assert.Single(result.Lines, l => l.Kind == DiffKind.Added);
        Assert.Equal("b", added.Text);
        Assert.Equal(1, result.ChangedCount);
    }

    [Fact]
    public void A_removed_line_is_marked()
    {
        TextDiffResult result = TextDiff.Compare("a\nb\nc", "a\nc");

        DiffLine removed = Assert.Single(result.Lines, l => l.Kind == DiffKind.Removed);
        Assert.Equal("b", removed.Text);
    }

    [Fact]
    public void A_changed_line_shows_both_sides()
    {
        TextDiffResult result = TextDiff.Compare("a\nold\nc", "a\nnew\nc");

        Assert.Contains(result.Lines, l => l.Kind == DiffKind.Removed && l.Text == "old");
        Assert.Contains(result.Lines, l => l.Kind == DiffKind.Added && l.Text == "new");
    }

    [Fact]
    public void The_surrounding_lines_are_kept_in_order()
    {
        TextDiffResult result = TextDiff.Compare("head\nold\ntail", "head\nnew\ntail");

        Assert.Equal("head", result.Lines[0].Text);
        Assert.Equal(DiffKind.Unchanged, result.Lines[0].Kind);
        Assert.Equal("tail", result.Lines[^1].Text);
        Assert.Equal(DiffKind.Unchanged, result.Lines[^1].Kind);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        Assert.False(TextDiff.Compare(null, null).HasChanges);
        Assert.Equal(2, TextDiff.Compare("", "a\nb").ChangedCount);
        Assert.Equal(2, TextDiff.Compare("a\nb", "").ChangedCount);
    }

    [Fact]
    public void Crlf_and_lf_are_treated_the_same()
    {
        Assert.False(TextDiff.Compare("a\r\nb", "a\nb").HasChanges);
    }

    [Fact]
    public void Huge_input_is_given_up_on()
    {
        string huge = string.Join('\n', Enumerable.Range(0, 6000));

        TextDiffResult result = TextDiff.Compare(huge, huge + "\nx");

        Assert.True(result.TooLarge);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Dhcp_lease_times_are_filtered_out()
    {
        // 作業と無関係に必ず変わる行。これを落とさないと差分がノイズで埋まる
        const string before = """
            イーサネット アダプター イーサネット:
               IPv4 アドレス . . . . . . . . . .: 192.168.1.20
               リースが取得された日. . . . . . .: 2026年8月15日 9:00:00
               リースの有効期限 . . . . . . . .: 2026年8月16日 9:00:00
            """;
        const string after = """
            イーサネット アダプター イーサネット:
               IPv4 アドレス . . . . . . . . . .: 192.168.1.20
               リースが取得された日. . . . . . .: 2026年8月15日 11:30:00
               リースの有効期限 . . . . . . . .: 2026年8月16日 11:30:00
            """;

        Assert.True(TextDiff.Compare(before, after).HasChanges);

        TextDiffResult filtered = TextDiff.Compare(before, after, DiffNoiseFilter.IpConfig);

        Assert.False(filtered.HasChanges);
        Assert.Equal(4, filtered.IgnoredLines);
    }

    [Fact]
    public void An_english_windows_is_filtered_the_same_way()
    {
        const string before = "   Lease Obtained. . . . . . . . . : Saturday, August 15, 2026 9:00:00 AM";
        const string after = "   Lease Obtained. . . . . . . . . : Saturday, August 15, 2026 11:30:00 AM";

        Assert.False(TextDiff.Compare(before, after, DiffNoiseFilter.IpConfig).HasChanges);
    }

    [Fact]
    public void A_real_address_change_survives_the_filter()
    {
        const string before = """
               IPv4 アドレス . . . . . . . . . .: 192.168.1.20
               リースの有効期限 . . . . . . . .: 2026年8月16日 9:00:00
            """;
        const string after = """
               IPv4 アドレス . . . . . . . . . .: 192.168.1.99
               リースの有効期限 . . . . . . . .: 2026年8月16日 11:30:00
            """;

        TextDiffResult result = TextDiff.Compare(before, after, DiffNoiseFilter.IpConfig);

        Assert.True(result.HasChanges);
        Assert.Contains(result.Lines, l => l.Kind == DiffKind.Added && l.Text.Contains("192.168.1.99", StringComparison.Ordinal));
    }

    [Fact]
    public void Route_metrics_do_not_count_as_a_change()
    {
        // メトリックはリンク速度や状態で動く。リンクが一瞬落ちただけで全行が差分になる
        const string before = """
                      0.0.0.0          0.0.0.0      192.168.1.1     192.168.1.20     35
                  192.168.1.0    255.255.255.0         リンク上     192.168.1.20    291
            """;
        const string after = """
                      0.0.0.0          0.0.0.0      192.168.1.1     192.168.1.20     25
                  192.168.1.0    255.255.255.0         リンク上     192.168.1.20    281
            """;

        Assert.True(TextDiff.Compare(before, after).HasChanges);
        Assert.False(TextDiff.Compare(before, after, DiffNoiseFilter.RouteTable).HasChanges);
    }

    [Fact]
    public void A_changed_next_hop_survives_the_metric_filter()
    {
        const string before = "          0.0.0.0          0.0.0.0      192.168.1.1     192.168.1.20     35";
        const string after = "          0.0.0.0          0.0.0.0    192.168.1.254     192.168.1.20     25";

        TextDiffResult result = TextDiff.Compare(before, after, DiffNoiseFilter.RouteTable);

        Assert.True(result.HasChanges);
        Assert.Contains(result.Lines, l => l.Kind == DiffKind.Added && l.Text.Contains("192.168.1.254", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_route_survives_the_metric_filter()
    {
        const string before = "          0.0.0.0          0.0.0.0      192.168.1.1     192.168.1.20     35";
        const string after = """
                      0.0.0.0          0.0.0.0      192.168.1.1     192.168.1.20     35
                     10.0.0.0        255.0.0.0      192.168.1.9     192.168.1.20     36
            """;

        TextDiffResult result = TextDiff.Compare(before, after, DiffNoiseFilter.RouteTable);

        Assert.True(result.HasChanges);
        Assert.Contains(result.Lines, l => l.Kind == DiffKind.Added && l.Text.Contains("10.0.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Headings_are_left_alone_by_the_metric_filter()
    {
        const string text = "ネットワーク宛先        ネットマスク          ゲートウェイ       インターフェイス  メトリック";

        Assert.False(TextDiff.Compare(text, text, DiffNoiseFilter.RouteTable).HasChanges);
    }
}
