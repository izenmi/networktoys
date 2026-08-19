namespace NetworkToys.Core.Verify;

/// <summary>
/// 業務確認試験のひな型。
///
/// 現場ごとに宛先は違うので、<b>そのまま使える形ではなく「書き換える枠」</b>として渡す。
/// 例示のホスト名は RFC 2606 の予約ドメイン（<c>example.jp</c> など）にしてあるので、
/// 消し忘れても実在の他人へ試験を投げてしまうことはない。
///
/// 並びは <b>基本の疎通 → Web → メール → 社内資源</b>。上から順に潰していける。
/// 収集タブの <see cref="Terminal.RecommendedCommands"/> と同じ流儀で定数として持つ。
/// </summary>
public static class RecommendedChecks
{
    /// <summary>ひとそろいの雛形。書式は <c>項目名,種類,宛先,期待</c>。</summary>
    public const string Standard = """
        # 宛先は現場に合わせて書き換えてください
        # 書式: 項目名,種類,宛先,期待するもの(任意)

        # 基本
        自分のホスト名が引ける,DNS
        名前が引ける,DNS,www.example.jp
        インターネットが見られる,HTTP,https://www.example.jp/

        # 社内 Web
        社内ポータルが開く,HTTP,http://portal.example.jp/
        業務システムのログイン画面が出る,HTTP,http://app.example.jp/login,ログイン

        # ブラウザでないと開けないページ（ログインが要る・証明書を選ぶ・JS で描画される）。
        # 実行するとブラウザで開くので、見て ○ か ✕ を押してください
        勤怠システムにログインできる,手動,https://kintai.example.jp/,ログイン後のトップ画面
        経費精算が開く,手動,https://keihi.example.jp/

        # Teams（音声の道まで確かめます）
        Teams が使える,Teams

        # メール（サーバが応答するところまで。送受信そのものは Outlook で）
        メール送信,SMTP,mail.example.jp:587
        メール受信,IMAP,mail.example.jp:993

        # 社内資源
        ファイルサーバに繋がる,TCP,fs01.example.jp:445
        プリンタに繋がる,TCP,printer01.example.jp:9100

        # 速度
        # 「期待」欄に Mbps で目安を書くと、下回ったとき「△ 注意」になります
        回線速度,fast.com,,20

        # 宛先を決めて測りたいときはこちら（社内のファイルサーバなど）
        # 回線速度（下り）,速度,https://example.jp/large.bin,20

        # 上りは受け取ってくれる相手が要ります。用意できたら行頭の # を外してください
        # 回線速度（上り）,速度上り,https://example.jp/upload|20
        """;

    /// <summary>画面のコンボに出す選択肢。</summary>
    public static IReadOnlyList<(string Name, string Text)> Templates =>
    [
        ("標準（社内＋インターネット）", Standard),
    ];
}
