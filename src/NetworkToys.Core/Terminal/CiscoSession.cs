using System.Diagnostics;
using System.Text;

namespace NetworkToys.Core.Terminal;

/// <summary>接続 1 本ぶんの認証情報。<b>保存も記録もしない。</b></summary>
public sealed record DeviceCredentials(string UserName, string Password, string EnablePassword);

/// <summary>待ち時間の設定。画面に出すのは接続と無音の 2 つだけ。</summary>
public sealed record CiscoSessionOptions
{
    /// <summary>最後の 1 バイトからこれだけ黙ったら諦める。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>1 コマンドの上限。無音にならないまま終わらない出力を必ず切る。</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>プロンプトを学習するときの静穏時間。長いバナーを吸収する。</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>ログインの待ち。</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>1 コマンドで返してよい最大の文字数。</summary>
    public int MaxOutputChars { get; init; } = 4 * 1024 * 1024;

    /// <summary>ページャに応答してよい回数。</summary>
    public int MaxMoreResponses { get; init; } = 2000;
}

/// <summary>
/// Cisco の対話セッションを駆動する状態機械。
///
/// <b><see cref="Stream"/> しか知らない。</b>SSH でも Telnet でも同じものが動き、
/// テストではメモリ上の偽の機器と会話させられる（CI が唯一の実行環境という
/// 制約下で、実装の正しさを確かめられる唯一の道）。
///
/// <b>ハングさせないことが最優先。</b>待ちはすべて <see cref="WaitForAsync"/> 1 本に
/// 集約し、無音と上限の二重のタイムアウトを必ず通す。<c>ReadLine</c> は使わない。
/// </summary>
public sealed class CiscoSession(Stream io, CiscoSessionOptions options)
{
    private readonly StringBuilder _buffer = new();
    private readonly Lock _gate = new();

    /// <summary>読めたことの合図。読み取りは専用タスクが回し、本体はここで待つ。</summary>
    private readonly SemaphoreSlim _arrived = new(0);

    private Task? _reader;
    private CancellationTokenSource? _readerStop;

    /// <summary>無言の機器へ改行を入れて様子を見る間隔。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);

    private string? _hostname;

    /// <summary>最後に確かめたプロンプトの段階。バッファを消しても失われないよう持つ。</summary>
    private PromptLevel _level = PromptLevel.User;

    /// <summary>進捗の知らせ（画面のステータス用）。</summary>
    public IProgress<string>? Progress { get; init; }

