using PingWatcher.Core.Work;

namespace PingWatcher.Core.Verify;

/// <summary>試験の判定。</summary>
public enum CheckVerdict
{
    /// <summary>まだ実行していない。</summary>
    NotRun,

    /// <summary>合格。</summary>
    Pass,

    /// <summary>不合格。</summary>
    Fail,

    /// <summary>
    /// 通ってはいるが目安を割っている。<b>合格とも不合格とも言えない状態は実際にある</b>
    /// （通話はできるが音が途切れる、など）。合格に丸めると現場で拾えなくなる。
    /// </summary>
    Warn,

    /// <summary>試験できなかった（宛先が空、種類に対して指定が足りない など）。</summary>
    Skipped,

    /// <summary>ブラウザで開いたので、人が見て合否を付ける番。</summary>
    AwaitingPerson,
}

/// <summary>
/// 試験 1 回ぶんの結果。<b>1 行 = 1 項目 × 1 プロキシ。</b>
/// </summary>
/// <param name="Name">項目名。</param>
/// <param name="Kind">種類。</param>
/// <param name="Target">実際に試した宛先。</param>
/// <param name="ProxyName">使ったプロキシの名前。使わない種類は空。</param>
/// <param name="Verdict">合否。</param>
/// <param name="Detail">根拠（応答コード・所要時間・エラーの内容）。</param>
/// <param name="ElapsedMs">所要時間。測れなかったら null。</param>
public sealed record CheckResult(
    string Name,
    CheckKind Kind,
    string Target,
    string ProxyName,
    CheckVerdict Verdict,
    string Detail,
    double? ElapsedMs = null)
{
    /// <summary>
    /// 画面に出す合否。<b>色だけで表さない</b>ので記号と文字を併記する。
    /// </summary>
    public string VerdictText => Verdict switch
    {
        CheckVerdict.Pass => "○ 合格",
        CheckVerdict.Fail => "✕ 不合格",
        CheckVerdict.Warn => "△ 注意",
        CheckVerdict.Skipped => "— 試験せず",
        CheckVerdict.AwaitingPerson => "◍ 目視で確認",
        _ => "◌ 未実行",
    };

    public bool IsPass => Verdict == CheckVerdict.Pass;
    public bool IsFail => Verdict == CheckVerdict.Fail;
    public bool IsWarn => Verdict == CheckVerdict.Warn;

    /// <summary>人の判定を待っている。<b>合否が付くまで試験は終わっていない。</b></summary>
    public bool NeedsPerson => Verdict == CheckVerdict.AwaitingPerson;

    /// <summary>プロキシを使わない種類は「—」。空欄だと抜けに見える。</summary>
    public string ProxyText => ProxyName.Length > 0 ? ProxyName : "—";

    public string ElapsedText => ElapsedMs is { } ms ? $"{ms:0} ms" : "";
}

/// <summary>試験結果を証跡の形にする。</summary>
public static class CheckReport
{
    /// <summary>Excel の試験成績書へ貼る前提の表。</summary>
    public static CsvTable ToCsv(IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return new CsvTable(
            ["項目", "種類", "宛先", "プロキシ", "合否", "所要", "詳細"],
            [.. results.Select(r => new[]
            {
                r.Name,
                CheckListParser.NameOf(r.Kind),
                r.Target,
                r.ProxyText,
                r.VerdictText,
                r.ElapsedText,
                r.Detail,
            })]);
    }

    /// <summary>画面の状態欄に出す 1 行。</summary>
    public static string Summarize(IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0) return "";

        int pass = results.Count(r => r.IsPass);
        int fail = results.Count(r => r.IsFail);
        int warn = results.Count(r => r.IsWarn);
        int skipped = results.Count(r => r.Verdict == CheckVerdict.Skipped);

        int waiting = results.Count(r => r.NeedsPerson);

        var tail = new List<string>();
        if (warn > 0) tail.Add($"注意 {warn}");
        if (skipped > 0) tail.Add($"試験せず {skipped}");

        string extra = tail.Count > 0 ? " / " + string.Join(" / ", tail) : "";

        // 人の判定待ちが残っているうちは「終わった」と言わない
        string pending = waiting > 0 ? $"　ブラウザで開いた {waiting} 件に合否を付けてください。" : "";

        if (fail > 0)
            return $"✕ 不合格が {fail} 件あります（合格 {pass}{extra}）。{pending}";

        return warn > 0
            ? $"△ 不合格はありませんが注意が {warn} 件あります（合格 {pass}{extra}）。{pending}"
            : $"自動の判定はすべて合格しました（{pass} 件{extra}）。{pending}";
    }
}
