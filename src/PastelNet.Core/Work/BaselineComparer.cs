namespace PastelNet.Core.Work;

/// <summary>作業前と作業後を突き合わせた結果。</summary>
public enum WorkVerdict
{
    /// <summary>変わっていない。</summary>
    Unchanged,

    /// <summary>前は応答していたのに、今は応答しない。<b>作業で壊した可能性が最も高い。</b></summary>
    Lost,

    /// <summary>宛先そのものが無くなった。測れなくなった以上、確認できていない。</summary>
    Removed,

    /// <summary>
    /// 応答はするが、ロス率が明らかに悪化した。
    /// 片系断や LAG の片落ちで最も出る症状で、不達にも遅延にも当てはまらない。
    /// </summary>
    LossIncreased,

    /// <summary>応答はするが、明らかに遅くなった。</summary>
    Slower,

    /// <summary>名前の解決先が変わった。DHCP の再割当や VIP の切り替わり。</summary>
    AddressChanged,

    /// <summary>
    /// 前後とも応答しているが、<b>その間に不通があった</b>。
    /// 冗長化の切替は必ずこの形になる。前後だけ見ると「変化なし」に見えてしまう。
    /// </summary>
    Unstable,

    /// <summary>作業後に増えた宛先。</summary>
    Added,

    /// <summary>前も今も応答しない。<b>作業前から落ちていた</b>ので、作業のせいではない。</summary>
    StillDown,

    /// <summary>応答が戻った。</summary>
    Recovered,

    /// <summary>速くなった。情報として出すだけ。</summary>
    Faster,

    /// <summary>前後で測り方が違う（ICMP と TCP など）。突き合わせても意味がない。</summary>
    NotComparable,

    /// <summary>作業後の試行回数が足りない、または測っていない。</summary>
    NotMeasured,
}

/// <summary>判定をどう扱うか。</summary>
public enum VerdictLevel
{
    /// <summary>情報。</summary>
    Info,

    /// <summary>要確認。合否は下げないが目を通すべき。</summary>
    Warning,

    /// <summary>問題あり。</summary>
    Failure,

    /// <summary>判定できていない。<b>これが残っているうちは合格にしない。</b></summary>
    Unknown,
}

/// <param name="Key">Host|Kind|Port。</param>
/// <param name="Host">表示用。</param>
/// <param name="Comment">備考。</param>
/// <param name="Before">作業前。無ければ null。</param>
/// <param name="After">作業後。無ければ null。</param>
/// <param name="Verdict">判定。</param>
/// <param name="Detail">人が読む説明。</param>
public sealed record WorkComparison(
    string Key,
    string Host,
    string Comment,
    WorkEntry? Before,
    WorkEntry? After,
    WorkVerdict Verdict,
    string Detail)
{
    public VerdictLevel Level => BaselineComparer.LevelOf(Verdict);

    /// <summary>画面とレポートで同じ言い方をするための短い日本語。</summary>
    public string Label => BaselineComparer.Label(Verdict);
}

/// <param name="Total">宛先の総数。</param>
/// <param name="Failures">問題ありの件数。</param>
/// <param name="Warnings">要確認の件数。</param>
/// <param name="Unknowns">判定できていない件数。</param>
/// <param name="Outages">作業中に観測した不通の件数。</param>
public sealed record WorkSummary(int Total, int Failures, int Warnings, int Unknowns, int Outages)
{
    /// <summary>
    /// 全体の判定。
    /// <b>判定できていない宛先が残っているうちは合格にしない。</b>
    /// 数件測れていないのに合格と出たら、このツールを使う意味が無くなる。
    /// </summary>
    public string Verdict => Failures > 0
        ? "要対応"
        : Unknowns > 0
            ? "未判定あり"
            : Warnings > 0
                ? "要確認"
                : "問題なし";

    public bool IsPass => Failures == 0 && Unknowns == 0;

    public string Headline => Failures > 0
        ? $"{Failures} 件が作業前と変わっています"
        : Unknowns > 0
            ? $"{Unknowns} 件が判定できていません"
            : Warnings > 0
                ? $"{Warnings} 件に気になる変化があります"
                : $"{Total} 件すべて作業前と同じ状態です";
}

/// <summary>比較の閾値。</summary>
public sealed class ComparisonOptions
{
    /// <summary>この回数に満たないベースラインは信用しない。</summary>
    public int MinimumAttempts { get; init; } = 10;

