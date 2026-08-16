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

    /// <summary>収集タブの機器一覧（<c>ホスト[:ポート],ユーザー名[,メモ]</c>）。</summary>
    public string CollectDevices { get; set; } = "";

    /// <summary>収集タブのコマンド一覧。空なら機種プリセットの初期値を使う。</summary>
    public string CollectCommands { get; set; } = "";

    /// <summary>
    /// 収集タブで前に使ったユーザー名（ホスト名がキー）。
    ///
    /// <b>パスワードはここに入れない。</b>入れ物も作らない — 保存しないと決めたものは、
    /// 置き場所を用意した時点で誰かが入れてしまう。
    /// </summary>
    public Dictionary<string, string> CollectUserNames { get; set; } = [];

    /// <summary>業務確認試験の項目（1 行 1 件のテキスト）。</summary>
    public string VerifyChecks { get; set; } = "";

    /// <summary>
    /// 試験に使うプロキシの定義（1 行 1 件で <c>名前,種類,アドレス</c>）。
    /// 統合 Windows 認証で通す前提なので、<b>ここにも認証情報は入れない</b>。
    /// </summary>
    public string VerifyProxies { get; set; } = "";

    /// <summary>
    /// 自分で作った試験のひな型（名前 → 項目のテキスト）。
    ///
    /// 現場ごとに試す項目は決まっているので、<b>作ったものを名前を付けて残せる</b>ようにする。
    /// 組み込みのひな型（標準 / Microsoft 365）とは別に持ち、同じ名前なら自分のものが勝つ。
    /// </summary>
    public Dictionary<string, string> VerifyTemplates { get; set; } = [];
}
