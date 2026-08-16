using System.Net;
using PingWatcher.Core.Net;
using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class WfpBlockedEventsTests
{
    private static readonly DateTime Base = new(2026, 8, 16, 1, 2, 3, DateTimeKind.Utc);

    private static WfpBlockedEvent Event(
        string app = @"\device\harddiskvolume4\windows\system32\svchost.exe",
        string remote = "203.0.113.9",
        ushort remotePort = 443,
        byte protocol = 6,
        ulong filterId = 12345,
        int secondsLater = 0)
        => new(
            TimeUtc: Base.AddSeconds(secondsLater),
            Direction: WfpDirection.Outbound,
            Protocol: protocol,
            Local: IPAddress.Parse("192.168.1.10"),
            LocalPort: 51234,
            Remote: IPAddress.Parse(remote),
            RemotePort: remotePort,
            ScopeId: 0,
            AppIdRaw: app,
            FilterId: filterId,
            LayerId: 44,
            IsLoopback: false);

    // ===== バイトオーダー =====

    [Fact]
    public void Ipv4_addresses_are_read_in_host_order()
    {
        // WFP は 192.168.1.10 を 0xC0A8010A で返す。iphlpapi のテーブルとは逆で、
        // 写経すると 10.1.168.192 になる
        Assert.Equal(IPAddress.Parse("192.168.1.10"), WfpFormat.Ipv4FromHostOrder(0xC0A8010A));
        Assert.Equal(IPAddress.Parse("0.0.0.0"), WfpFormat.Ipv4FromHostOrder(0));
        Assert.Equal(IPAddress.Parse("255.255.255.255"), WfpFormat.Ipv4FromHostOrder(0xFFFFFFFF));
    }

    // ===== 表示文字列 =====

    [Theory]
    [InlineData(1, "ICMP")]
    [InlineData(6, "TCP")]
    [InlineData(17, "UDP")]
    [InlineData(58, "ICMPv6")]
    [InlineData(253, "253")]   // 知らない番号は番号のまま
    public void Protocols_are_named_or_left_as_numbers(byte protocol, string expected)
        => Assert.Equal(expected, WfpFormat.Protocol(protocol));

    [Fact]
    public void Directions_carry_a_symbol_and_a_word()
    {
        Assert.Equal("→ 送信", WfpFormat.Direction(WfpDirection.Outbound));
        Assert.Equal("← 受信", WfpFormat.Direction(WfpDirection.Inbound));
        Assert.Equal("—", WfpFormat.Direction(WfpDirection.Unknown));

        Assert.Equal(WfpDirection.Outbound, WfpFormat.DirectionOf(0));
        Assert.Equal(WfpDirection.Inbound, WfpFormat.DirectionOf(1));
        // 知らない値でも例外にせず「不明」に寄せる
        Assert.Equal(WfpDirection.Unknown, WfpFormat.DirectionOf(7));
    }

    [Fact]
    public void Endpoints_bracket_ipv6_and_keep_the_scope()
    {
        Assert.Equal("192.168.1.10:443", WfpFormat.Endpoint(IPAddress.Parse("192.168.1.10"), 443, 0));
        Assert.Equal("192.168.1.10", WfpFormat.Endpoint(IPAddress.Parse("192.168.1.10"), 0, 0));
        Assert.Equal("[fe80::1%12]:443", WfpFormat.Endpoint(IPAddress.Parse("fe80::1"), 443, 12));
        Assert.Equal("[2001:db8::1]:80", WfpFormat.Endpoint(IPAddress.Parse("2001:db8::1"), 80, 0));
        Assert.Equal("—", WfpFormat.Endpoint(null, 443, 0));
    }

    // ===== NT パス =====

    [Fact]
    public void App_id_bytes_are_decoded_without_the_terminator()
    {
        byte[] withNul = System.Text.Encoding.Unicode.GetBytes("\\device\\a\\b.exe\0");
        byte[] withoutNul = System.Text.Encoding.Unicode.GetBytes("\\device\\a\\b.exe");

        // 終端を含む場合と含まない場合で同じ結果になること(size の解釈に依存しない)
        Assert.Equal("\\device\\a\\b.exe", NtPathText.FromBlobBytes(withNul));
        Assert.Equal("\\device\\a\\b.exe", NtPathText.FromBlobBytes(withoutNul));
    }

    [Fact]
    public void Odd_or_tiny_app_id_buffers_do_not_throw()
    {
        Assert.Equal("", NtPathText.FromBlobBytes([]));
        Assert.Equal("", NtPathText.FromBlobBytes([0x41]));

        // 奇数バイトは最後の 1 バイトを捨てる
        byte[] odd = [.. System.Text.Encoding.Unicode.GetBytes("ab"), 0x41];
        Assert.Equal("ab", NtPathText.FromBlobBytes(odd));
    }

    [Fact]
    public void File_names_are_taken_from_the_end_of_the_nt_path()
    {
        Assert.Equal("chrome.exe", NtPathText.FileName(@"\device\harddiskvolume4\program files\chrome.exe"));
        Assert.Equal("", NtPathText.FileName(""));
        Assert.Equal("bare.exe", NtPathText.FileName("bare.exe"));
        // 末尾が \ ならファイル名が無い。空欄にせず元の文字列を残す
        Assert.Equal(@"\device\a\", NtPathText.FileName(@"\device\a\"));
    }

    // ===== 畳み込み =====

    [Fact]
    public void Identical_blocks_are_folded_into_one_row_with_a_count()
    {
        IReadOnlyList<WfpBlockedRow> rows = WfpEventView.Group(
        [
            Event(secondsLater: 0),
            Event(secondsLater: 5),
            Event(secondsLater: 9),
        ]);

        WfpBlockedRow row = Assert.Single(rows);
        Assert.Equal("3", row.CountText);
        // 時刻は最後に起きたもの
        Assert.Equal(Base.AddSeconds(9), row.LatestUtc);
        Assert.Equal("svchost.exe", row.ProcessName);
    }

    [Fact]
    public void Different_destinations_or_filters_stay_separate()
    {
        IReadOnlyList<WfpBlockedRow> rows = WfpEventView.Group(
        [
            Event(remote: "203.0.113.9"),
            Event(remote: "198.51.100.4"),
            Event(remote: "203.0.113.9", remotePort: 80),
            Event(remote: "203.0.113.9", filterId: 999),
        ]);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void Newest_blocks_come_first_by_default()
    {
        IReadOnlyList<WfpBlockedRow> rows = WfpEventView.Group(
        [
            Event(remote: "203.0.113.9", secondsLater: 0),
            Event(remote: "198.51.100.4", secondsLater: 60),
        ]);

        Assert.Equal("198.51.100.4:443", rows[0].Remote);
    }

    [Fact]
    public void Sorting_by_a_column_is_a_total_order()
    {
        // 件数が同じ行が複数あっても並びが揺れないこと(揺れると差分同期が全行を書き換える)
        WfpBlockedEvent[] events =
        [
            Event(remote: "203.0.113.9"),
            Event(remote: "198.51.100.4"),
            Event(remote: "192.0.2.7"),
        ];

        string[] first = [.. WfpEventView.Group(events, sortColumn: "Count").Select(r => r.SortKey)];
        string[] second = [.. WfpEventView.Group(events, sortColumn: "Count").Select(r => r.SortKey)];

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_filter_matches_process_and_address()
    {
        WfpBlockedEvent[] events =
        [
            Event(app: @"\device\hd\chrome.exe", remote: "203.0.113.9"),
            Event(app: @"\device\hd\svchost.exe", remote: "198.51.100.4"),
        ];

        Assert.Single(WfpEventView.Group(events, filter: "chrome"));
        Assert.Single(WfpEventView.Group(events, filter: "198.51"));
        Assert.Equal(2, WfpEventView.Group(events, filter: "device").Count);
        Assert.Empty(WfpEventView.Group(events, filter: "見つからない"));
    }

    [Fact]
    public void Kernel_traffic_without_an_app_id_still_shows_up()
    {
        IReadOnlyList<WfpBlockedRow> rows = WfpEventView.Group([Event(app: "")]);

        Assert.Equal("—", Assert.Single(rows).ProcessName);
    }

    // ===== CSV =====

    [Fact]
    public void Csv_keeps_one_column_per_header_and_carries_the_full_path()
    {
        CsvTable table = WfpEventView.ToCsv(WfpEventView.Group([Event()]));

        Assert.Equal(10, table.Headers.Count);
        Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
        // 一覧はファイル名だけだが、CSV には完全な NT パスを残す
        Assert.Contains(@"\device\harddiskvolume4\windows\system32\svchost.exe", table.ToCsv());
    }

    [Fact]
    public void Csv_neutralises_paths_that_look_like_formulas()
    {
        string csv = WfpEventView.ToCsv(WfpEventView.Group([Event(app: "=cmd|'/c calc'!A1")])).ToCsv();

        Assert.Contains("'=cmd", csv);
    }
}
