namespace NetworkToys.Core.Storage;

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

    /// <summary>
    /// Ping 画面の<b>名前を付けて残した宛先リスト</b>（名前 → 宛先のテキスト）。
    ///
    /// 現場ごとに測る相手は決まっているので、いくつか持って切り替えられるようにする
    /// （2026-08-17 ユーザー指示）。いま画面に出ているものは <see cref="Ping"/> の方。
    /// </summary>
    public Dictionary<string, string> PingTargetLists { get; set; } = [];

    /// <summary>TCP 画面の名前を付けて残した宛先リスト。Ping と混ざらないよう分けてある。</summary>
    public Dictionary<string, string> TcpTargetLists { get; set; } = [];

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

    /// <summary>ACI タブの接続先（APIC のホスト名か IP）。1 行 1 件。</summary>
    public string AciHosts { get; set; } = "";

    /// <summary>
    /// ACI タブで前に使ったユーザー名（ホストがキー）。
    /// <b>パスワードはここに入れない</b>（収集タブと同じ決まり）。
    /// </summary>
    public Dictionary<string, string> AciUserNames { get; set; } = [];

    /// <summary>
    /// 受け入れた APIC の証明書の指紋（ホストがキー、値は <c>SHA256:…</c>）。
    ///
    /// 指紋は秘密ではないので保存してよい（SSH の known_hosts と同じ）。
    /// <b>ここに入るのは人が画面で見比べて受け入れたものだけ。</b>
    /// 勝手に足すと、証明書のすり替わりに気づけなくなる。
    /// </summary>
    public Dictionary<string, string> AciFingerprints { get; set; } = [];

    /// <summary>WLC タブの接続先（Catalyst 9800 のホスト名か IP）。1 行 1 件。</summary>
    public string WlcHosts { get; set; } = "";

    /// <summary>
    /// WLC タブで前に使ったユーザー名（ホストがキー）。
    /// <b>パスワードはここに入れない</b>（収集タブ・ACI と同じ決まり）。
    /// </summary>
    public Dictionary<string, string> WlcUserNames { get; set; } = [];

    /// <summary>Catalyst Center タブの接続先（ホスト名か IP）。1 行 1 件。</summary>
    public string DnacHosts { get; set; } = "";

    /// <summary>
    /// Catalyst Center タブで前に使ったユーザー名（ホストがキー）。
    /// <b>パスワードはここに入れない</b>（収集タブ・ACI・WLC と同じ決まり）。
    /// </summary>
    public Dictionary<string, string> DnacUserNames { get; set; } = [];

    /// <summary>受け入れた Catalyst Center の証明書の指紋（ホストがキー）。ACI と同じ扱い。</summary>
    public Dictionary<string, string> DnacFingerprints { get; set; } = [];

    /// <summary>
    /// ウィンドウを最前面に固定するか。
    ///
    /// メニューにチェック項目があるのに保存していなかったため、
    /// 毎回外れていた（2026-08-16 に気づいて足した）。
    /// </summary>
    public bool Topmost { get; set; }

    /// <summary>
    /// 文字の大きさの倍率（0.85 / 1.0 / 1.25 / 1.5）。
    /// <b>読めない値は 1.0 に落とす</b> — 設定ファイルは手でも書き換えられる。
    /// </summary>
    public double UiScale { get; set; } = 1.0;

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
