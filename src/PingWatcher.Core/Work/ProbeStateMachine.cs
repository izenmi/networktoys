using PingWatcher.Core.Models;

namespace PingWatcher.Core.Work;

/// <summary>宛先が今どう見えているか。</summary>
public enum LinkState
{
    /// <summary>まだ 1 件も結果が来ていない。</summary>
    Pending,

    /// <summary>応答している。</summary>
    Up,

    /// <summary>連続で応答が無い。</summary>
    Down,

    /// <summary>
    /// 結果そのものが届かなくなった。
    /// <b>「応答が無い」とは意味がまったく違う。</b>測定が止まっているのに
    /// 「応答あり」と表示し続けると、変更作業の確認ツールとしては致命的なので区別する。
    /// </summary>
    Stalled,
}

/// <summary>
/// 1 宛先分の状態を、<b>サンプル 1 件ごとに</b>進める状態機械。
///
/// なぜ独立させるか: 以前は UI 側で「最新の 1 件」だけを見て判定していたため、
/// 1 回の画面更新に 2 件以上の結果が届くと失敗を数え落としていた。
/// ここに寄せると合成したサンプル列でテストできる。
/// </summary>
public sealed class ProbeStateMachine
{
    /// <summary>何回続けて応答が無ければ落ちたとみなすか。1 回の取りこぼしはよくあること。</summary>
    public const int FailuresToDeclareDown = 2;

    private readonly int _failuresToDeclareDown;

    public ProbeStateMachine(int failuresToDeclareDown = FailuresToDeclareDown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresToDeclareDown, 1);
        _failuresToDeclareDown = failuresToDeclareDown;
    }

    public LinkState State { get; private set; } = LinkState.Pending;

    /// <summary>
    /// <see cref="LinkState.Stalled"/> になる直前の状態。
    /// 表示は「停止」を出すべきでも、<b>応答あり/なしの振り分けまで戻してはいけない</b>。
    /// 不達だった宛先が、測定が詰まった瞬間に「応答あり」欄へ移るのは嘘になる。
    /// </summary>
    public LinkState StateBeforeStall { get; private set; } = LinkState.Pending;

    /// <summary>いま何回続けて応答が無いか。</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>観測した中で最も長く続いた連続失敗。</summary>
    public int MaxConsecutiveFailures { get; private set; }

    /// <summary>1 回だけ落ちてすぐ戻った回数。切替の瞬断はここに出る。</summary>
    public int SingleDropCount { get; private set; }

    /// <summary>最後に応答があった時刻（Ticks）。一度も無ければ null。</summary>
    public long? LastSuccessTicks { get; private set; }

    /// <summary>最後にサンプルが届いた時刻（Ticks）。測定が止まったかの判断に使う。</summary>
    public long? LastSampleTicks { get; private set; }

    /// <summary>直近のサンプル。</summary>
    public ProbeSample Latest { get; private set; }

    /// <summary>
    /// サンプルを 1 件取り込む。状態が変わったときだけ <see langword="true"/> を返す。
    /// </summary>
    public bool Observe(in ProbeSample sample)
    {
        // 「まだ測っていない」は観測ではない。ここで時刻を更新すると
        // 停止検知（結果が途絶えた）のタイマーを空振りで延命してしまう
        if (sample.Status == ProbeStatus.Pending)
            return false;

        Latest = sample;
        LastSampleTicks = sample.TimestampTicks;

        LinkState previous = State;

        // TCP の「拒否」は RST が返っている＝相手は生きているので、応答側に数える
        if (sample.Status is ProbeStatus.Success or ProbeStatus.Refused)
        {
            if (ConsecutiveFailures == 1)
                SingleDropCount++;

            ConsecutiveFailures = 0;
            LastSuccessTicks = sample.TimestampTicks;
            State = LinkState.Up;
        }
        else
        {
            ConsecutiveFailures++;

            if (ConsecutiveFailures > MaxConsecutiveFailures)
                MaxConsecutiveFailures = ConsecutiveFailures;

            // 規定回数までは前の状態を保つ（初回の失敗だけでは落ちたと判断しない）
            if (ConsecutiveFailures >= _failuresToDeclareDown)
                State = LinkState.Down;
        }

        return State != previous;
    }

    /// <summary>
    /// 結果が途絶えていないかを確かめる。途絶えていれば <see cref="LinkState.Stalled"/> にする。
    /// </summary>
    /// <param name="nowTicks">現在時刻（Ticks）。</param>
    /// <param name="intervalMs">測定間隔。この 3 倍を過ぎたら止まったとみなす。</param>
    /// <returns>状態が変わったか。</returns>
    public bool CheckStalled(long nowTicks, int intervalMs)
    {
        if (LastSampleTicks is not { } last || State == LinkState.Pending)
            return false;

        long limit = TimeSpan.FromMilliseconds(Math.Max(intervalMs, 1) * 3.0).Ticks;

        LinkState previous = State;

        if (nowTicks - last > limit && State != LinkState.Stalled)
        {
            // 振り分けを戻さないよう、止まる直前の到達性を控えてから切り替える
            StateBeforeStall = State;
            State = LinkState.Stalled;
        }

        // 止まっていた状態から結果が戻ってきた場合は Observe 側で更新される
        return State != previous;
    }

    public void Reset()
    {
        State = LinkState.Pending;
        StateBeforeStall = LinkState.Pending;
        ConsecutiveFailures = 0;
        MaxConsecutiveFailures = 0;
        SingleDropCount = 0;
        LastSuccessTicks = null;
        LastSampleTicks = null;
        Latest = default;
    }
}