    public async Task<DeviceCollectionResult> RunAsync(
        string host,
        int port,
        bool usedSsh,
        string? hostKeyFingerprint,
        DeviceCredentials credentials,
        IReadOnlyList<string> commands,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(commands);

        DateTime started = DateTime.Now;
        List<CommandResult> results = [];
        bool reachedEnable = false;
        string pagerNote = "未実施";
        string? failure = null;

        try
        {
            failure = await LoginAsync(credentials, token).ConfigureAwait(false);

            if (failure is null)
            {
                reachedEnable = await EnableAsync(credentials, token).ConfigureAwait(false);
                pagerNote = await DisablePagerAsync(token).ConfigureAwait(false);

                for (int i = 0; i < commands.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    Progress?.Report($"{i + 1}/{commands.Count} {commands[i]}");
                    results.Add(await RunOneAsync(i + 1, commands[i], token).ConfigureAwait(false));
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure ??= "中断しました。";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            failure ??= $"接続が切れました: {ex.Message}";
        }

        StopReader();

        return new DeviceCollectionResult(
            Host: host,
            Port: port,
            LearnedHostname: _hostname,
            UserName: credentials.UserName,
            UsedSsh: usedSsh,
            HostKeyFingerprint: hostKeyFingerprint,
            StartedAt: started,
            FinishedAt: DateTime.Now,
            ReachedEnable: reachedEnable,
            PagerNote: pagerNote,
            Commands: results,
            FailureMessage: failure);
    }

    /// <summary>
    /// ログイン。<b>どの合図が来るか分からないので、到達点を同時に待つ。</b>
    /// パスワードを 2 回聞かれたら認証失敗として即中断する（回数による判定が、
    /// 機種ごとの文言に依存しない一番強い規則）。<b>再試行はしない</b> —
    /// TACACS+/AD 環境でアカウントを固めないため。
    /// </summary>
    private async Task<string?> LoginAsync(DeviceCredentials credentials, CancellationToken token)
    {
        Progress?.Report("ログインしています…");

        int passwordSent = 0;
        bool userSent = false;
        var deadline = Stopwatch.StartNew();
        var probe = Stopwatch.StartNew();

        // 何も言ってこない機器のために、こちらから改行を 1 つ入れて呼び水にする
        await SendAsync("", token).ConfigureAwait(false);

        while (deadline.Elapsed < options.LoginTimeout)
        {
            token.ThrowIfCancellationRequested();

            bool got = await PumpAsync(options.SettleTime, token).ConfigureAwait(false);
            string view = Clean();

            if (CiscoPrompt.DetectAuthFailure(view) is { } reason)
                return reason;

            if (CiscoPrompt.TryMatchAtEnd(view, null, out PromptMatch match))
            {
                _hostname = match.Hostname;
                _level = match.Level;
                ClearBuffer();
                return null;
            }

            if (CiscoPrompt.EndsWithUsernamePrompt(view) && !userSent)
            {
                userSent = true;
                ClearBuffer();
                await SendAsync(credentials.UserName, token).ConfigureAwait(false);
                continue;
            }

            if (CiscoPrompt.EndsWithPasswordPrompt(view))
            {
                if (passwordSent >= 1)
                    return "ユーザー名かパスワードが違います。";

                passwordSent++;
                ClearBuffer();
                await SendAsync(credentials.Password, token).ConfigureAwait(false);
                continue;
            }

            // 何も来なくなったら、こちらから改行を入れて様子を見る。
            // 静穏のたびに送ると機器側が数え切れないほどの改行を受け取るので間を空ける
            if (!got && probe.Elapsed > ProbeInterval)
            {
                probe.Restart();
                await SendAsync("", token).ConfigureAwait(false);
            }
        }

        return "ログインの応答がありませんでした。";
    }

    /// <summary>
    /// 特権モードへ。<b>入れなくてもユーザーモードのまま続行する</b>
    /// （<c>show version</c> などは取れる。取れるものを捨てない）。
    /// </summary>
    private async Task<bool> EnableAsync(DeviceCredentials credentials, CancellationToken token)
    {
        // すでに # なら二重に投げない(ログインで見た段階を覚えてある)
        if (_level != PromptLevel.User) return true;

        await SendAsync("enable", token).ConfigureAwait(false);

        var deadline = Stopwatch.StartNew();
        bool passwordSent = false;

        while (deadline.Elapsed < options.LoginTimeout)
        {
            token.ThrowIfCancellationRequested();

            await PumpAsync(options.SettleTime, token).ConfigureAwait(false);
            string view = Clean();

            if (CiscoPrompt.EndsWithPasswordPrompt(view))
            {
                if (passwordSent || credentials.EnablePassword.Length == 0)
                {
                    // 抜けられないので改行だけ入れてユーザーモードへ戻す
                    ClearBuffer();
                    await SendAsync("", token).ConfigureAwait(false);
                    return false;
                }

                passwordSent = true;
                ClearBuffer();
                await SendAsync(credentials.EnablePassword, token).ConfigureAwait(false);
                continue;
            }

            if (CiscoPrompt.TryMatchAtEnd(view, _hostname, out PromptMatch match))
            {
                _level = match.Level;
                ClearBuffer();
                return match.Level != PromptLevel.User;
            }
        }

        return false;
    }

    /// <summary>
    /// ページャを止める。<b>機種で出し分けない。</b>全部投げて失敗を無視する。
    /// 機種判定を誤るとページャが生き残って全コマンドが詰まるので、
    /// 判定の誤りより無害な失敗を選ぶ。
    /// </summary>
    private async Task<string> DisablePagerAsync(CancellationToken token)
    {
        string[] candidates = ["terminal length 0", "terminal pager 0", "terminal width 0"];
        int accepted = 0;

        foreach (string command in candidates)
        {
            CommandResult result = await RunOneAsync(0, command, token).ConfigureAwait(false);

            if (result.Problem is null) accepted++;
        }

        return accepted == 0
            ? "⚠ 無効化できませんでした（出力が途中で切れることがあります）"
            : $"無効化を実施（{candidates.Length} 本中 {accepted} 本が有効）";
    }

    /// <summary>コマンドを 1 本投げて、プロンプトが返るまで読む。</summary>
    private async Task<CommandResult> RunOneAsync(int index, string command, CancellationToken token)
    {
        ClearBuffer();
        var watch = Stopwatch.StartNew();

        await SendAsync(command, token).ConfigureAwait(false);

        string? problem = null;
        int moreCount = 0;

        // 「最後に何か届いてから」の無音を測る。1 回黙っただけで諦めると、
        // 考えている最中のコマンド(show run の頭など)を切ってしまう
        var silence = Stopwatch.StartNew();

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (watch.Elapsed > options.CommandTimeout)
            {
                problem = "時間がかかりすぎたため打ち切りました。";
                break;
            }

            if (BufferLength > options.MaxOutputChars)
            {
                problem = "出力が大きすぎたため打ち切りました。";
                break;
            }

            bool got = await PumpAsync(options.SettleTime, token).ConfigureAwait(false);
            if (got) silence.Restart();

            string view = Clean();

            if (CiscoPrompt.TryMatchAtEnd(view, _hostname, out _))
                break;

            if (CiscoPrompt.EndsWithConfirmPrompt(view))
            {
                // 答えなければ実行されない。ここで Enter を送ってはいけない
                problem = "確認を求められたため実行しませんでした。";
                break;
            }

            if (CiscoPrompt.EndsWithMore(view))
            {
                if (++moreCount > options.MaxMoreResponses)
                {
                    problem = "ページャの応答が多すぎたため打ち切りました。";
                    break;
                }

                // スペース 1 文字で次の画面へ（改行だと 1 行しか進まない）
                await WriteRawAsync(" ", token).ConfigureAwait(false);
                continue;
            }

            if (!got && silence.Elapsed > options.IdleTimeout)
            {
                // 無音が続いた。プロンプトが返らないまま止まっている
                problem = "応答が返らないため打ち切りました。";
                break;
            }
        }

        watch.Stop();

        string output = CommandCapture.ExtractOutput(Clean(), command, _hostname);
        problem ??= CiscoPrompt.DetectCommandProblem(output);

        if (problem is not null)
            await ResyncAsync(token).ConfigureAwait(false);

        return new CommandResult(index, command, output, watch.Elapsed, problem);
    }

    /// <summary>
    /// 打ち切ったあとの立て直し。<b>一度だけ試す。</b>
    /// 繰り返し粘るのがハングの正体なので、駄目ならそのまま次へ進む。
    /// </summary>
    private async Task ResyncAsync(CancellationToken token)
    {
        try
        {
            await WriteRawAsync("q\r\n", token).ConfigureAwait(false);
            await PumpAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 立て直せなければ諦める。呼び出し側が次のコマンドで気づく
        }

        ClearBuffer();
    }

    private Task SendAsync(string line, CancellationToken token) => WriteRawAsync(line + "\r\n", token);

    private async Task WriteRawAsync(string text, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        await io.WriteAsync(bytes, token).ConfigureAwait(false);
        await io.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// 静穏時間ぶん待つ。何か読めていたら true。
    ///
    /// <b>読み取り自体はキャンセルしない。</b>ソケットの読み取りを途中で打ち切ると、
    /// OS がすでに受け取ったバイトを取りこぼすことがある。読み取りは専用タスクに
    /// 回しっぱなしにして、こちらは「届いた合図」だけを待つ。
    /// 止めるときは接続を閉じてタスクごと終わらせる。
    /// </summary>
    private async Task<bool> PumpAsync(TimeSpan quiet, CancellationToken token)
    {
        StartReader();

        try
        {
            return await _arrived.WaitAsync(quiet, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private void StartReader()
    {
        if (_reader is not null) return;

        _readerStop = new CancellationTokenSource();
        CancellationToken stop = _readerStop.Token;

        _reader = Task.Run(async () =>
        {
            byte[] raw = new byte[8192];
            char[] chars = new char[8192];

            // マルチバイト文字は読み取り境界で割れるので Decoder を持ち回す
            Decoder decoder = Encoding.UTF8.GetDecoder();

            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int read = await io.ReadAsync(raw, stop).ConfigureAwait(false);
                    if (read <= 0) return;

                    int count = decoder.GetChars(raw, 0, read, chars, 0);
                    if (count == 0) continue;

                    lock (_gate)
                        _buffer.Append(chars, 0, count);

                    _arrived.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 接続が閉じた。呼び出し側は無音として扱う
            }
        }, stop);
    }

    private void StopReader()
    {
        _readerStop?.Cancel();
        _readerStop?.Dispose();
        _readerStop = null;
        _reader = null;
    }

    private int BufferLength
    {
        get { lock (_gate) return _buffer.Length; }
    }

    private string Clean()
    {
        lock (_gate)
            return TerminalText.Clean(_buffer.ToString());
    }

    private void ClearBuffer()
    {
        lock (_gate)
            _buffer.Clear();

        // 溜まっていた合図も捨てる(古い合図で次の待ちが素通りするのを防ぐ)
        while (_arrived.CurrentCount > 0)
            _arrived.Wait(0);
    }
}

/// <summary>1 コマンド分の塊から、エコー行と末尾のプロンプトを剥がす。</summary>
public static class CommandCapture
{
    public static string ExtractOutput(string chunk, string command, string? hostname)
    {
        if (chunk.Length == 0) return "";

        // ページャの痕跡はここで落とす(判定側はまだ必要としている)
        string[] lines = TerminalText.StripMorePrompts(chunk).Split('\n');
        int start = 0;
        int end = lines.Length;

        // 先頭のエコー行(打ったコマンドがそのまま返ってくる)を落とす。
        // エコーを返さない機器もあるので「あれば落とす」に留める
        while (start < end && lines[start].Trim().Length == 0) start++;

        if (start < end && lines[start].Trim().EndsWith(command.Trim(), StringComparison.Ordinal))
            start++;

        // 末尾のプロンプト行を落とす
        while (end > start && lines[end - 1].Trim().Length == 0) end--;

        if (end > start && IsPrompt(lines[end - 1], hostname))
            end--;

        while (end > start && lines[end - 1].Trim().Length == 0) end--;

        return end <= start ? "" : string.Join('\n', lines[start..end]) + "\n";
    }

    private static bool IsPrompt(string line, string? hostname)
        => CiscoPrompt.TryMatchAtEnd(line.TrimEnd(), hostname, out _);
}
