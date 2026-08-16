namespace PingWatcher.Core.Net;

/// <summary>
/// WinHTTP のプロキシ設定を <c>netsh</c> のスクリプト行にする。
///
/// <b>WinINET（IP設定タブの上段）とは別の設定。</b>あちらはログインしたユーザーごとの
/// 設定でブラウザが従うもの。こちらは<b>PC 全体で 1 つ</b>で、サービスとして動くもの
/// （Windows Update・.NET のアプリ・監視エージェント・PowerShell の一部）が従う。
/// 「ブラウザは見えるのに更新やエージェントだけ外に出られない」の典型的な原因で、
/// 画面から確かめる手段が無いまま放置されやすい。
///
/// 書き込みには管理者権限が要るので、IP設定の適用と同じく
/// <c>netsh -f 一時スクリプト</c> を昇格して流す。
/// <b>読み取りは netsh の出力を読まない</b>（ロケール依存で壊れる）。
/// WinHTTP の API から直に取る。
///
/// 文字列の組み立てだけをここに置いて、CI で固める。
/// </summary>
public static class WinHttpProxyScript
{
    /// <summary>
    /// 適用のためのスクリプト行。
    ///
    /// <b>PAC は指定できない。</b>WinHTTP の既定設定は「直接」か「固定のプロキシ」の
    /// 2 つしか持てず、自動構成スクリプトという概念が無い。PAC を使う現場では
    /// PAC が返す代表的なプロキシを固定で入れることになる。
    /// </summary>
    public static IReadOnlyList<string> Build(ProxyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Mode != ProxyMode.Fixed)
            return ["winhttp reset proxy"];

        string line = $"winhttp set proxy proxy-server=\"{Sanitize(plan.Server)}\"";

        return plan.Bypass.Length > 0
            ? [line + $" bypass-list=\"{Sanitize(plan.Bypass)}\""]
            : [line];
    }

    /// <summary>
    /// 引用符と改行を落とす。
    ///
    /// この文字列は<b>昇格して実行されるスクリプトに入る</b>ので、引用符を閉じられると
    /// 後ろに別のコマンドを継ぎ足せてしまう。改行も同じ理由で落とす。
    /// 入力欄はユーザーのものだが、配られた手順書からの貼り付けもありうる。
    ///
    /// <b><c>&lt;</c> <c>&gt;</c> <c>&amp;</c> の類は落とさない。</b>
    /// netsh はスクリプトファイルを自分で読むのでコマンドプロンプトを経由せず、
    /// これらに特別な意味は無い。むしろ除外リストの <c>&lt;local&gt;</c> は
    /// <b>いちばんよく使う値</b>で、落とすと機能そのものが壊れる。
    /// </summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var text = new System.Text.StringBuilder(value.Length);

        foreach (char c in value)
        {
            if (c is '"' or '\r' or '\n') continue;

            text.Append(c);
        }

        return text.ToString().Trim();
    }

    /// <summary>画面に出す 1 行。読めなかったときも「読めない」と言う。</summary>
    public static string Describe(bool direct, string server, string bypass)
    {
        if (direct || string.IsNullOrWhiteSpace(server))
            return "プロキシなし（直接接続）";

        return string.IsNullOrWhiteSpace(bypass)
            ? $"固定: {server}"
            : $"固定: {server}（除外 {bypass}）";
    }
}
