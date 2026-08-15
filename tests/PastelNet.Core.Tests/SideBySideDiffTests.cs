using PastelNet.Core.Work;
using Xunit;

namespace PastelNet.Core.Tests;

public class SideBySideDiffTests
{
    [Fact]
    public void Identical_text_lines_up_on_both_sides()
    {
        SideBySideResult result = SideBySideDiff.Build("a\nb", "a\nb");

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal(SideKind.Same, r.Kind));
        Assert.False(result.HasChanges);
    }

    [Fact]
    public void Line_numbers_are_counted_per_side()
    {
        SideBySideResult result = SideBySideDiff.Build("a\nb\nc", "a\nc");

        Assert.Equal(1, result.Rows[0].LeftNumber);
        Assert.Equal(1, result.Rows[0].RightNumber);

        // 消えた行は左にだけ番号が付く
        Assert.Equal(2, result.Rows[1].LeftNumber);
        Assert.Null(result.Rows[1].RightNumber);

        Assert.Equal(3, result.Rows[2].LeftNumber);
        Assert.Equal(2, result.Rows[2].RightNumber);
    }

    [Fact]
    public void A_rewritten_line_is_paired_on_one_row()
    {
        // 上下に離すより、左右に並べた方がどこが書き換わったか分かる
        SideBySideResult result = SideBySideDiff.Build("head\nold\ntail", "head\nnew\ntail");

        SideBySideRow changed = Assert.Single(result.Rows, r => r.Kind == SideKind.Changed);

        Assert.Equal("old", changed.Left);
        Assert.Equal("new", changed.Right);
    }

    [Fact]
    public void A_removed_line_leaves_the_right_side_empty()
    {
        SideBySideResult result = SideBySideDiff.Build("a\nb", "a");

        SideBySideRow row = Assert.Single(result.Rows, r => r.Kind == SideKind.LeftOnly);

        Assert.Equal("b", row.Left);
        Assert.Null(row.Right);
        Assert.Equal(string.Empty, row.RightText);
    }

    [Fact]
    public void An_added_line_leaves_the_left_side_empty()
    {
        SideBySideResult result = SideBySideDiff.Build("a", "a\nb");

        SideBySideRow row = Assert.Single(result.Rows, r => r.Kind == SideKind.RightOnly);

        Assert.Null(row.Left);
        Assert.Equal("b", row.Right);
    }

    [Fact]
    public void Uneven_blocks_pair_up_as_far_as_they_can()
    {
        SideBySideResult result = SideBySideDiff.Build("a\nx1\nx2\nb", "a\ny1\nb");

        Assert.Equal(1, result.Rows.Count(r => r.Kind == SideKind.Changed));   // x1 と y1
        Assert.Equal(1, result.Rows.Count(r => r.Kind == SideKind.LeftOnly));  // 余った x2
    }

    [Fact]
    public void Only_the_differences_can_be_taken_out()
    {
        SideBySideResult result = SideBySideDiff.Build("a\nb\nc", "a\nB\nc");

        Assert.Single(SideBySideDiff.OnlyDifferences(result));
    }

    [Fact]
    public void A_cisco_config_ignores_the_lines_that_always_change()
    {
        // 設定を触っていなくても、取得のたびに必ず変わる行がある
        const string before = """
            Building configuration...

            Current configuration : 4521 bytes
            !
            ! Last configuration change at 09:00:00 JST Fri Aug 15 2026 by admin
            !
            hostname RTR01
            """;
        const string after = """
            Building configuration...

            Current configuration : 4530 bytes
            !
            ! Last configuration change at 11:30:00 JST Fri Aug 15 2026 by admin
            !
            hostname RTR01
            """;

        Assert.True(SideBySideDiff.Build(before, after).HasChanges);
        Assert.False(SideBySideDiff.Build(before, after, DiffNoiseFilter.CiscoConfig).HasChanges);
    }

    [Fact]
    public void A_real_config_change_survives_the_filter()
    {
        const string before = """
            Current configuration : 4521 bytes
            hostname RTR01
            interface GigabitEthernet0/0
             ip address 192.168.1.1 255.255.255.0
            """;
        const string after = """
            Current configuration : 4530 bytes
            hostname RTR01
            interface GigabitEthernet0/0
             ip address 192.168.1.254 255.255.255.0
            """;

        SideBySideResult result = SideBySideDiff.Build(before, after, DiffNoiseFilter.CiscoConfig);

        SideBySideRow changed = Assert.Single(result.Rows, r => r.Kind == SideKind.Changed);
        Assert.Contains("192.168.1.254", changed.RightText, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        Assert.Empty(SideBySideDiff.Build(null, null).Rows);
        Assert.Equal(2, SideBySideDiff.Build("", "a\nb").Rows.Count);
    }
}