    /// <summary>ロス率がこのポイント数以上悪化したら拾う。</summary>
    public double LossIncreasePoints { get; init; } = 5;

    /// <summary>大きく伸びたと見なす倍率。</summary>
    public double SlowerRatio { get; init; } = 2.0;

    /// <summary>上の倍率と併せて課す絶対差（ms）。1ms が 2ms になっただけで騒がないため。</summary>
    public double SlowerMinimumDeltaMs { get; init; } = 10;

    /// <summary>
    /// 倍率は控えめでも実害のある伸び方を拾うための、ゆるい方の倍率。
    /// 30ms→55ms のような経路が伸びた変化は 2 倍に届かないが、見逃してはいけない。
    /// </summary>
    public double ModerateRatio { get; init; } = 1.5;

    /// <summary>ゆるい方の倍率と併せて課す絶対差（ms）。WAN の 200ms→220ms を拾わないため。</summary>
    public double ModerateDeltaMs { get; init; } = 20;
}

/// <summary>
/// 作業前後を突き合わせる。純粋な計算なので、既知の入力と期待値でテストできる。
/// </summary>
public static class BaselineComparer
{
    public static VerdictLevel LevelOf(WorkVerdict verdict) => verdict switch
    {
        WorkVerdict.Lost or WorkVerdict.Removed => VerdictLevel.Failure,

        WorkVerdict.LossIncreased or WorkVerdict.Slower
            or WorkVerdict.AddressChanged or WorkVerdict.Unstable => VerdictLevel.Warning,

        WorkVerdict.NotComparable or WorkVerdict.NotMeasured => VerdictLevel.Unknown,

        _ => VerdictLevel.Info,
    };

