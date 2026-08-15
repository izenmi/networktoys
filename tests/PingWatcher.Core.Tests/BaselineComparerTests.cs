using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class BaselineComparerTests
{
    private static WorkEntry Entry(
        string host = "192.168.1.10",
        string kind = "ICMP",
        string address = "192.168.1.10",
        bool responded = true,
        int attempts = 60,
        double loss = 0,
        double average = 2,
        double p95 = 3) =>
        // 鍵は宛先の登録内容から作られるので、測り方（kind）を変えても同じ鍵になる。
        // だから「同じ宛先を ICMP と TCP で測った」を突き合わせられる
        new($"{host}|Icmp|0", host, kind, address, "備考", responded, attempts, loss, average, p95, 0);

    private static WorkSnapshot Snapshot(params WorkEntry[] entries)
        => new(DateTimeOffset.Now, string.Empty, 1000, entries);

    private static WorkComparison Single(WorkSnapshot before, WorkSnapshot after, params string[] outageKeys)
        => Assert.Single(BaselineComparer.Compare(before, after, outageKeys));

    [Fact]
    public void Nothing_changed()
    {
        WorkComparison result = Single(Snapshot(Entry()), Snapshot(Entry()));

        Assert.Equal(WorkVerdict.Unchanged, result.Verdict);
        Assert.Equal(VerdictLevel.Info, result.Level);
    }

    [Fact]
    public void Losing_a_response_is_a_failure()
    {
        WorkComparison result = Single(Snapshot(Entry()), Snapshot(Entry(responded: false, loss: 100)));

        Assert.Equal(WorkVerdict.Lost, result.Verdict);
        Assert.Equal(VerdictLevel.Failure, result.Level);
    }

    [Fact]
    public void A_target_that_was_already_down_is_not_blamed_on_the_work()
    {
        WorkComparison result = Single(
            Snapshot(Entry(responded: false, loss: 100)),
            Snapshot(Entry(responded: false, loss: 100)));

        Assert.Equal(WorkVerdict.StillDown, result.Verdict);
        Assert.Equal(VerdictLevel.Info, result.Level);
    }

    [Fact]
    public void Coming_back_is_reported_as_recovered()
    {
        WorkComparison result = Single(Snapshot(Entry(responded: false, loss: 100)), Snapshot(Entry()));

        Assert.Equal(WorkVerdict.Recovered, result.Verdict);
    }

    [Fact]
    public void A_disappeared_target_counts_as_a_failure()
    {
        // 測れなくなった以上「確認できた」とは言えない
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(Snapshot(Entry()), Snapshot());

        Assert.Equal(WorkVerdict.Removed, Assert.Single(results).Verdict);
        Assert.Equal(VerdictLevel.Failure, results[0].Level);
    }

    [Fact]
    public void A_new_target_is_reported_as_added()
    {
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(Snapshot(), Snapshot(Entry()));

        Assert.Equal(WorkVerdict.Added, Assert.Single(results).Verdict);
    }

    [Fact]
    public void Increased_loss_is_caught_even_though_it_still_responds()
    {
        // 片系断や LAG の片落ちで最も出る症状。不達でも遅延でもないので、
        // これが無いと完全に見落とす
        WorkComparison result = Single(Snapshot(Entry(loss: 0)), Snapshot(Entry(loss: 30)));

        Assert.Equal(WorkVerdict.LossIncreased, result.Verdict);
        Assert.Equal(VerdictLevel.Warning, result.Level);
    }

    [Fact]
    public void A_small_change_in_loss_is_ignored()
    {
        WorkComparison result = Single(Snapshot(Entry(loss: 0)), Snapshot(Entry(loss: 2)));

        Assert.Equal(WorkVerdict.Unchanged, result.Verdict);
    }

    [Fact]
    public void An_outage_during_the_work_is_caught_even_if_both_ends_look_fine()
    {
        // 冗長化の切替は必ずこの形になる。前後だけ見ると「変化なし」に見える
        WorkEntry entry = Entry();
        WorkComparison result = Single(Snapshot(entry), Snapshot(entry), entry.Key);

        Assert.Equal(WorkVerdict.Unstable, result.Verdict);
        Assert.Equal(VerdictLevel.Warning, result.Level);
    }

    [Fact]
    public void A_changed_address_is_reported()
    {
        WorkComparison result = Single(
            Snapshot(Entry(host: "vip.example.jp", address: "192.168.1.100")),
            Snapshot(Entry(host: "vip.example.jp", address: "192.168.1.200")));

        Assert.Equal(WorkVerdict.AddressChanged, result.Verdict);
    }

    [Fact]
    public void Comparing_icmp_against_tcp_is_refused()
    {
        // 測り方が違えば数字を比べても意味がない。「合格」と出す方が危ない
        WorkComparison result = Single(
            Snapshot(Entry(kind: "ICMP")),
            Snapshot(Entry(kind: "TCP:443")));

        Assert.Equal(WorkVerdict.NotComparable, result.Verdict);
        Assert.Equal(VerdictLevel.Unknown, result.Level);
    }

    [Fact]
    public void Too_few_samples_are_not_judged()
    {
        WorkComparison result = Single(Snapshot(Entry()), Snapshot(Entry(attempts: 3)));

        Assert.Equal(WorkVerdict.NotMeasured, result.Verdict);
        Assert.Equal(VerdictLevel.Unknown, result.Level);
    }

    [Theory]
    [InlineData(2, 3, false)]      // LAN 内の揺らぎ。倍率は満たすが差が小さい
    [InlineData(2, 20, true)]      // 倍率も差も満たす
    [InlineData(30, 55, true)]     // 倍率には届かないが差が大きい（経路が伸びた）
    [InlineData(30, 40, false)]    // どちらにも届かない
    [InlineData(0.3, 0.9, false)]  // 1ms 未満の揺らぎで騒がない
    public void Slowdowns_are_judged_by_ratio_and_absolute_difference(double before, double after, bool expected)
    {
        WorkComparison result = Single(
            Snapshot(Entry(average: before, p95: before)),
            Snapshot(Entry(average: after, p95: after)));

        Assert.Equal(expected, result.Verdict == WorkVerdict.Slower);
    }

    [Fact]
    public void A_slow_tail_is_caught_even_when_the_average_looks_fine()
    {
        // 平均は裾を隠す。p95 も見る
        WorkComparison result = Single(
            Snapshot(Entry(average: 5, p95: 6)),
            Snapshot(Entry(average: 6, p95: 60)));

        Assert.Equal(WorkVerdict.Slower, result.Verdict);
    }

    [Fact]
    public void Getting_faster_never_affects_the_verdict()
    {
        WorkComparison result = Single(
            Snapshot(Entry(average: 50, p95: 60)),
            Snapshot(Entry(average: 2, p95: 3)));

        Assert.Equal(WorkVerdict.Faster, result.Verdict);
        Assert.Equal(VerdictLevel.Info, result.Level);
    }

    [Fact]
    public void Problems_are_listed_first()
    {
        WorkSnapshot before = Snapshot(
            Entry(host: "a"),
            Entry(host: "b"),
            Entry(host: "c"));

        WorkSnapshot after = Snapshot(
            Entry(host: "a"),
            Entry(host: "b", responded: false, loss: 100),
            Entry(host: "c", loss: 40));

        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(before, after);

        Assert.Equal(WorkVerdict.Lost, results[0].Verdict);           // 問題
        Assert.Equal(WorkVerdict.LossIncreased, results[1].Verdict);  // 要確認
        Assert.Equal(WorkVerdict.Unchanged, results[2].Verdict);      // 情報
    }

    [Fact]
    public void A_clean_run_passes()
    {
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(
            Snapshot(Entry(host: "a"), Entry(host: "b")),
            Snapshot(Entry(host: "a"), Entry(host: "b")));

        WorkSummary summary = BaselineComparer.Summarize(results);

        Assert.True(summary.IsPass);
        Assert.Equal("問題なし", summary.Verdict);
    }

    [Fact]
    public void Unjudged_targets_stop_it_from_passing()
    {
        // 数件測れていないのに「合格」と出したら、このツールを使う意味が無くなる
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(
            Snapshot(Entry(host: "a"), Entry(host: "b")),
            Snapshot(Entry(host: "a"), Entry(host: "b", attempts: 2)));

        WorkSummary summary = BaselineComparer.Summarize(results);

        Assert.False(summary.IsPass);
        Assert.Equal("未判定あり", summary.Verdict);
        Assert.Equal(1, summary.Unknowns);
    }

    [Fact]
    public void Warnings_alone_do_not_make_it_fail()
    {
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(
            Snapshot(Entry()),
            Snapshot(Entry(loss: 30)));

        WorkSummary summary = BaselineComparer.Summarize(results);

        Assert.Equal("要確認", summary.Verdict);
        Assert.Equal(1, summary.Warnings);
        Assert.Equal(0, summary.Failures);
    }

    [Fact]
    public void Failures_win_over_everything()
    {
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(
            Snapshot(Entry(host: "a"), Entry(host: "b")),
            Snapshot(Entry(host: "a", responded: false, loss: 100), Entry(host: "b", attempts: 2)));

        WorkSummary summary = BaselineComparer.Summarize(results);

        Assert.Equal("要対応", summary.Verdict);
        Assert.False(summary.IsPass);
    }

    [Fact]
    public void Every_verdict_has_a_label()
    {
        foreach (WorkVerdict verdict in Enum.GetValues<WorkVerdict>())
        {
            string label = BaselineComparer.Label(verdict);

            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(verdict.ToString(), label);
        }
    }

    [Fact]
    public void Duplicate_keys_do_not_throw()
    {
        // セッション JSON は手で編集されることもある。重複キーで落ちない
        IReadOnlyList<WorkComparison> results = BaselineComparer.Compare(
            Snapshot(Entry(), Entry()),
            Snapshot(Entry()));

        Assert.Single(results);
    }

    [Fact]
    public void A_thin_baseline_cannot_declare_slower()
    {
        // 2 回しか測っていない作業前を基準に「遅くなった」と断定しない
        WorkComparison result = Single(
            Snapshot(Entry(attempts: 2, average: 1)),
            Snapshot(Entry(average: 30)));

        Assert.Equal(WorkVerdict.NotMeasured, result.Verdict);
    }
}
