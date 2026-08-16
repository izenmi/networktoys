using PingWatcher.Core.Terminal;
using Xunit;

namespace PingWatcher.Core.Tests;

public class CiscoPromptTests
{
    [Theory]
    [InlineData("R1>", "R1", PromptLevel.User)]
    [InlineData("R1#", "R1", PromptLevel.Enable)]
    [InlineData("R1(config)#", "R1", PromptLevel.Config)]
    [InlineData("R1(config-if)#", "R1", PromptLevel.Config)]
    [InlineData("sw-3f.example#", "sw-3f.example", PromptLevel.Enable)]
    public void Prompts_at_the_end_are_recognised(string buffer, string host, PromptLevel level)
    {
        Assert.True(CiscoPrompt.TryMatchAtEnd(buffer, null, out PromptMatch match));
        Assert.Equal(host, match.Hostname);
        Assert.Equal(level, match.Level);
    }

    [Fact]
    public void A_prompt_followed_by_a_newline_is_output_not_a_prompt()
    {
        // 機器の出力行は必ず改行で終わる。プロンプトだけは改行せずに止まる
        Assert.False(CiscoPrompt.TryMatchAtEnd("R1#\n", null, out _));
        Assert.False(CiscoPrompt.TryMatchAtEnd("R1#\nshow version\n", null, out _));
    }

    [Fact]
    public void A_different_hostname_is_not_our_prompt()
    {
        Assert.True(CiscoPrompt.TryMatchAtEnd("R1#", "R1", out _));
        Assert.False(CiscoPrompt.TryMatchAtEnd("R2#", "R1", out _));
    }

    [Fact]
    public void The_hostname_is_learned_from_the_prompt()
    {
        Assert.Equal("SW-4F", CiscoPrompt.LearnHostname("banner text\nSW-4F>"));
        Assert.Null(CiscoPrompt.LearnHostname("banner text\n"));
    }

    [Fact]
    public void Config_text_containing_a_hash_does_not_look_like_a_prompt()
    {
        // 設定の中に出てくる文字列に引っかからないこと
        Assert.False(CiscoPrompt.TryMatchAtEnd("hostname R1\n", "R1", out _));
        Assert.False(CiscoPrompt.TryMatchAtEnd("! comment #", "R1", out _));
    }

    [Fact]
    public void Login_and_pager_prompts_are_recognised()
    {
        Assert.True(CiscoPrompt.EndsWithUsernamePrompt("Username: "));
        Assert.True(CiscoPrompt.EndsWithUsernamePrompt("login:"));
        Assert.True(CiscoPrompt.EndsWithPasswordPrompt("Password: "));
        Assert.True(CiscoPrompt.EndsWithMore("...\n --More-- "));
        Assert.False(CiscoPrompt.EndsWithPasswordPrompt("Password: wrong\nR1>"));
    }

    [Theory]
    [InlineData("Proceed with reload? [confirm]")]
    [InlineData("Do you want to continue? [yes/no]:")]
    [InlineData("Destination filename [startup-config]?")]
    [InlineData("Are you sure you want to continue")]
    public void Confirmation_prompts_are_recognised(string buffer)
        => Assert.True(CiscoPrompt.EndsWithConfirmPrompt(buffer));

    [Fact]
    public void Authentication_failures_get_a_japanese_reason()
    {
        Assert.Contains("パスワード", CiscoPrompt.DetectAuthFailure("% Login invalid")!);
        Assert.Contains("拒否", CiscoPrompt.DetectAuthFailure("% Access denied")!);
        Assert.Null(CiscoPrompt.DetectAuthFailure("R1>"));
    }

    [Fact]
    public void Command_problems_get_a_japanese_reason()
    {
        Assert.Contains("使えない", CiscoPrompt.DetectCommandProblem("% Invalid input detected at '^' marker.")!);
        Assert.Contains("許可", CiscoPrompt.DetectCommandProblem("Command authorization failed.")!);
        Assert.Null(CiscoPrompt.DetectCommandProblem("Cisco IOS Software, Version 15.2"));
    }
}

public class CiscoCommandGuardTests
{
    [Theory]
    [InlineData("reload")]
    [InlineData("rel")]
    [InlineData("configure terminal")]
    [InlineData("conf t")]
    [InlineData("wr")]
    [InlineData("write memory")]
    [InlineData("copy running-config startup-config")]
    [InlineData("clear counters")]
    [InlineData("cl ip route *")]
    [InlineData("debug ip packet")]
    [InlineData("erase startup-config")]
    [InlineData("delete flash:test")]
    public void Dangerous_commands_are_blocked_with_a_reason(string command)
    {
        CommandVerdict verdict = CiscoCommandGuard.Classify(command);

        Assert.Equal(CommandRisk.Blocked, verdict.Risk);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Theory]
    [InlineData("show version")]
    [InlineData("sh run")]
    [InlineData("dir flash:")]
    [InlineData("terminal length 0")]
    [InlineData("show run | include !")]
    public void Read_only_commands_are_allowed(string command)
        => Assert.Equal(CommandRisk.Allowed, CiscoCommandGuard.Classify(command).Risk);

