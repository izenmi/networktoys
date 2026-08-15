namespace PingWatcher.Core.Storage;

/// <summary>
/// settings.json の中身。アプリの設定はこの 1 ファイルにまとめる
/// （以前は targets.json / tcp-targets.json / theme.txt / columns.txt に
/// 分かれていた。初回起動時に旧ファイルから引き継いで統合する）。
/// </summary>
public sealed class AppSettingsDocument
{
    /// <summary>将来フォーマットを変えたときの移行判断用。</summary>
    public int Version { get; set; } = 1;

    /// <summary>配色。"dark" / "light"。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>
    /// Ping/TCP 一覧の列幅（状態・宛先・RTT・ロス・推移の順）。
    /// 空なら既定幅。並びが変わったら Version を上げて捨てる。
    /// </summary>
    public List<double> Columns { get; set; } = [];

    /// <summary>Ping 画面の宛先リストと測定の既定値。</summary>
    public TargetDocument Ping { get; set; } = new();

    /// <summary>TCP 画面の宛先リストと測定の既定値。</summary>
    public TargetDocument Tcp { get; set; } = new();
}
