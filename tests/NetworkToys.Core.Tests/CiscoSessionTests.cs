using System.Text;
using NetworkToys.Core.Terminal;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// 偽の Cisco 機器。台本どおりに返すだけのメモリ上のストリーム。
///
/// <see cref="CiscoSession"/> が <see cref="Stream"/> しか知らないので、
/// 実機も実ネットワークも無しにログイン〜収集〜切断を丸ごと走らせられる。
/// CI が唯一の実行環境というこのリポジトリでは、ここが正しさの拠り所になる。
/// </summary>
internal sealed class FakeCiscoDevice(string banner, Func<string, string?> respond) : Stream
{
    private readonly Queue<byte> _toClient = new(Encoding.UTF8.GetBytes(banner));
    private readonly StringBuilder _fromClient = new();
    private readonly Lock _gate = new();

    /// <summary>届いたことの合図。読み手は回しっぱなしなので、起こしてやる必要がある。</summary>
    private readonly SemaphoreSlim _data = new(banner.Length > 0 ? 1 : 0);

    /// <summary>クライアントが送ったものの全記録（何を送ったかの検証用）。</summary>
    public string Sent
    {
        get { lock (_gate) return _fromClient.ToString(); }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private void Push(string text)
    {
        lock (_gate)
        {
            foreach (byte b in Encoding.UTF8.GetBytes(text))
                _toClient.Enqueue(b);
        }

        _data.Release();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_toClient.Count > 0)
                {
                    int count = Math.Min(buffer.Length, _toClient.Count);
                    for (int i = 0; i < count; i++)
                        buffer.Span[i] = _toClient.Dequeue();

                    return count;
                }
            }

            // 無言。届くまで待つ（実機の沈黙と同じ。呼び出し側が別に時間を測る）
            await _data.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        string text = Encoding.UTF8.GetString(buffer.Span);

        lock (_gate)
            _fromClient.Append(text);

        int newline;
        string pending = text;

        while ((newline = pending.IndexOf('\n', StringComparison.Ordinal)) >= 0)
        {
            string line = pending[..newline].TrimEnd('\r');
            pending = pending[(newline + 1)..];

            if (respond(line) is { } answer) Push(answer);
        }

