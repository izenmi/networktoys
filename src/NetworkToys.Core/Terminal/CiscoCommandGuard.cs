namespace NetworkToys.Core.Terminal;

public enum CommandRisk
{
    /// <summary>そのまま実行してよい。</summary>
    Allowed,

    /// <summary>実行はするが、見せる前に一言添える。</summary>
    Warned,

    /// <summary>実行しない。</summary>
    Blocked,
}

public readonly record struct CommandVerdict(CommandRisk Risk, string? Reason);

/// <summary>
/// 収集で流してよいコマンドかを見る。
///
/// <b>危ないものは実行前に弾く。「警告だけ出して投げる」は選べない。</b>
/// <c>reload</c> は <c>Proceed with reload? [confirm]</c>、<c>copy run start</c> は
/// <c>Destination filename [...]?</c> と<b>対話プロンプトを返す</b>が、収集の状態機械は
/// それに答えられない（答えてはいけない）。結果は「タイムアウトして中断」で、
/// 使う人には「なぜか動かない」としか見えない。先に弾いて理由を出す方が親切。
///
/// <b>上書きの手段は作らない。</b>作った瞬間に弾く意味が消える。
/// 設定を変えたい人には「Ping の行を右クリック → Tera Term で接続」がある。
/// </summary>
public static class CiscoCommandGuard
{
    /// <summary>
    /// 弾く動詞。Cisco は「一意に定まる最短形」まで省略できるので、
    /// 動詞ごとに<b>省略の最短長</b>を決めて前方一致で見る。
    /// </summary>
    private static readonly (string Verb, int Shortest, string Reason)[] Blocked =
    [
        ("reload", 3, "再起動します。"),
        ("configure", 4, "設定モードに入ります。"),
        ("write", 2, "設定を書き込みます。"),
        ("copy", 3, "設定やファイルを書き込みます。"),
        ("erase", 3, "消去します。"),
        ("format", 4, "初期化します。"),
        ("delete", 3, "削除します。"),
        ("squeeze", 3, "フラッシュを整理します。"),
        ("rename", 3, "名前を変えます。"),
        ("clear", 2, "カウンタやログを消します（調べたい情報そのものが消えます）。"),
        ("debug", 3, "出力が止まらなくなり、収集が終わらなくなります。"),
        ("undebug", 5, "debug の解除は収集の対象外です。"),
        ("setup", 5, "対話設定に入ります。"),
        ("archive", 4, "設定の保存操作です。"),
        ("verify", 4, "時間がかかり、対話を求められることがあります。"),
        ("test", 4, "機器の動作を変えることがあります。"),
        ("send", 4, "他の端末へ文字を送ります。"),
        ("disconnect", 4, "セッションを切ります。"),
        ("logout", 4, "セッションを切ります。"),
        ("exit", 4, "セッションを切ります。"),
        ("end", 3, "収集では使いません。"),
    ];

    /// <summary>そのまま通す動詞（読むだけのもの）。</summary>
    private static readonly (string Verb, int Shortest)[] Allowed =
    [
        ("show", 2),
        ("dir", 3),
        ("more", 4),
        ("terminal", 4),
        ("who", 3),
        ("where", 5),
    ];

    public static CommandVerdict Classify(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0)
            return new CommandVerdict(CommandRisk.Blocked, "空の行です。");

        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0].ToLowerInvariant();

        // ping / traceroute は引数が無いと対話モード（Protocol [ip]: …）に入る。
        // 引数付きなら 1 回で終わるので通す
        if (Matches(verb, "ping", 2) || Matches(verb, "traceroute", 4))
        {
            return parts.Length >= 2
                ? new CommandVerdict(CommandRisk.Allowed, null)
                : new CommandVerdict(CommandRisk.Blocked, "宛先が要ります（引数なしは対話モードに入ります）。");
        }

        foreach ((string blocked, int shortest, string reason) in Blocked)
        {
            if (Matches(verb, blocked, shortest))
                return new CommandVerdict(CommandRisk.Blocked, reason);
        }

        foreach ((string allowed, int shortest) in Allowed)
        {
            if (Matches(verb, allowed, shortest))
                return new CommandVerdict(CommandRisk.Allowed, null);
        }

        return new CommandVerdict(CommandRisk.Warned, "読み取り専用か確かめられませんでした。");
    }

    /// <summary>
    /// Cisco の省略形に一致するか。<paramref name="shortest"/> 文字以上で、
    /// かつ正式名の前方一致であること。
    /// </summary>
    private static bool Matches(string verb, string full, int shortest)
        => verb.Length >= shortest
           && verb.Length <= full.Length
           && full.StartsWith(verb, StringComparison.Ordinal);
}
