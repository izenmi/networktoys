using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class IntralineDiffTests
{
    private static string Render(IReadOnlyList<DiffSegment> segments)
        => string.Concat(segments.Select(s => s.Changed ? $"[{s.Text}]" : s.Text));

    [Fact]
    public void Only_the_changed_value_is_marked()
    {
        (var left, var right) = IntralineDiff.Split(
            " ip address 192.168.1.1 255.255.255.0",
            " ip address 192.168.2.1 255.255.255.0");

        Assert.Equal(" ip address 192.168.[1].1 255.255.255.0", Render(left));
        Assert.Equal(" ip address 192.168.[2].1 255.255.255.0", Render(right));
    }

    [Fact]
    public void An_insertion_marks_only_one_side()
    {
        (var left, var right) = IntralineDiff.Split(
            "interface Gi0/1",
            "interface Gi0/1 shutdown");

        Assert.Equal("interface Gi0/1", Render(left));
        Assert.Equal("interface Gi0/1[ shutdown]", Render(right));
        Assert.DoesNotContain(left, s => s.Changed);
    }

    [Fact]
    public void Completely_different_lines_are_fully_marked()
    {
        (var left, var right) = IntralineDiff.Split("abc", "xyz");

        Assert.Equal("[abc]", Render(left));
        Assert.Equal("[xyz]", Render(right));
    }

    [Fact]
    public void Identical_lines_have_no_marks()
    {
        (var left, var right) = IntralineDiff.Split("no changes here", "no changes here");

        Assert.Equal("no changes here", Render(left));
        Assert.DoesNotContain(left, s => s.Changed);
        Assert.DoesNotContain(right, s => s.Changed);
    }

    [Fact]
    public void Prefix_and_suffix_do_not_overlap()
    {
        // "aaa" と "aa" — 前置きと後置きが重なりうる形。文字が消えただけとして扱う
        (var left, var right) = IntralineDiff.Split("aaa", "aa");

        Assert.Equal("aaa", Render(left).Replace("[", "").Replace("]", ""));
        Assert.Equal("aa", Render(right).Replace("[", "").Replace("]", ""));
        Assert.Equal(1, left.Count(s => s.Changed));
        Assert.DoesNotContain(right, s => s.Changed);
    }

    [Fact]
    public void Changed_rows_expose_segments_and_other_rows_stay_whole()
    {
        var changed = new SideBySideRow(1, "hostname sw-old", 1, "hostname sw-new", SideKind.Changed);
        Assert.Contains(changed.LeftSegments, s => s.Changed);
        Assert.Contains(changed.RightSegments, s => s.Changed);

        var same = new SideBySideRow(2, "line vty 0 4", 2, "line vty 0 4", SideKind.Same);
        DiffSegment single = Assert.Single(same.LeftSegments);
        Assert.False(single.Changed);

        var leftOnly = new SideBySideRow(3, "old line", null, null, SideKind.LeftOnly);
        Assert.Single(leftOnly.RightSegments);   // 空でも 1 区切り(描画側が分岐しなくて済む)
    }
}