    /// <param name="before">作業前。</param>
    /// <param name="after">作業後。</param>
    /// <param name="keysWithOutage">作業中に不通があった宛先の鍵。前後とも正常でも「途中で切れた」を拾うのに使う。</param>
    /// <param name="options">閾値。</param>
    public static IReadOnlyList<WorkComparison> Compare(
        WorkSnapshot before,
        WorkSnapshot after,
        IReadOnlyCollection<string>? keysWithOutage = null,
        ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        options ??= new ComparisonOptions();
        keysWithOutage ??= [];

        var beforeByKey = before.Entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        var afterByKey = after.Entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

        var results = new List<WorkComparison>();

        foreach (WorkEntry entry in before.Entries)
        {
            if (afterByKey.TryGetValue(entry.Key, out WorkEntry? current))
            {
                results.Add(CompareOne(entry, current, keysWithOutage.Contains(entry.Key), options));
            }
            else
            {
                results.Add(new WorkComparison(
                    entry.Key, entry.Host, entry.Comment, entry, null,
                    WorkVerdict.Removed,
                    "作業後の一覧から消えているため、確認できていません。"));
            }
        }

        foreach (WorkEntry entry in after.Entries)
        {
            if (!beforeByKey.ContainsKey(entry.Key))
            {
                results.Add(new WorkComparison(
                    entry.Key, entry.Host, entry.Comment, null, entry,
                    WorkVerdict.Added,
                    entry.Responded ? "作業後に追加され、応答しています。" : "作業後に追加されましたが、応答がありません。"));
            }
        }

        // 手当てが要るものを上に
        return [.. results.OrderByDescending(r => (int)r.Level).ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase)];
    }

    private static WorkComparison CompareOne(WorkEntry before, WorkEntry after, bool hadOutage, ComparisonOptions options)
    {
        WorkComparison Make(WorkVerdict verdict, string detail)
            => new(before.Key, before.Host, before.Comment, before, after, verdict, detail);

        // 測り方が違えば、数字を比べても意味がない
        if (!string.Equals(before.Kind, after.Kind, StringComparison.OrdinalIgnoreCase))
            return Make(WorkVerdict.NotComparable, $"測り方が違います（作業前 {before.Kind} / 作業後 {after.Kind}）。");

        if (after.Attempts < options.MinimumAttempts)
            return Make(WorkVerdict.NotMeasured, $"作業後の測定が {after.Attempts} 回しかありません。もう少し測ってから比べてください。");

        if (before.Responded && !after.Responded)
            return Make(WorkVerdict.Lost, "作業前は応答していましたが、応答しなくなりました。");

        if (!before.Responded && !after.Responded)
            return Make(WorkVerdict.StillDown, "作業前から応答がありません。この作業で落ちたものではありません。");

        if (!before.Responded && after.Responded)
            return Make(WorkVerdict.Recovered, "作業前は応答がありませんでしたが、応答するようになりました。");

        // ここから先は前後とも応答している

        double lossDelta = after.LossPercent - before.LossPercent;
        if (lossDelta >= options.LossIncreasePoints)
        {
            return Make(WorkVerdict.LossIncreased,
                $"応答はしますが、ロス率が {before.LossPercent:0.#}% から {after.LossPercent:0.#}% に増えています。");
        }

        if (!string.Equals(before.Address, after.Address, StringComparison.OrdinalIgnoreCase)
            && before.Address.Length > 0 && after.Address.Length > 0)
        {
            return Make(WorkVerdict.AddressChanged,
                $"解決先が {before.Address} から {after.Address} に変わりました。");
        }

        // 前後とも正常でも、その間に切れていれば見逃さない。冗長化の切替はこの形になる
        if (hadOutage)
            return Make(WorkVerdict.Unstable, "作業前後は応答していますが、作業中に不通がありました。");

        if (IsSlower(before.AverageMs, after.AverageMs, options) || IsSlower(before.P95Ms, after.P95Ms, options))
        {
            return Make(WorkVerdict.Slower,
                $"応答が遅くなりました（平均 {before.AverageMs:0.#} → {after.AverageMs:0.#} ms）。");
        }

        if (IsSlower(after.AverageMs, before.AverageMs, options))
        {
            return Make(WorkVerdict.Faster,
                $"応答が速くなりました（平均 {before.AverageMs:0.#} → {after.AverageMs:0.#} ms）。");
        }

        return Make(WorkVerdict.Unchanged, "作業前と同じ状態です。");
    }

    /// <summary>
    /// 倍率と絶対差の両方を課す。それを二段構えにする。
    ///
    /// ・倍率だけで判定すると 0.3ms→0.9ms（3倍）を騒ぎ立てる
    /// ・絶対差だけで判定すると WAN の 200ms→220ms を拾ってしまう
    /// ・厳しい方だけだと 30ms→55ms（1.8倍・差25ms）という経路が伸びた変化を見逃す
    ///
    /// なので「大きく伸びた」と「そこそこ伸びて差も大きい」の二本立てにしている。
    /// </summary>
    private static bool IsSlower(double before, double after, ComparisonOptions options)
    {
        if (before <= 0 || after <= 0) return false;

        double delta = after - before;
        if (delta <= 0) return false;

        double ratio = after / before;

        bool clearlySlower = ratio >= options.SlowerRatio && delta >= options.SlowerMinimumDeltaMs;
        bool moderatelySlower = ratio >= options.ModerateRatio && delta >= options.ModerateDeltaMs;

        return clearlySlower || moderatelySlower;
    }

    public static WorkSummary Summarize(IReadOnlyList<WorkComparison> comparisons, int outageCount = 0)
    {
        ArgumentNullException.ThrowIfNull(comparisons);

        return new WorkSummary(
            comparisons.Count,
            comparisons.Count(c => c.Level == VerdictLevel.Failure),
            comparisons.Count(c => c.Level == VerdictLevel.Warning),
            comparisons.Count(c => c.Level == VerdictLevel.Unknown),
            outageCount);
    }

    /// <summary>判定を日本語の短い言葉にする。UI とレポートで同じ言い方を使う。</summary>
    public static string Label(WorkVerdict verdict) => verdict switch
    {
        WorkVerdict.Unchanged => "変化なし",
        WorkVerdict.Lost => "応答しなくなった",
        WorkVerdict.Removed => "宛先が消えた",
        WorkVerdict.LossIncreased => "ロスが増えた",
        WorkVerdict.Slower => "遅くなった",
        WorkVerdict.AddressChanged => "解決先が変わった",
        WorkVerdict.Unstable => "途中で切れた",
        WorkVerdict.Added => "追加された",
        WorkVerdict.StillDown => "作業前から不達",
        WorkVerdict.Recovered => "復旧した",
        WorkVerdict.Faster => "速くなった",
        WorkVerdict.NotComparable => "比較不能",
        WorkVerdict.NotMeasured => "測定不足",
        _ => verdict.ToString(),
    };
}
