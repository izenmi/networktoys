using PingWatcher.Core.Models;
using PingWatcher.Core.Storage;
using Xunit;

namespace PingWatcher.Core.Tests;

/// <summary>
/// 書式は EXPing の公式ドキュメント(ExPing.txt「§使い方」)に合わせている。
/// 仕様の要点をそのままテストにしている。
/// </summary>
public class TargetListParserTests
{
    [Fact]
    public void Empty_input_yields_nothing()
    {
        Assert.Empty(TargetListParser.Parse(null).Targets);
        Assert.Empty(TargetListParser.Parse(string.Empty).Targets);
    }

    [Fact]
    public void One_address_per_line()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.1\n192.168.1.2\nexample.jp");

        Assert.Equal(3, result.Targets.Count);
        Assert.Equal("192.168.1.1", result.Targets[0].Host);
        Assert.Equal("example.jp", result.Targets[2].Host);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Text_after_the_first_space_is_a_remark()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.1 ルータ 1階 EPS内");

        Assert.Single(result.Targets);
        Assert.Equal("192.168.1.1", result.Targets[0].Host);
        Assert.Equal("ルータ 1階 EPS内", result.Targets[0].Comment);
    }

    [Fact]
    public void A_remark_is_optional()
    {
        Target target = TargetListParser.Parse("192.168.1.1").Targets[0];

        Assert.Equal("192.168.1.1", target.Host);
        Assert.Equal(string.Empty, target.Comment);
    }

    [Fact]
    public void Tabs_also_separate_the_remark()
    {
        // EXPing の仕様は半角スペースだが、表計算から貼るときのために受け付ける
        Target target = TargetListParser.Parse("192.168.1.1\tファイルサーバ").Targets[0];

        Assert.Equal("192.168.1.1", target.Host);
        Assert.Equal("ファイルサーバ", target.Comment);
    }

    [Theory]
    [InlineData("# コメント")]
    [InlineData("; コメント")]
    [InlineData("' コメント")]
    [InlineData("　コメント")]   // 全角スペース始まり
    public void Comment_lines_are_ignored(string line)
    {
        TargetListParseResult result = TargetListParser.Parse(line + "\n192.168.1.1");

        Assert.Single(result.Targets);
        Assert.Equal(1, result.CommentLines);
    }

    [Fact]
    public void A_comment_head_only_counts_at_the_start_of_a_line()
    {
        // 「行の途中から注釈を設定することはできません」
        Target target = TargetListParser.Parse("192.168.1.1 予備 # 未使用").Targets[0];

        Assert.Equal("192.168.1.1", target.Host);
        Assert.Equal("予備 # 未使用", target.Comment);
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.1\n\n\n192.168.1.2\n   \n");

        Assert.Equal(2, result.Targets.Count);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Handles_crlf()
    {
        Assert.Equal(2, TargetListParser.Parse("192.168.1.1\r\n192.168.1.2\r\n").Targets.Count);
    }

    [Fact]
    public void Expands_a_last_octet_range()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.10-13 事務所");

        Assert.Equal(4, result.Targets.Count);
        Assert.Equal("192.168.1.10", result.Targets[0].Host);
        Assert.Equal("192.168.1.13", result.Targets[3].Host);
        Assert.All(result.Targets, t => Assert.Equal("事務所", t.Comment));
        Assert.Equal(4, result.ExpandedCount);
    }

    [Fact]
    public void Expands_a_full_range()
    {
        TargetListParseResult result = TargetListParser.Parse("10.0.0.254-10.0.1.2");

        Assert.Equal(5, result.Targets.Count);
        Assert.Equal("10.0.0.254", result.Targets[0].Host);
        Assert.Equal("10.0.1.2", result.Targets[4].Host);
    }

    [Fact]
    public void Expands_cidr_without_network_and_broadcast()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.0/29");

        Assert.Equal(6, result.Targets.Count);              // 8 から 2 を引く
        Assert.Equal("192.168.1.1", result.Targets[0].Host);
        Assert.Equal("192.168.1.6", result.Targets[5].Host);
    }

    [Fact]
    public void Hostnames_containing_a_hyphen_are_not_treated_as_a_range()
    {
        TargetListParseResult result = TargetListParser.Parse("my-file-server-01 社内NAS");

        Assert.Single(result.Targets);
        Assert.Equal("my-file-server-01", result.Targets[0].Host);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Reversed_ranges_are_reported()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.100-10");

        Assert.Empty(result.Targets);
        Assert.Single(result.Errors);
        Assert.Equal(1, result.Errors[0].LineNumber);
    }

    [Fact]
    public void Oversized_ranges_are_reported_instead_of_expanded()
    {
        TargetListParseResult result = TargetListParser.Parse("10.0.0.0/16", limit: 4096);

        Assert.Empty(result.Targets);
        Assert.Single(result.Errors);
        Assert.Contains("上限", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stops_at_the_limit_and_says_so()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.1-200\n192.168.2.1", limit: 50);

        Assert.True(result.HasErrors);
        Assert.True(result.Targets.Count <= 50);
    }

    [Fact]
    public void A_trailing_port_selects_tcp()
    {
        Target target = TargetListParser.Parse("example.jp:443 Web サーバ").Targets[0];

        Assert.Equal("example.jp", target.Host);
        Assert.Equal(ProbeKind.Tcp, target.Kind);
        Assert.Equal(443, target.Port);
        Assert.Equal("Web サーバ", target.Comment);
    }

    [Fact]
    public void A_port_applies_to_every_address_in_a_range()
    {
        TargetListParseResult result = TargetListParser.Parse("192.168.1.1-3:22 SSH");

        Assert.Equal(3, result.Targets.Count);
        Assert.All(result.Targets, t =>
        {
            Assert.Equal(ProbeKind.Tcp, t.Kind);
            Assert.Equal(22, t.Port);
        });
        Assert.Equal("192.168.1.3", result.Targets[2].Host);
    }

    [Theory]
    [InlineData("example.jp:0")]        // ポート番号として不正
    [InlineData("example.jp:70000")]
    [InlineData("example.jp:abc")]
    [InlineData("example.jp:")]
    [InlineData("fe80::1")]             // IPv6 リテラルはコロンが複数
    public void Invalid_or_ambiguous_ports_stay_icmp(string token)
    {
        Target target = TargetListParser.Parse(token).Targets[0];
        Assert.Equal(ProbeKind.Icmp, target.Kind);
    }

    [Fact]
    public void Round_trips_a_tcp_target_through_Format()
    {
        TargetListParseResult first = TargetListParser.Parse("example.jp:443 Web\n192.168.1.1 GW");
        TargetListParseResult second = TargetListParser.Parse(TargetListParser.Format(first.Targets));

        Assert.Equal(ProbeKind.Tcp, second.Targets[0].Kind);
        Assert.Equal(443, second.Targets[0].Port);
        Assert.Equal(ProbeKind.Icmp, second.Targets[1].Kind);
    }

    [Fact]
    public void Round_trips_through_Format()
    {
        const string text = "192.168.1.1 ルータ\nexample.jp Web サーバ\n10.0.0.1\n";

        TargetListParseResult first = TargetListParser.Parse(text);
        TargetListParseResult second = TargetListParser.Parse(TargetListParser.Format(first.Targets));

        Assert.Equal(first.Targets.Count, second.Targets.Count);
        for (int i = 0; i < first.Targets.Count; i++)
        {
            Assert.Equal(first.Targets[i].Host, second.Targets[i].Host);
            Assert.Equal(first.Targets[i].Comment, second.Targets[i].Comment);
        }
    }

    [Fact]
    public void A_realistic_list_parses_as_expected()
    {
        const string text = """
            # 本社ネットワーク
            192.168.1.1 既定ゲートウェイ
            192.168.1.10 ファイルサーバ
            192.168.1.20-22 プリンタ

            ; 外部疎通
            8.8.8.8 Google Public DNS
            example.jp 自社サイト
            """;

        TargetListParseResult result = TargetListParser.Parse(text);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.CommentLines);
        Assert.Equal(7, result.Targets.Count);   // 2 + 3 + 1 + 1
        Assert.Equal("プリンタ", result.Targets[3].Comment);
        Assert.Equal("192.168.1.22", result.Targets[4].Host);
    }

    [Fact]
    public void An_indented_comment_line_does_not_become_a_target()
    {
        // 以前は「  # 予備機」が Host="#"・備考「予備機」の偽の宛先になっていた
        TargetListParseResult result = TargetListParser.Parse("  # 予備機\n192.168.1.1");

        Assert.Equal("192.168.1.1", Assert.Single(result.Targets).Host);
        Assert.Equal(1, result.CommentLines);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void A_broken_cidr_is_an_error_not_a_hostname()
    {
        // ホスト名に「/」は使えない。丸ごとホスト名として登録すると
        // 実行時に DNS 失敗の行として一覧に混ざり、原因に辿り着けない
        TargetListParseResult result = TargetListParser.Parse("999.999.999.999/24");

        Assert.Empty(result.Targets);
        Assert.True(result.HasErrors);
    }
}
