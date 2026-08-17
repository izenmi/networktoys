using System.Text.RegularExpressions;

namespace NetworkToys.Core.Terminal;

/// <summary>特権の段階。</summary>
public enum PromptLevel
{
    User,      // hostname&gt;
    Enable,    // hostname#
    Config,    // hostname(config)#
}

public readonly record struct PromptMatch(string Hostname, PromptLevel Level, string Text);

/// <summary>
/// Cisco の対話プロンプトを見分ける。
///
/// <b>誤判定するとハングする（あるいは出力を取りこぼす）ので、ここが最重要。</b>
/// プロンプトを推測せず<b>ログイン直後に学習</b>し、以降は次の 3 つを同時に
/// 満たしたときだけプロンプトとみなす:
///
/// <list type="number">
/// <item>バッファの<b>末尾</b>にある（＝そのあとに改行が無い）</item>
/// <item>学習したホスト名と前置が一致する</item>
/// <item>末尾が <c>&gt;</c> / <c>#</c> / <c>)#</c></item>
/// </list>
///
/// 「末尾に改行が無い」が効くのは、機器の出力行は必ず改行で終わるのに対し、
/// プロンプトだけは改行せずに止まるため。ここにホスト名一致を重ねると、
/// 設定中の <c>hostname R1</c> のような行に引っかかる余地がほぼ消える。
/// </summary>
public static partial class CiscoPrompt
{
    [GeneratedRegex(@"^([A-Za-z0-9._\-]{1,63})(\([A-Za-z0-9._\-]{1,32}\))?\s*([>#])$")]
    private static partial Regex PromptLine();

    /// <summary>バッファ末尾がプロンプトなら取り出す。</summary>
    /// <param name="expectedHost">学習済みのホスト名。null なら形だけで判定する（信頼度が下がる）。</param>
    public static bool TryMatchAtEnd(string buffer, string? expectedHost, out PromptMatch match)
    {
        match = default;
        if (buffer.Length == 0) return false;

        // 末尾に改行があるなら、それは出力行であってプロンプトではない
        if (buffer[^1] is '\n' or '\r') return false;

        string last = LastLine(buffer);
        if (last.Length == 0) return false;

        Match m = PromptLine().Match(last.TrimEnd());
        if (!m.Success) return false;

        string hostname = m.Groups[1].Value;

        if (expectedHost is { Length: > 0 }
            && !string.Equals(hostname, expectedHost, StringComparison.OrdinalIgnoreCase))
            return false;

        bool inConfig = m.Groups[2].Success;
        char tail = m.Groups[3].Value[0];

        PromptLevel level = inConfig ? PromptLevel.Config
            : tail == '#' ? PromptLevel.Enable
            : PromptLevel.User;

        match = new PromptMatch(hostname, level, last.TrimEnd());
        return true;
    }

    /// <summary>バッファの末尾からホスト名を学習する。取れなければ null。</summary>
    public static string? LearnHostname(string buffer)
        => TryMatchAtEnd(buffer, null, out PromptMatch match) ? match.Hostname : null;

    public static bool EndsWithMore(string buffer)
    {
        string tail = buffer.TrimEnd();
        return tail.EndsWith("--More--", StringComparison.Ordinal)
               || tail.EndsWith("<--- More --->", StringComparison.Ordinal);
    }

    public static bool EndsWithPasswordPrompt(string buffer)
        => EndsWithAny(buffer, ["password:", "passcode:"]);

    public static bool EndsWithUsernamePrompt(string buffer)
        => EndsWithAny(buffer, ["username:", "login:", "user name:"]);

    /// <summary>
    /// 「はい/いいえ」を求められている状態か。
    /// <b>ここを検出したら何も送らずに離脱する</b>（答えなければ実行されない）。
    /// </summary>
    public static bool EndsWithConfirmPrompt(string buffer)
    {
        string tail = buffer.TrimEnd();
        if (tail.Length == 0) return false;

        string lower = tail.ToLowerInvariant();

        return lower.EndsWith("[confirm]", StringComparison.Ordinal)
               || lower.EndsWith("[yes/no]:", StringComparison.Ordinal)
               || lower.EndsWith("[y/n]:", StringComparison.Ordinal)
               || lower.EndsWith("(y/n)?", StringComparison.Ordinal)
               || lower.EndsWith("[yes]:", StringComparison.Ordinal)
               // "Destination filename [startup-config]?" のような ]? 終わり
               || (lower.EndsWith("]?", StringComparison.Ordinal) && lower.Contains('[', StringComparison.Ordinal))
               || lower.Contains("are you sure", StringComparison.Ordinal);
    }

    /// <summary>認証に失敗した文言なら日本語の理由を返す。該当しなければ null。</summary>
    public static string? DetectAuthFailure(string buffer)
    {
        string lower = buffer.ToLowerInvariant();

        if (lower.Contains("% login invalid", StringComparison.Ordinal)
            || lower.Contains("% bad passwords", StringComparison.Ordinal)
            || lower.Contains("authentication failed", StringComparison.Ordinal)
            || lower.Contains("% authorization failed", StringComparison.Ordinal))
            return "ユーザー名かパスワードが違います。";

        if (lower.Contains("% access denied", StringComparison.Ordinal))
            return "アクセスを拒否されました。";

        if (lower.Contains("% error in authentication", StringComparison.Ordinal))
            return "認証サーバが応答しませんでした。";

        return null;
    }

    /// <summary>コマンドが通らなかったことを示す出力なら日本語の理由を返す。</summary>
    public static string? DetectCommandProblem(string output)
    {
        if (output.Contains("% Invalid input", StringComparison.OrdinalIgnoreCase))
            return "この機器では使えないコマンドです。";

        if (output.Contains("% Incomplete command", StringComparison.OrdinalIgnoreCase))
            return "コマンドが途中です。";

        if (output.Contains("% Ambiguous command", StringComparison.OrdinalIgnoreCase))
            return "コマンドの省略が曖昧です。";

        if (output.Contains("Command authorization failed", StringComparison.OrdinalIgnoreCase))
            return "このアカウントでは実行を許可されていません。";

        if (output.Contains("% Permission denied", StringComparison.OrdinalIgnoreCase))
            return "権限が足りません（特権モードが要るかもしれません）。";

        return null;
    }

    private static bool EndsWithAny(string buffer, string[] suffixes)
    {
        string tail = buffer.TrimEnd().ToLowerInvariant();
        if (tail.Length == 0) return false;

        foreach (string suffix in suffixes)
        {
            if (tail.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>最後の行（改行を含まない末尾部分）。ANSI は事前に落としてある前提。</summary>
    private static string LastLine(string buffer)
    {
        int start = buffer.LastIndexOfAny(['\n', '\r']);
        return start < 0 ? buffer : buffer[(start + 1)..];
    }
}
