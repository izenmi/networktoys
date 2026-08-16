namespace PingWatcher.Core.Verify;

/// <summary>
/// メールサーバが返す最初の 1 行（バナー）で合否を決める。
///
/// 実際の送受信は人が Outlook でやるものとして、ここは
/// <b>「サーバが応答を返すところまで」</b>を確かめる。それだけでも
/// 「経路が塞がれている」と「サーバが落ちている」は切り分けられる。
/// </summary>
public static class BannerCheck
{
    /// <summary>種類ごとに、バナーの先頭がこれで始まれば正常。</summary>
    public static string ExpectedPrefix(CheckKind kind) => kind switch
    {
        CheckKind.Smtp => "220",
        CheckKind.Imap => "* OK",
        CheckKind.Pop3 => "+OK",
        _ => "",
    };

    /// <summary>
    /// バナーを判定する。
    ///
    /// <b>先頭の空白は落とすが、大文字小文字は問う。</b>プロトコルで綴りが決まっているため。
    /// 途中で切れた応答（改行が来ないまま接続が切れた）も、先頭さえ合っていれば通す —
    /// 見たいのは「サーバが名乗ったか」であって全文ではない。
    /// </summary>
    public static bool Matches(CheckKind kind, string? banner)
    {
        string expected = ExpectedPrefix(kind);
        if (expected.Length == 0) return false;

        return (banner ?? "").TrimStart().StartsWith(expected, StringComparison.Ordinal);
    }

    /// <summary>証跡に残す 1 行。長いバナーは切り詰める（Exchange は数十文字返す）。</summary>
    public static string Summarize(string? banner)
    {
        string text = (banner ?? "").ReplaceLineEndings(" ").Trim();

        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