        // 改行を伴わない送信(ページャへのスペースなど)も台本へ渡す
        if (pending.Length > 0 && respond(pending) is { } extra) Push(extra);

        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public class CiscoSessionTests
{
    // 実時間で待つので、待ち時間はすべて短くする
    private static readonly CiscoSessionOptions Fast = new()
    {
        SettleTime = TimeSpan.FromMilliseconds(60),
        IdleTimeout = TimeSpan.FromMilliseconds(300),
        CommandTimeout = TimeSpan.FromSeconds(3),
        LoginTimeout = TimeSpan.FromSeconds(3),
    };

    private static readonly DeviceCredentials Login = new("admin", "pass1", "enable1");

    private static Task<DeviceCollectionResult> RunAsync(
        FakeCiscoDevice device, string[] commands, CancellationToken token = default)
        => new CiscoSession(device, Fast).RunAsync(
            "10.0.0.1", 23, usedSsh: false, hostKeyFingerprint: null, Login, commands, token);

    /// <summary>ログインから始まり、enable まで素直に応じる機器の台本。</summary>
    private static Func<string, string?> FromLogin(Func<string, string?> commands)
    {
        bool loggedIn = false;
        bool enabled = false;

        return line =>
        {
            string text = line.Trim();

            if (!loggedIn)
            {
                if (text == "admin") return "\r\nPassword: ";
                if (text == "pass1") { loggedIn = true; return "\r\nR1>"; }
                return "\r\nUsername: ";
            }

            if (!enabled)
            {
                if (text == "enable") return "\r\nPassword: ";
                if (text == "enable1") { enabled = true; return "\r\nR1#"; }
            }

            string prompt = enabled ? "\r\nR1#" : "\r\nR1>";

            if (text.StartsWith("terminal ", StringComparison.Ordinal))
                return text == "terminal length 0" ? prompt : "\r\n% Invalid input detected" + prompt;

            return commands(text) is { } answer ? "\r\n" + answer + prompt : prompt;
        };
    }

    /// <summary>すでに特権モードで、ログインを求めない機器の台本。</summary>
    private static Func<string, string?> AlreadyEnabled(Func<string, string?> commands) => line =>
    {
        string text = line.Trim();

        if (text.StartsWith("terminal ", StringComparison.Ordinal))
            return text == "terminal length 0" ? "\r\nR1#" : "\r\n% Invalid input detected\r\nR1#";

        return commands(text) is { } answer ? "\r\n" + answer + "\r\nR1#" : "\r\nR1#";
    };

    [Fact]
    public async Task A_normal_session_logs_in_enables_and_collects_every_command()
    {
        var device = new FakeCiscoDevice("\r\nUser Access Verification\r\n\r\nUsername: ", FromLogin(text => text switch
        {
            "show clock" => "15:30:13.123 JST Sat Aug 16 2026",
            "show version" => "Cisco IOS Software, Version 15.2",
            _ => null,
        }));

        DeviceCollectionResult result = await RunAsync(device, ["show clock", "show version"]);

        Assert.Null(result.FailureMessage);
        Assert.Equal("R1", result.LearnedHostname);
        Assert.True(result.ReachedEnable);
        Assert.Equal(2, result.Commands.Count);
        Assert.Contains("15:30:13", result.Commands[0].Output);
        Assert.Contains("Cisco IOS Software", result.Commands[1].Output);
    }

    [Fact]
    public async Task The_pager_is_disabled_with_one_command_not_three()
    {
        // IOS 系に terminal pager 0 を投げると「% Invalid input」が出て、
        // 使う人には失敗したように見える（2026-08-18 に報告された）
        var sent = new List<string>();

        var device = new FakeCiscoDevice("\r\nR1#", line =>
        {
            string text = line.Trim();

            if (text.StartsWith("terminal ", StringComparison.Ordinal))
            {
                sent.Add(text);
                return text == "terminal length 0" ? "\r\nR1#" : "\r\n% Invalid input detected\r\nR1#";
            }

            return "\r\nR1#";
        });

        DeviceCollectionResult result = await RunAsync(device, ["show clock"]);

        Assert.Equal(new[] { "terminal length 0" }, sent.ToArray());
        Assert.Contains("terminal length 0", result.PagerNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_only_knows_the_asa_form_still_gets_its_pager_off()
    {
        var sent = new List<string>();

        var device = new FakeCiscoDevice("\r\nasa#", line =>
        {
            string text = line.Trim();

            if (text.StartsWith("terminal ", StringComparison.Ordinal))
            {
                sent.Add(text);
                return text == "terminal pager 0" ? "\r\nasa#" : "\r\n% Invalid input detected\r\nasa#";
            }

            return "\r\nasa#";
        });

        DeviceCollectionResult result = await RunAsync(device, ["show clock"]);

        // 1 本目が断られたら次を試す。通ったところで止める
        Assert.Equal(new[] { "terminal length 0", "terminal pager 0" }, sent.ToArray());
        Assert.Contains("terminal pager 0", result.PagerNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_asks_for_the_password_twice_is_a_failed_login()
    {
        // 3 回目を送らないこと(TACACS+ 環境でアカウントを固めない)
        var device = new FakeCiscoDevice("\r\nUsername: ", line =>
            line.Trim() == "admin" ? "\r\nPassword: " : "\r\nPassword: ");

        DeviceCollectionResult result = await RunAsync(device, ["show clock"]);

        Assert.Contains("パスワード", result.FailureMessage);
        Assert.Empty(result.Commands);
        Assert.Equal(1, device.Sent.Split("pass1").Length - 1);
    }

    [Fact]
    public async Task A_device_already_in_enable_mode_is_not_asked_to_enable_again()
    {
        var device = new FakeCiscoDevice("\r\nR1#", AlreadyEnabled(text =>
            text == "show clock" ? "15:30:13" : null));

        DeviceCollectionResult result = await RunAsync(device, ["show clock"]);

        Assert.True(result.ReachedEnable);
        Assert.DoesNotContain("enable", device.Sent, StringComparison.Ordinal);
        Assert.Contains("15:30:13", Assert.Single(result.Commands).Output);
    }

    [Fact]
    public async Task Failing_to_enable_still_collects_what_user_mode_can_see()
    {
        bool loggedIn = false;

        var device = new FakeCiscoDevice("\r\nUsername: ", line =>
        {
            string text = line.Trim();

            if (!loggedIn)
            {
                if (text == "admin") return "\r\nPassword: ";
                if (text == "pass1") { loggedIn = true; return "\r\nR1>"; }
                return "\r\nUsername: ";
            }

            // enable のパスワードを何度でも聞き返す機器
            if (text is "enable" or "enable1") return "\r\nPassword: ";

            return text == "show clock" ? "\r\n15:30:13\r\nR1>" : "\r\nR1>";
        });

        DeviceCollectionResult result = await RunAsync(device, ["show clock"]);

        Assert.False(result.ReachedEnable);
        Assert.Contains("15:30:13", Assert.Single(result.Commands).Output);
    }

    [Fact]
    public async Task Paged_output_is_joined_back_together()
    {
        bool morePending = false;

        var device = new FakeCiscoDevice("\r\nR1#", line =>
        {
            // ページャへの応答はスペース 1 文字(改行を伴わない)
            if (morePending && line == " ")
            {
                morePending = false;
                return "line-two\r\nR1#";
            }

            string text = line.Trim();

            if (text.StartsWith("terminal ", StringComparison.Ordinal)) return "\r\nR1#";

            if (text == "show run")
            {
                morePending = true;
                return "\r\nline-one\r\n --More-- ";
            }

            return "\r\nR1#";
        });

        DeviceCollectionResult result = await RunAsync(device, ["show run"]);

        CommandResult command = Assert.Single(result.Commands);
        Assert.Contains("line-one", command.Output);
        Assert.Contains("line-two", command.Output);
        Assert.DoesNotContain("More", command.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_never_answers_is_given_up_on_not_waited_for_forever()
    {
        var device = new FakeCiscoDevice("", _ => null);

        DeviceCollectionResult result = await RunAsync(device, ["show clock"]);

        Assert.NotNull(result.FailureMessage);
        Assert.Empty(result.Commands);
        Assert.True(result.FinishedAt - result.StartedAt < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_command_that_never_returns_is_cut_off_and_the_rest_still_run()
    {
        var device = new FakeCiscoDevice("\r\nR1#", AlreadyEnabled(text =>
            text == "show hang" ? null : "ok"));

        // show hang はプロンプトすら返さない台本にする
        var silent = new FakeCiscoDevice("\r\nR1#", line =>
        {
            string text = line.Trim();

            if (text.StartsWith("terminal ", StringComparison.Ordinal)) return "\r\nR1#";
            if (text == "show hang") return null;
            if (text == "q") return "\r\nR1#";

            return "\r\nok\r\nR1#";
        });

        _ = device;
        DeviceCollectionResult result = await RunAsync(silent, ["show hang", "show clock"]);

        Assert.Equal(2, result.Commands.Count);
        Assert.NotNull(result.Commands[0].Problem);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task A_confirmation_prompt_is_left_unanswered()
    {
        // 実機は確認を出したところで止まる(プロンプトは返さない)
        var device = new FakeCiscoDevice("\r\nR1#", line =>
        {
            string text = line.Trim();

            if (text.StartsWith("terminal ", StringComparison.Ordinal)) return "\r\nR1#";
            if (text == "show risky") return "\r\nProceed? [confirm]";
            if (text == "q") return "\r\nR1#";

            return "\r\nR1#";
        });

        DeviceCollectionResult result = await RunAsync(device, ["show risky"]);

        Assert.Contains("確認を求められた", Assert.Single(result.Commands).Problem);

        // 確認へ答えていないこと(答えれば実行されてしまう)
        Assert.DoesNotContain("yes", device.Sent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_stops_quickly_and_keeps_what_was_collected()
    {
        // 応答を待っている最中に止められること
        var device = new FakeCiscoDevice("\r\nR1#", line =>
        {
            string text = line.Trim();

            if (text.StartsWith("terminal ", StringComparison.Ordinal)) return "\r\nR1#";

            return text == "show slow" ? null : "\r\nR1#";
        });

        using var cts = new CancellationTokenSource();

        Task<DeviceCollectionResult> run = RunAsync(device, ["show slow"], cts.Token);

        await Task.Delay(200, CancellationToken.None);
        await cts.CancelAsync();

        DeviceCollectionResult result = await run;

        Assert.Contains("中断", result.FailureMessage);
    }

    [Fact]
    public async Task A_command_the_device_rejects_is_recorded_with_a_japanese_reason()
    {
        var device = new FakeCiscoDevice("\r\nR1#", AlreadyEnabled(text =>
            text == "show vpc" ? "% Invalid input detected at '^' marker." : "ok"));

        DeviceCollectionResult result = await RunAsync(device, ["show vpc"]);

        Assert.Contains("使えない", Assert.Single(result.Commands).Problem);
    }
}
