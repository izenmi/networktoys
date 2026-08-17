namespace NetworkToys.Core.Work;

/// <summary>その変化をどれくらい重く見るか。</summary>
public enum ChangeSeverity
{
    /// <summary>知らせるだけ。</summary>
    Info,

    /// <summary>目を通してほしい。</summary>
    Warning,

    /// <summary>作業で壊した可能性がある。</summary>
    Critical,
}

/// <summary>
/// 機器の出力を前後で比べたときの変化 1 件。
///
/// 経路表・インターフェース・隣接機器・MAC で共通の形にしておくと、
/// 画面が 1 つで済む。
/// </summary>
/// <param name="Key">対象を指す文字列（宛先、ポート名、MAC など）。</param>
/// <param name="Kind">変化の種類を表す短い言葉。</param>
/// <param name="Description">そのまま読める説明。</param>
/// <param name="Severity">重さ。</param>
public sealed record DeviceChange(string Key, string Kind, string Description, ChangeSeverity Severity)
{
    public static DeviceChange Critical(string key, string kind, string description)
        => new(key, kind, description, ChangeSeverity.Critical);

    public static DeviceChange Warning(string key, string kind, string description)
        => new(key, kind, description, ChangeSeverity.Warning);

    public static DeviceChange Info(string key, string kind, string description)
        => new(key, kind, description, ChangeSeverity.Info);
}

/// <param name="Headline">要約。</param>
/// <param name="Changes">変化の一覧。重いものが先。</param>
/// <param name="Note">読むときの注意（ノイズが出やすい対象で使う）。</param>
public sealed record DeviceCompareOutcome(string Headline, IReadOnlyList<DeviceChange> Changes, string? Note = null)
{
    public bool HasChanges => Changes.Count > 0;

    public int CriticalCount => Changes.Count(c => c.Severity == ChangeSeverity.Critical);

    public int WarningCount => Changes.Count(c => c.Severity == ChangeSeverity.Warning);

    /// <summary>重いものを先に並べ替える。</summary>
    public static IReadOnlyList<DeviceChange> Order(IEnumerable<DeviceChange> changes)
        => [.. changes.OrderByDescending(c => (int)c.Severity).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)];
}