    [Fact]
    public void Ping_needs_a_destination_or_it_opens_the_interactive_mode()
    {
        Assert.Equal(CommandRisk.Allowed, CiscoCommandGuard.Classify("ping 8.8.8.8").Risk);
        Assert.Equal(CommandRisk.Blocked, CiscoCommandGuard.Classify("ping").Risk);
        Assert.Equal(CommandRisk.Allowed, CiscoCommandGuard.Classify("traceroute 8.8.8.8").Risk);
        Assert.Equal(CommandRisk.Blocked, CiscoCommandGuard.Classify("traceroute").Risk);
    }

    [Fact]
    public void Unknown_verbs_are_warned_not_blocked()
        => Assert.Equal(CommandRisk.Warned, CiscoCommandGuard.Classify("monitor session 1").Risk);

    [Fact]
    public void Case_and_extra_spaces_do_not_matter()
    {
        Assert.Equal(CommandRisk.Blocked, CiscoCommandGuard.Classify("  RELOAD  ").Risk);
        Assert.Equal(CommandRisk.Allowed, CiscoCommandGuard.Classify("  SHOW   version ").Risk);
    }

    [Fact]
    public void The_recommended_defaults_are_never_blocked_by_our_own_rules()
    {
        // 初期値が自分のブロック規則に引っかかる、という間抜けな退行を防ぐ
        foreach ((string name, string commands) in RecommendedCommands.Presets)
        {
            CommandListParseResult parsed = CommandListParser.Parse(commands);

            Assert.Empty(parsed.Errors);
            Assert.All(parsed.Commands, c =>
                Assert.True(c.Risk != CommandRisk.Blocked, $"{name}: {c.Command} が弾かれた"));
            Assert.True(parsed.RunnableCount > 0, $"{name}: 実行できるコマンドが無い");
        }
    }
}

public class CommandListParserTests
{
    [Fact]
    public void Comment_lines_and_blanks_are_skipped()
    {
        CommandListParseResult result = CommandListParser.Parse("""
            ! これは注釈
            show version

            # これも注釈
            show clock
            """);

        Assert.Equal(2, result.Commands.Count);
        Assert.Equal(2, result.CommentLines);
    }

    [Fact]
    public void A_pipe_to_include_a_bang_is_not_treated_as_a_comment()
    {
        // 行内注釈を解釈すると、この正当なコマンドが壊れる
        CommandListParseResult result = CommandListParser.Parse("show run | include !");

        Assert.Equal("show run | include !", Assert.Single(result.Commands).Command);
    }

    [Fact]
    public void Line_numbers_are_kept_for_the_error_message()
    {
        CommandListParseResult result = CommandListParser.Parse("!c\n\nshow version");

        Assert.Equal(3, Assert.Single(result.Commands).LineNumber);
    }

