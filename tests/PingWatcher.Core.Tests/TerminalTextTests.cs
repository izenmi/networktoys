using PingWatcher.Core.Terminal;
using Xunit;

namespace PingWatcher.Core.Tests;

public class TelnetFilterTests
{
    private const byte Iac = 255;
    private const byte Se = 240;
    private const byte Sb = 250;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    private static (string Payload, byte[] Reply) Run(byte[] input, int chunkSize = int.MaxValue)
    {
        var filter = new TelnetFilter();
        var payload = new List<byte>();
        var replies = new List<byte>();
        byte[] buffer = new byte[input.Length];

        for (int offset = 0; offset < input.Length; offset += chunkSize)
        {
            int size = Math.Min(chunkSize, input.Length - offset);
            int written = filter.Process(input.AsSpan(offset, size), buffer, out byte[] reply);

            payload.AddRange(buffer[..written]);
            replies.AddRange(reply);
        }

        return (System.Text.Encoding.ASCII.GetString([.. payload]), [.. replies]);
    }

    [Fact]
    public void Plain_bytes_pass_through_untouched()
        => Assert.Equal("R1#show ver", Run(System.Text.Encoding.ASCII.GetBytes("R1#show ver")).Payload);

    [Fact]
    public void Every_offer_is_refused()
    {
        (_, byte[] reply) = Run([Iac, Do, 1, Iac, Will, 3]);

        // DO には WONT、WILL には DONT を返す(こちらは端末エミュレータではない)
        Assert.Equal(new byte[] { Iac, Wont, 1, Iac, Dont, 3 }, reply);
    }

    [Fact]
    public void Refusals_from_the_other_side_need_no_answer()
        => Assert.Empty(Run([Iac, Dont, 1, Iac, Wont, 3]).Reply);

    [Fact]
    public void Escaped_ff_becomes_a_single_data_byte()
    {
        (string payload, _) = Run([0x41, Iac, Iac, 0x42]);

        Assert.Equal(3, payload.Length);
        Assert.Equal('A', payload[0]);
        Assert.Equal('B', payload[2]);
    }

    [Fact]
    public void Subnegotiation_is_swallowed_whole()
    {
        (string payload, byte[] reply) = Run([0x41, Iac, Sb, 24, 0, (byte)'X', Iac, Se, 0x42]);

        Assert.Equal("AB", payload);
        Assert.Empty(reply);
    }

    [Fact]
    public void The_same_bytes_survive_every_split_position()
    {
        // IAC の並びは TCP のセグメント境界をまたぐ。持ち越せないと稀に化ける
        byte[] input =
        [
            .. System.Text.Encoding.ASCII.GetBytes("R1"),
            Iac, Do, 1,
            .. System.Text.Encoding.ASCII.GetBytes("#sh"),
            Iac, Sb, 24, 0, Iac, Se,
            Iac, Iac,
            .. System.Text.Encoding.ASCII.GetBytes("ow"),
        ];

        (string whole, byte[] wholeReply) = Run(input);

        for (int chunk = 1; chunk <= input.Length; chunk++)
        {
            (string split, byte[] splitReply) = Run(input, chunk);

            Assert.Equal(whole, split);
            Assert.Equal(wholeReply, splitReply);
        }
    }
}

public class TerminalTextTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void Backspaces_erase_the_more_prompt_without_leaving_a_gap()
    {
        // IOS はスペースを受け取ると --More-- をバックスペースで消しにくる
        string raw = "line1\n--More--" + new string('\b', 8) + new string(' ', 8) + new string('\b', 8) + "line2\n";

        Assert.Equal("line1\nline2\n", TerminalText.Clean(raw));
    }

    [Fact]
    public void Backspaces_do_not_eat_past_the_start_of_a_line()
    {
        // 行頭を越えて食うと前の行が壊れる
        Assert.Equal("abc\nx", TerminalText.Clean("abc\n" + new string('\b', 5) + "x"));
    }

    [Fact]
    public void Ansi_colours_are_removed_but_the_text_stays()
    {
        Assert.Equal("R1#", TerminalText.Clean($"{Esc}[1;32mR1#{Esc}[0m"));
        Assert.Equal("ab", TerminalText.Clean($"a{Esc}[2Kb"));
    }

    [Fact]
    public void Osc_sequences_are_removed()
        => Assert.Equal("ab", TerminalText.Clean($"a{Esc}]0;title\u0007b"));

    [Fact]
    public void Newlines_are_normalised_and_noise_control_characters_go()
    {
        Assert.Equal("a\nb\nc\n", TerminalText.Clean("a\r\nb\rc\n"));
        Assert.Equal("ab", TerminalText.Clean("a\0\u0007b"));
    }

    [Fact]
    public void Japanese_text_is_left_alone()
        => Assert.Equal("説明 3階EPS\n", TerminalText.Clean("説明 3階EPS\r\n"));

    [Fact]
    public void Leftover_more_prompts_are_swept_up()
        => Assert.Equal("ab", TerminalText.Clean("a--More--b"));

    [Fact]
    public void An_empty_string_stays_empty()
        => Assert.Equal("", TerminalText.Clean(""));
}
