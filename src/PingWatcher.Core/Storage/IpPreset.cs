namespace PingWatcher.Core.Storage;

/// <summary>
/// IP 設定タブの名前付きプリセット(現場ごとのアドレス設計)。
///
/// 値は文字列のまま持つ — 適用時に必ず IpPlan.Parse を通すので検証が一元化され、
/// settings.json を手で書く人にも優しい。<b>アダプタ名は保存しない</b>
/// (プリセット=現場の設計、アダプタ=その PC の事情。混ぜると PC を替えた
/// 瞬間に壊れる。適用先は毎回選ぶ)。
/// </summary>
public sealed class IpPreset
{
    public string Name { get; set; } = "";

    public bool Dhcp { get; set; }

    public string Address { get; set; } = "";

    public string Mask { get; set; } = "";

    public string Gateway { get; set; } = "";

    public string Dns1 { get; set; } = "";

    public string Dns2 { get; set; } = "";
}
