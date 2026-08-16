namespace PingWatcher.Core.Verify;

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
        名前が引ける,DNS,www.example.jp
        インターネットが見られる,HTTP,https://www.example.jp/

        # 社内 Web
        社内ポータルが開く,HTTP,http://portal.example.jp/
        業務システムのログイン画面が出る,HTTP,http://app.example.jp/login,ログイン

        # Teams（音声の道まで確かめます）
        Teams が使える,Teams

        # メール（サーバが応答するところまで。送受信そのものは Outlook で）
        メール送信,SMTP,mail.example.jp:587
        メール受信,IMAP,mail.example.jp:993

        # 社内資源
        ファイルサーバに繋がる,TCP,fs01.example.jp:445
        プリンタに繋がる,TCP,printer01.example.jp:9100
        """;

    /// <summary>Microsoft 365 を使う現場向け。宛先が公開されているので書き換え不要。</summary>
    public const string Microsoft365 = """
        # Microsoft 365 の主要な経路

        Teams が使える,Teams
        Teams のサイトが開く,HTTP,https://teams.microsoft.com/
        Outlook on the web が開く,HTTP,https://outlook.office365.com/
        Exchange Online に繋がる,TCP,outlook.office365.com:443
        SharePoint が開く,HTTP,https://www.office.com/
        名前が引ける,DNS,teams.microsoft.com
        """;

    /// <summary>画面のコンボに出す選択肢。</summary>
    public static IReadOnlyList<(string Name, string Text)> Templates =>
    [
        ("標準（社内＋インターネット）", Standard),
        ("Microsoft 365", Microsoft365),
    ];
}
