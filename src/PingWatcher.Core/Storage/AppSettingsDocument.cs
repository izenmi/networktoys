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
    public string Theme { get; set; } = "light";

    /// <summary>
    /// Ping/TCP 一覧の列幅（状態・宛先・RTT・ロス・推移の順）。
    /// 空なら既定幅。並びが変わったら Version を上げて捨てる。
    /// </summary>
    public List<double> Columns { get; set; } = [];

    /// <summary>
    /// Ping/TCP 以外の一覧の列幅。キーは「テーブル名.列番号」。
    /// 知らないキーは読み飛ばすので、列を足し引きしても壊れない。
    /// </summary>
    public Dictionary<string, double> TableColumns { get; set; } = [];

    /// <summary>Ping 画面の宛先リストと測定の既定値。</summary>
    public TargetDocument Ping { get; set; } = new();

    /// <summary>TCP 画面の宛先リストと測定の既定値。</summary>
    public TargetDocument Tcp { get; set; } = new();

    /// <summary>IP 設定タブの名前付きプリセット。</summary>
    public List<IpPreset> IpPresets { get; set; } = [];

    /// <summary>
    /// Tera Term (ttermpro.exe) の場所。空なら既定の導入先から探す。
    /// 見つからないときに選んでもらった場所をここに覚える。
    /// </summary>
    public string TeraTermPath { get; set; } = "";
}
