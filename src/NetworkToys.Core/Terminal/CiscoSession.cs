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

    /// <summary>
    /// 言い終わったとみなすまでの静穏時間（<c>DrainAsync</c> 用）。
    ///
    /// 呼び水への応答は、本来の応答から<b>おおむね 1 往復ぶん遅れて</b>届く。
    /// 静穏時間を <see cref="SettleTime"/> と同じにすると、遅い回線でこれを取りこぼす。
    /// </summary>
    public TimeSpan DrainQuiet { get; init; } = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// 機器から届いた文字を丸ごと残したもの。
    ///
    /// <b>表（区画）の元になった生の会話</b>で、ずれや取りこぼしを実機で切り分ける唯一の手掛かり
    /// （WLC の「取得した出力」と同じ考え方。2026-08-18 に 3 度めのずれの報告を受けて足した）。
    /// <b>こちらが送った文字は入れない</b> — パスワードを書き残さないため
    /// （機器はパスワードをエコーしないので、届いた文字だけなら安全）。
    /// </summary>
    private readonly StringBuilder _transcript = new();

    /// <summary>生ログの上限。これを超えたら以降は捨てる（1 台で数百 MB にしない）。</summary>
    private const int MaxTranscript = 1024 * 1024;
    private readonly Lock _gate = new();

    /// <summary>読めたことの合図。読み取りは専用タスクが回し、本体はここで待つ。</summary>
    private readonly SemaphoreSlim _arrived = new(0);

    private Task? _reader;
    private CancellationTokenSource? _readerStop;

    /// <summary>無言の機器へ改行を入れて様子を見る間隔。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);

    /// <summary>言い終わるのを待つ上限。<see cref="DrainAsync"/> で粘りすぎないため。</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(3);

    private string? _hostname;

    /// <summary>
    /// 行末に送る文字。<b>SSH では改行 1 文字だけ</b>にする。
    ///
    /// SSH には Telnet のような改行の取り決めが無く、機器側の端末制御がそのまま働く。
    /// <c>CR LF</c> を送ると<b>Enter を 2 回</b>押したことになる機器があり、
    /// 1 本のコマンドに対してプロンプトが 2 つ返る。1 つ目で「そろった」と見て
    /// 次を送るので、遅れて届く 2 つ目が<b>そのコマンドの完了</b>と読み違えられ、
    /// 以降のコマンドと出力が丸ごと 1 本ずれる
    /// （2026-08-18 実機報告: show clock は正しく、show version が 0 行、
    /// show inventory の区画に show version が載る）。
    ///
    /// Telnet は <c>CR LF</c> が Enter そのものなので、あちらは変えない。
    /// </summary>
    private string _newline = "\r\n";

    /// <summary>最後に確かめたプロンプトの段階。バッファを消しても失われないよう持つ。</summary>
    private PromptLevel _level = PromptLevel.User;

    /// <summary>
    /// いま機器がプロンプトで待っていると分かっているか。
    ///
    /// <b>ここが false のまま次のコマンドを送ると、前の出力の途中へ打ち込むことになる。</b>
    /// 打ち込んだ文字は前のコマンドの出力に紛れ、以降のコマンドと出力の対応が
    /// 丸ごとずれる（2026-08-18 に「コマンドがズレまくる」として報告された）。
    /// プロンプトを見たときだけ true にし、何か送ったら false に戻す。
    /// </summary>
    private bool _atPrompt;

    /// <summary>
    /// こちらから入れた呼び水（改行）の回数。
    ///
    /// <b>余分なプロンプトが後から返ってくる原因はこれだけ</b>なので、
    /// 1 度も入れていなければ読み捨て（<see cref="DrainAsync"/>）も要らない。
    /// </summary>
    private int _probes;

    /// <summary>
    /// この機器は打ったコマンドをそのまま返す（エコーする）か。
    ///
    /// 対話で入る機器はまず必ずエコーする。<b>エコーも出力も無くプロンプトだけが来たら、
    /// それは前のやり取りの残り</b>で、いま送ったコマンドの完了ではない。
    /// エコーを返さない機器も無くはないので、<b>一度エコーを見てから</b>この判断を始める。
    /// </summary>
    private bool _echoes;

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

        // SSH と Telnet で行末が違う（上の _newline の説明のとおり）
        _newline = usedSsh ? "\n" : "\r\n";

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
            FailureMessage: failure,
            Transcript: Transcript());
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

        // 呼び水（改行）は、機器が黙り込んでいるときだけ下の輪の中で入れる。
        // すでに何か言っている機器へ入れると、余分なプロンプトが返ってきて、
        // それを後のコマンドの完了と読み違える（ズレの原因になる）

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
                _atPrompt = true;
                await DrainAsync(token).ConfigureAwait(false);
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
                _probes++;
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
        var silence = Stopwatch.StartNew();
        bool passwordSent = false;
        bool droppedStale = false;

        while (deadline.Elapsed < options.LoginTimeout)
        {
            token.ThrowIfCancellationRequested();

            bool got = await PumpAsync(options.SettleTime, token).ConfigureAwait(false);
            if (got) silence.Restart();

            string view = Clean();

            if (CiscoPrompt.EndsWithPasswordPrompt(view))
            {
                if (passwordSent || credentials.EnablePassword.Length == 0)
                {
                    // 抜けられないので改行だけ入れてユーザーモードへ戻す。
                    // 戻ったこと(＝プロンプトが返ったこと)まで見届けてから諦める —
                    // ここを見ずに戻ると、次のコマンドを送る前に立て直しが挟まる
                    ClearBuffer();
                    _probes++;
                    await SendAsync("", token).ConfigureAwait(false);
                    await WaitForPromptAsync(options.LoginTimeout, token).ConfigureAwait(false);
                    return false;
                }

                passwordSent = true;
                ClearBuffer();
                await SendAsync(credentials.EnablePassword, token).ConfigureAwait(false);
                continue;
            }

            if (CiscoPrompt.TryMatchAtEnd(view, _hostname, out PromptMatch match))
            {
                // enable と打った直後に、エコーも何も無いプロンプトだけが来ることがある
                // （前のやり取りの残り）。それを「入れなかった」と読むと、以降ずっと
                // ユーザーモードのまま走り、show running-config が
                // 「% Invalid input」で断られる（2026-08-18 に実機で報告された）。
                // 1 度だけ捨てて、本当の応答を待つ
                if (!droppedStale && match.Level == PromptLevel.User && CommandCapture.IsBarePrompt(view))
                {
                    droppedStale = true;
                    ClearBuffer();
                    continue;
                }

                _level = match.Level;
                _atPrompt = true;
                await DrainAsync(token).ConfigureAwait(false);
                return match.Level != PromptLevel.User;
            }

            // 残りを捨てたあと機器が黙ったままなら、それ以上は待たない
            if (droppedStale && !got && silence.Elapsed > options.IdleTimeout) return false;
        }

        return false;
    }

    /// <summary>
    /// ページャを止める。<b>機種で出し分けない</b>が、<b>通った時点でやめる</b>。
    ///
    /// 以前は 3 本とも投げていた。判定の誤りより無害な失敗を選ぶ、という考えは同じだが、
    /// IOS-XE に <c>terminal pager 0</c> を投げると「% Invalid input」が出て、
    /// 使う人には失敗したように見える（2026-08-18 に報告された）。
    /// <b>先頭から順に投げて、機器が受け取ったところで止める</b> — IOS 系なら
    /// <c>terminal length 0</c> だけ、ASA なら <c>terminal pager 0</c> だけになる。
    /// </summary>
    private async Task<string> DisablePagerAsync(CancellationToken token)
    {
        string[] candidates = ["terminal length 0", "terminal pager 0", "terminal width 0"];

        foreach (string command in candidates)
        {
            CommandResult result = await RunOneAsync(0, command, token).ConfigureAwait(false);

            if (result.Problem is null && !WasRejected(result.Output))
                return $"無効化を実施（{command}）";
        }

        return "⚠ 無効化できませんでした（出力が途中で切れることがあります）";
    }

    /// <summary>
    /// 機器がそのコマンドを断ったか。<b>文言は機種で違う</b>ので、
    /// どれかに当たれば断られたとみなす（当たらなければ通ったものとして扱う）。
    /// </summary>
    private static bool WasRejected(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;

        foreach (string mark in (string[])
                 ["% Invalid input", "% Unknown command", "% Ambiguous command",
                  "Invalid input detected", "% Incomplete command", "ERROR: %"])
        {
            if (output.Contains(mark, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// コマンドを 1 本投げて、プロンプトが返るまで読む。
    ///
    /// <b>送る前に、機器がプロンプトで待っていることを必ず確かめる</b>
    /// （2026-08-18 ユーザー指示）。前のコマンドが打ち切りや確認待ちで終わっていると
    /// 機器はまだ何か出している最中で、そこへ打ち込むと文字が出力に紛れ、
    /// <b>以降のコマンドと出力の対応が全部ずれる</b>。
    /// 直前のコマンドが正常に終わっていれば、そのときプロンプトを見ているので待ち時間は 0。
    /// </summary>
    private async Task<CommandResult> RunOneAsync(int index, string command, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();

        if (!_atPrompt && !await ResyncAsync(token).ConfigureAwait(false))
        {
            // 立て直せなかった。ここで送ると、ずれたまま最後まで走ってしまう
            return new CommandResult(
                index, command, "", watch.Elapsed,
                "前のコマンドの応答が終わっていないため実行しませんでした。");
        }

        ClearBuffer();

        await SendAsync(command, token).ConfigureAwait(false);

        string? problem = null;
        int moreCount = 0;

        // プロンプトが返ったか。返っていれば会話はそろっているので立て直す必要がない
        bool inSync = false;

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
            {
                // エコーを返す機器なのに、エコーも出力も無い。前のやり取りの残りなので捨てる
                if (_echoes && CommandCapture.IsBarePrompt(view))
                {
                    ClearBuffer();
                    continue;
                }

                inSync = true;
                _atPrompt = true;
                break;
            }

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

        string chunk = Clean();
        string output = CommandCapture.ExtractOutput(chunk, command, _hostname);

        // 1 度でもエコーを見たら、以降は「プロンプトだけ」を残りとして捨てられる
        _echoes |= CommandCapture.HasEcho(chunk, command);

        problem ??= CiscoPrompt.DetectCommandProblem(output);

        // 特権モードに入れていないと、show running-config や show logging のような
        // 特権コマンドは「% Invalid input」で断られる。コマンド自体が使えないように
        // 読めてしまうので、そうは書かない（2026-08-18 実機報告）
        if (problem is not null && _level == PromptLevel.User && WasRejected(output))
            problem = "特権モード(#)に入れていないため使えません。enable のパスワードを確かめてください。";

        // 立て直しはここでしない。会話がそろっていなければ _atPrompt が false のままなので、
        // 次のコマンドの入口が面倒を見る（最後のコマンドなら誰も待たされない）。
        // プロンプトが返っているなら立て直す必要はない — 機器が「そんなコマンドは無い」と
        // 言って戻ってきた場合も会話はそろっている（2026-08-18 報告）
        _ = inSync;

        return new CommandResult(index, command, output, watch.Elapsed, problem);
    }

    /// <summary>
    /// 打ち切ったあとの立て直し。<b>プロンプトが返ってきたら true。</b>
    ///
    /// <c>q</c> を送るのはページャで止まっている場合に抜けるため。あとは
    /// <see cref="WaitForPromptAsync"/> に任せる — <b>時間ではなくプロンプトを待つ</b>。
    /// 決められた時間で返らなければ諦めて false（繰り返し粘るのがハングの正体）。
    /// </summary>
    private async Task<bool> ResyncAsync(CancellationToken token)
    {
        try
        {
            await SendAsync("q", token).ConfigureAwait(false);

            return await WaitForPromptAsync(options.IdleTimeout, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 立て直せなければ諦める。呼び出し側が次のコマンドで気づく
            ClearBuffer();
            return false;
        }
    }

    /// <summary>
    /// プロンプトが返るまで読む。返ったらバッファを空にして true。
    ///
    /// 黙り込んだら改行を入れて様子を見る（ログインと同じ手）。ページャで
    /// 止まっているときはスペースを送って先へ進める — <b>そこを抜けないと
    /// プロンプトは永久に返らない</b>。
    /// </summary>
    private async Task<bool> WaitForPromptAsync(TimeSpan limit, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        var probe = Stopwatch.StartNew();

        while (deadline.Elapsed < limit)
        {
            token.ThrowIfCancellationRequested();

            bool got = await PumpAsync(options.SettleTime, token).ConfigureAwait(false);
            string view = Clean();

            if (CiscoPrompt.TryMatchAtEnd(view, _hostname, out PromptMatch match))
            {
                _level = match.Level;
                _atPrompt = true;
                await DrainAsync(token).ConfigureAwait(false);
                return true;
            }

            if (CiscoPrompt.EndsWithMore(view))
            {
                await WriteRawAsync(" ", token).ConfigureAwait(false);
                continue;
            }

            if (!got && probe.Elapsed > ProbeInterval)
            {
                probe.Restart();
                _probes++;
                await SendAsync("", token).ConfigureAwait(false);
            }
        }

        ClearBuffer();
        return false;
    }

    /// <summary>
    /// 機器が黙るまで読み捨ててから、バッファを空にする。
    ///
    /// <b>これが無いと、以降のコマンドと出力が丸ごと 1 本ずれる。</b>
    /// ログインや立て直しでは、黙り込んだ機器へこちらから改行（呼び水）を入れる。
    /// 機器はあとで我に返ったときに、<b>本来の応答と呼び水への応答の 2 つ</b>を返す。
    /// こちらは 1 つ目のプロンプトで「そろった」と見て次のコマンドを送るので、
    /// 遅れて届いた 2 つ目のプロンプトが<b>そのコマンドの完了と読み違えられる</b> —
    /// そのコマンドは中身が空で終わり、本当の出力は次のコマンドの区画に入る。
    /// これが最後まで連鎖する（2026-08-18 に「コマンドがズレまくる」として報告された）。
    ///
    /// 呼び水を入れるのはログイン・enable・立て直しの 3 か所だけなので、
    /// 読み捨てるのも 1 セッションにつき数回で済む。
    /// <b>黙らない機器で粘らない</b>（上限を切ってバッファを空にして戻る）。
    /// </summary>
    private async Task DrainAsync(CancellationToken token)
    {
        // 呼び水を 1 度も入れていなければ、遅れて届くものは無い
        if (_probes == 0)
        {
            ClearBuffer();
            return;
        }

        _probes = 0;
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < DrainLimit)
        {
            token.ThrowIfCancellationRequested();

            // 静穏時間ぶん待って何も来なければ、機器は言い終わっている
            if (!await PumpAsync(options.DrainQuiet, token).ConfigureAwait(false)) break;
        }

        ClearBuffer();
    }

    private Task SendAsync(string line, CancellationToken token) => WriteRawAsync(line + _newline, token);

    private async Task WriteRawAsync(string text, CancellationToken token)
    {
        // 送った時点で、機器はプロンプトで待っている状態ではなくなる
        _atPrompt = false;

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
                    {
                        _buffer.Append(chars, 0, count);

                        if (_transcript.Length < MaxTranscript) _transcript.Append(chars, 0, count);
                    }

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

    /// <summary>届いた文字そのもの（端末の制御文字だけ均す）。</summary>
    private string Transcript()
    {
        lock (_gate)
        {
            string text = TerminalText.Clean(_transcript.ToString());

            return _transcript.Length >= MaxTranscript
                ? text + "\n（ここまで。これ以上は記録していません）\n"
                : text;
        }
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
    /// <summary>エコー行を探す範囲（頭から数えた行数）。</summary>
    private const int EchoSearchLines = 5;

    /// <summary>打ったコマンドがそのまま返ってきているか（頭の数行だけ見る）。</summary>
    public static bool HasEcho(string chunk, string command)
        => EchoLine(TerminalText.StripMorePrompts(chunk).Split('\n'), command) >= 0;

    /// <summary>プロンプトだけで、ほかに何も無いか。</summary>
    public static bool IsBarePrompt(string view)
    {
        string[] lines = view.Split('\n');

        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].Trim().Length > 0) return false;
        }

        return lines.Length > 0 && lines[^1].Trim().Length > 0;
    }

    /// <summary>エコー行の位置。無ければ -1。</summary>
    private static int EchoLine(string[] lines, string command)
    {
        for (int i = 0; i < lines.Length && i < EchoSearchLines; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            if (lines[i].Trim().EndsWith(command.Trim(), StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    public static string ExtractOutput(string chunk, string command, string? hostname)
    {
        if (chunk.Length == 0) return "";

        // ページャの痕跡はここで落とす(判定側はまだ必要としている)
        string[] lines = TerminalText.StripMorePrompts(chunk).Split('\n');
        int start = 0;
        int end = lines.Length;

        // 先頭のエコー行(打ったコマンドがそのまま返ってくる)までを落とす。
        // エコーの前に前の応答の残り(遅れて届いたプロンプトなど)が挟まることがあるので、
        // 見つけたエコーより前は<b>まとめて</b>捨てる。ただし探すのは頭の数行だけ —
        // 出力の奥に同じ文字で終わる行(alias の定義など)があっても掴まないため。
        // エコーを返さない機器もあるので「あれば落とす」に留める
        if (EchoLine(lines, command) is int echo && echo >= 0) start = echo + 1;

        while (start < end && lines[start].Trim().Length == 0) start++;

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
