using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

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
    public void Twenty_thousand_lines_are_compared_not_refused()
    {
        // 2 万行の設定どうし。ところどころ違うのが本番の形
        string[] lines = [.. Enumerable.Range(0, 20_000).Select(i => $"  set property-{i} value-{i}")];

        string before = string.Join('\n', lines);

        lines[5_000] = "  set property-5000 value-CHANGED";
        lines[19_000] = "  set property-19000 value-CHANGED";

        string after = string.Join('\n', lines);

        TextDiffResult result = TextDiff.Compare(before, after);

        Assert.False(result.TooLarge);

        // 変わった 2 行が「消えた + 現れた」で 4 行。ほかは動かさない
        Assert.Equal(2, result.Lines.Count(l => l.Kind == DiffKind.Removed));
        Assert.Equal(2, result.Lines.Count(l => l.Kind == DiffKind.Added));
        Assert.Equal(20_000 - 2, result.Lines.Count(l => l.Kind == DiffKind.Unchanged));
    }

    [Fact]
    public void Insertions_in_a_large_file_do_not_shift_everything()
    {
        // 途中に 3 行入っただけ。後ろが全部ずれて出ると差分として使い物にならない
        string before = string.Join('\n', Enumerable.Range(0, 10_000).Select(i => $"line {i}"));

        string after = string.Join('\n', Enumerable.Range(0, 10_000)
            .Select(i => i == 4_000 ? "added A\nadded B\nadded C\nline 4000" : $"line {i}"));

        TextDiffResult result = TextDiff.Compare(before, after);

        Assert.False(result.TooLarge);
        Assert.Equal(3, result.Lines.Count(l => l.Kind == DiffKind.Added));
        Assert.DoesNotContain(result.Lines, l => l.Kind == DiffKind.Removed);
    }

    [Fact]
    public void Absurd_input_is_still_given_up_on()
    {
        string huge = string.Join('\n', Enumerable.Range(0, 200_001));

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

    [Fact]
    public void show_ip_routeの経過時間だけの違いは差分にしない()
    {
        // 上段の「経路として見た変化」は構造化側が時間を読み捨てるが、
        // 下段の行差分が素通しで、時間の違いが差分に出ていた(2026-08-21 報告)
        const string before = "O        10.0.2.0/24 [110/2] via 192.168.1.2, 00:12:34, GigabitEthernet0/1";
        const string aged = "O        10.0.2.0/24 [110/2] via 192.168.1.2, 2d05h, GigabitEthernet0/1";

        Assert.True(TextDiff.Compare(before, aged).HasChanges);
        Assert.False(TextDiff.Compare(before, aged, DiffNoiseFilter.CiscoRoutes).HasChanges);

        // BGP のようにインターフェイス無しで時間が行末に来る形も落ちる
        Assert.False(TextDiff.Compare(
            "B        10.9.0.0/16 [20/0] via 198.51.100.7, 3w4d",
            "B        10.9.0.0/16 [20/0] via 198.51.100.7, 1y2w",
            DiffNoiseFilter.CiscoRoutes).HasChanges);
    }

    [Fact]
    public void 向き先の変わった経路は時間の掃除をしても差分に残る()
    {
        const string before = "O        10.0.2.0/24 [110/2] via 192.168.1.2, 00:12:34, GigabitEthernet0/1";
        const string moved = "O        10.0.2.0/24 [110/2] via 192.168.1.9, 00:00:05, GigabitEthernet0/1";

        TextDiffResult result = TextDiff.Compare(before, moved, DiffNoiseFilter.CiscoRoutes);

        Assert.True(result.HasChanges);
        Assert.Contains(result.Lines, l => l.Kind == DiffKind.Added && l.Text.Contains("192.168.1.9", StringComparison.Ordinal));

        // 時間を持たない行(直結)はそのまま比べられる
        const string direct = "C        192.168.1.0/24 is directly connected, GigabitEthernet0/1";
        Assert.False(TextDiff.Compare(direct, direct, DiffNoiseFilter.CiscoRoutes).HasChanges);
    }

    [Fact]
    public void Completely_different_inputs_are_shown_as_a_full_replacement()
    {
        // 共通の行が 1 本も無い。行どうしの対応は付けようがないので、
        // 「まるごと消えて、まるごと現れた」として見せる（諦めて空にしない）
        string before = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"before {i}"));
        string after = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"after {i}"));

        TextDiffResult result = TextDiff.Compare(before, after);

        Assert.False(result.TooLarge);
        Assert.Equal(3000, result.Lines.Count(l => l.Kind == DiffKind.Removed));
        Assert.Equal(3000, result.Lines.Count(l => l.Kind == DiffKind.Added));
    }
}