    [Fact]
    public void Too_many_lines_are_reported_not_silently_dropped()
    {
        string many = string.Join('\n', Enumerable.Repeat("show clock", 250));
        CommandListParseResult result = CommandListParser.Parse(many, limit: 10);

        Assert.Equal(10, result.Commands.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Empty_input_is_not_an_error()
    {
        Assert.Empty(CommandListParser.Parse(null).Commands);
        Assert.Empty(CommandListParser.Parse("   ").Commands);
    }
}

public class DeviceListParserTests
{
    [Fact]
    public void A_line_carries_the_host_method_user_and_memo()
    {
        DeviceListParseResult result = DeviceListParser.Parse("""
            ! 3F の島ハブ
            192.168.10.1,ssh,admin
            sw-3f.example.local,telnet,wataru,3階 EPS
            172.16.0.1
            """, defaultUseSsh: true);

        Assert.Equal(3, result.Devices.Count);
        Assert.Equal(1, result.CommentLines);

        Assert.Equal(new DeviceEntry("192.168.10.1", true, "admin", ""), result.Devices[0]);
        Assert.Equal(new DeviceEntry("sw-3f.example.local", false, "wataru", "3階 EPS"), result.Devices[1]);
        Assert.Equal(new DeviceEntry("172.16.0.1", true, "", ""), result.Devices[2]);
    }

    [Fact]
    public void The_port_comes_from_the_method_not_from_the_text()
    {
        DeviceListParseResult result = DeviceListParser.Parse("a,ssh,u\nb,telnet,u", defaultUseSsh: true);

        Assert.Equal(22, result.Devices[0].Port);
        Assert.Equal(23, result.Devices[1].Port);
    }

    [Fact]
    public void Lines_written_before_the_method_existed_are_still_read()
    {
        // 2 番目が ssh/telnet でなければユーザー名として読む
        DeviceListParseResult result = DeviceListParser.Parse("10.0.0.1,admin,コア", defaultUseSsh: false);

        Assert.Equal(new DeviceEntry("10.0.0.1", false, "admin", "コア"), Assert.Single(result.Devices));
    }

    [Fact]
    public void Tab_separated_pastes_from_a_spreadsheet_work()
    {
        DeviceListParseResult result = DeviceListParser.Parse("10.0.0.1\tssh\tadmin\tコア", defaultUseSsh: false);

        Assert.Equal(new DeviceEntry("10.0.0.1", true, "admin", "コア"), Assert.Single(result.Devices));
    }

    [Fact]
    public void Ipv6_literals_survive()
    {
        DeviceListParseResult result = DeviceListParser.Parse("2001:db8::1,ssh,admin", defaultUseSsh: true);

        Assert.Equal("2001:db8::1", Assert.Single(result.Devices).Host);
    }

    [Fact]
    public void A_line_without_a_host_is_reported_with_its_line_number()
    {
        DeviceListParseResult result = DeviceListParser.Parse(",ssh,admin", defaultUseSsh: true);

        Assert.Empty(result.Devices);
        Assert.Contains("1 行目", Assert.Single(result.Errors));
    }

    [Fact]
    public void Formatting_and_parsing_round_trip()
    {
        DeviceEntry[] devices =
        [
            new("10.0.0.1", true, "admin", ""),
            new("sw.example", false, "wataru", "3階"),
        ];

        string text = DeviceListParser.Format(devices);

        Assert.Equal(devices, DeviceListParser.Parse(text, defaultUseSsh: true).Devices);
    }

    [Fact]
    public void Duplicate_hosts_are_kept()
    {
        // 同じ機器を 2 回叩きたい使い方がある
        DeviceListParseResult result = DeviceListParser.Parse("10.0.0.1,ssh,a\n10.0.0.1,ssh,b", defaultUseSsh: true);

        Assert.Equal(2, result.Devices.Count);
    }
}

public class DeviceReportTests
{
    private static DeviceCollectionResult Sample(params CommandResult[] commands) => new(
        Host: "192.168.10.1",
        Port: 22,
        LearnedHostname: "R1",
        UserName: "admin",
        UsedSsh: true,
        HostKeyFingerprint: "SHA256:abc",
        StartedAt: new DateTime(2026, 8, 16, 15, 30, 12, DateTimeKind.Local),
        FinishedAt: new DateTime(2026, 8, 16, 15, 31, 40, DateTimeKind.Local),
        ReachedEnable: true,
        PagerNote: "無効化を実施",
        Commands: commands,
        FailureMessage: null);

    [Fact]
    public void Every_command_gets_its_own_section()
    {
        string text = DeviceReport.Render(Sample(
            new CommandResult(1, "show clock", "15:30:13 JST\n", TimeSpan.FromSeconds(0.3), null),
            new CommandResult(2, "show version", "Cisco IOS\n", TimeSpan.FromSeconds(1.2), null)));

        Assert.Contains("===[ 1/2 ] show clock", text);
        Assert.Contains("===[ 2/2 ] show version", text);
        Assert.Contains("15:30:13 JST", text);
    }

    [Fact]
    public void A_command_that_failed_says_why_in_japanese()
    {
        string text = DeviceReport.Render(Sample(
            new CommandResult(1, "show vpc", "", TimeSpan.Zero, "この機器では使えないコマンドです。")));

        Assert.Contains("この機器では使えないコマンドです。", text);
    }

    [Fact]
    public void The_saved_text_never_contains_a_password()
    {
        // 認証情報は入れ物すら作っていないが、退行したら必ず気づけるようにする
        string text = DeviceReport.Render(Sample(
            new CommandResult(1, "show clock", "15:30:13\n", TimeSpan.FromSeconds(0.3), null)));

        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Line_endings_are_crlf_for_notepad()
    {
        string text = DeviceReport.Render(Sample(
            new CommandResult(1, "show clock", "a\nb\n", TimeSpan.Zero, null)));

        Assert.Contains("\r\n", text);

        // 裸の LF が 1 つも残っていないこと(メモ帳で 1 行に潰れる)
        Assert.DoesNotContain('\n', text.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("R1", "R1")]
    [InlineData("sw/3f", "sw3f")]
    [InlineData("a:b*c?", "abc")]
    [InlineData("CON", "CON_")]
    [InlineData("name.", "name")]
    [InlineData("", "")]
    // CSV の書き出しも通す。宛先をファイル名に混ぜる画面(経路の trace-<宛先>)があり、
    // IPv6 を入れられるとコロンだらけの名前になる
    [InlineData("trace-2001:db8::1", "trace-2001db81")]
    public void File_names_drop_what_windows_cannot_use(string input, string expected)
        => Assert.Equal(expected, DeviceReport.Sanitize(input));

    [Fact]
    public void A_nameless_device_still_gets_a_file_name()
    {
        DeviceCollectionResult result = Sample() with { LearnedHostname = null };

        Assert.StartsWith("192.168.10.1_", DeviceReport.FileName(result));
        Assert.EndsWith(".txt", DeviceReport.FileName(result));
    }
}
