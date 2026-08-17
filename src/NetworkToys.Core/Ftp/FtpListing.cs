using System.Globalization;
using NetworkToys.Core.Files;

namespace NetworkToys.Core.Ftp;

/// <summary>
/// FTP の一覧の解釈。<b>クライアント側の難所はここに集まっている。</b>
///
/// 通信そのものは短いが、一覧の書式は<b>サーバ任せ</b>で決まっていない。
/// 3 通りを順に試す:
///
/// <list type="number">
///   <item><b>MLSD</b>（RFC 3659）— 機械可読で年まで入る。あるならこれが最良</item>
///   <item><b>UNIX 形式</b> — <c>-rw-r--r-- 1 owner group 1234 Aug 15 09:30 name</c>。いちばん多い</item>
///   <item><b>DOS 形式</b> — <c>08-15-26  09:30AM  &lt;DIR&gt;  name</c>。IIS などが返す</item>
/// </list>
///
/// <b>読めない行は捨てる。例外にしない。</b>1 行読めないだけで一覧全体が
/// 出なくなる方が困る（機器の独自書式は珍しくない）。
///
/// 純粋関数なので CI で固められる。<see cref="FtpFormats.ListLine"/> が
/// 生成側なので、<b>生成 → 解釈の往復</b>もテストできる。
/// </summary>
public static class FtpListing
{
    /// <summary>
    /// LIST の 1 行を読む。UNIX 形式と DOS 形式のどちらも受ける。
    /// 読めなければ null。
    /// </summary>
    /// <param name="now">
    /// 「いま」。UNIX 形式は<b>年を持たない</b>ことがあり、そのときの推定に要る。
    /// テストで固定できるよう引数で受ける。
    /// </param>
    public static RemoteEntry? ParseListLine(string? line, DateTime now)
    {
        string text = (line ?? "").TrimEnd('\r', '\n');

        if (text.Trim().Length == 0) return null;

        // 「total 12」はファイルではなくブロック数の見出し
        if (text.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) return null;

        RemoteEntry? entry = ParseUnix(text, now) ?? ParseDos(text);

        // 「.」「..」はこちらで足すので、受け取った分は捨てる
        return entry is { IsDots: true } ? null : entry;
    }

    /// <summary>
    /// MLSD（RFC 3659）の 1 行を読む。<c>type=dir;size=0;modify=20260815093000; name</c>。
    /// 読めなければ null。
    /// </summary>
    public static RemoteEntry? ParseMachineLine(string? line)
    {
        string text = (line ?? "").TrimEnd('\r', '\n');

        // 名前は最初の半角空白のあと。名前自体に空白が入りうるので、ここで 1 度だけ割る
        int split = text.IndexOf(' ', StringComparison.Ordinal);
        if (split < 0) return null;

        string name = text[(split + 1)..];
        if (name.Length == 0) return null;

        bool directory = false;
        long size = 0;
        DateTime modified = DateTime.MinValue;

        foreach (string fact in text[..split].Split(';'))
        {
            int equals = fact.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) continue;

            string key = fact[..equals].Trim();
            string value = fact[(equals + 1)..].Trim();

            if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                // cdir / pdir は「いまの場所」と「親」。一覧には出さない
                if (value is "cdir" or "pdir") return null;

                directory = value.Equals("dir", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out size);
            }
            else if (key.Equals("modify", StringComparison.OrdinalIgnoreCase))
            {
                modified = ParseTimestamp(value) ?? DateTime.MinValue;
            }
        }

        var entry = new RemoteEntry(name, directory, directory ? 0 : size, modified);

        return entry.IsDots ? null : entry;
    }

    /// <summary>
    /// <c>yyyyMMddHHmmss</c>（MDTM / MLSD の modify）を読む。
    /// 小数の秒が付くことがあるので、先頭 14 桁だけ見る。
    /// </summary>
    public static DateTime? ParseTimestamp(string? value)
    {
        string text = (value ?? "").Trim();

        if (text.Length < 14) return null;

        return DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// <c>257 "/home/admin" is current directory</c> からパスを取り出す。
    /// <b>中の <c>""</c> は <c>"</c> に戻す</b>（<see cref="FtpFormats.QuotedPath"/> の逆）。
    /// 引用符が無ければ、コードの後ろを丸ごと返す。
    /// </summary>
    public static string UnquotePath(string? reply)
    {
        string text = (reply ?? "").Trim();

        int open = text.IndexOf('"', StringComparison.Ordinal);

        if (open < 0)
        {
            // 引用符を付けないサーバもある。3 桁コードだけ落として返す
            return text.Length > 4 && char.IsAsciiDigit(text[0]) ? text[4..].Trim() : text;
        }

        var built = new System.Text.StringBuilder(text.Length);

        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                built.Append(text[i]);
                continue;
            }

            // "" は 1 つの " を表す。単独の " は終わり
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                built.Append('"');
                i++;
                continue;
            }

            break;
        }

        return built.ToString();
    }

    /// <summary>
    /// <c>227 Entering Passive Mode (192,168,1,10,195,80).</c> から接続先を取り出す。
    /// <b>かっこの中は PORT の引数と同じ書式</b>なので、解釈は
    /// <see cref="FtpFormats.ParsePort"/> に任せる。
    /// </summary>
    public static (System.Net.IPAddress Address, int Port)? ParsePassiveReply(string? reply)
    {
        string text = reply ?? "";

        int open = text.IndexOf('(', StringComparison.Ordinal);
        int close = text.LastIndexOf(')');

        if (open < 0 || close <= open) return null;

        return FtpFormats.ParsePort(text[(open + 1)..close]);
    }

    // ===== UNIX 形式 =====

    /// <summary>
    /// <c>-rw-r--r--   1 owner group      1234 Aug 15 09:30 name</c>
    ///
    /// 後ろから数えない。<b>名前に空白が入る</b>ので、
    /// 日時の 3 つを見つけてから「その後ろ全部」を名前とする。
    /// </summary>
    private static RemoteEntry? ParseUnix(string line, DateTime now)
    {
        if (line.Length < 10) return null;

        char kind = line[0];

        // d=フォルダ / -=ファイル / l=シンボリックリンク。それ以外（b/c/s/p）は出さない
        if (kind is not ('d' or '-' or 'l')) return null;

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 最低でも 権限 + 何か + サイズ + 月 + 日 + 時刻/年 + 名前 の 7 つ。
        // グループを出さないサーバがあるので 9 を要求してはいけない
        if (parts.Length < 7) return null;

        // サイズの位置はサーバによって違う（グループを出さないものがある）。
        // 「月名の 3 つ前」ではなく、月名を探してそこから逆算する
        int month = -1;

        for (int i = 3; i < parts.Length - 2; i++)
        {
            if (MonthNumber(parts[i]) > 0) { month = i; break; }
        }

        if (month < 1) return null;

        if (!long.TryParse(parts[month - 1], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
            size = 0;

        DateTime modified = ParseUnixDate(parts[month], parts[month + 1], parts[month + 2], now);

        // 名前は「時刻/年」の次の空白の後ろ全部。ここまでの語を数えて位置を出す
        int nameAt = IndexAfterWords(line, month + 3);
        if (nameAt < 0 || nameAt >= line.Length) return null;

        string name = line[nameAt..].Trim();

        // シンボリックリンクは「name -> target」。矢印から先は落とす
        if (kind == 'l')
        {
            int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0) name = name[..arrow];
        }

        if (name.Length == 0) return null;

        bool directory = kind == 'd';

        return new RemoteEntry(name, directory, directory ? 0 : size, modified);
    }

    /// <summary>
    /// UNIX 形式の日時。<b>年が無いことがある</b>
    /// （半年より古いものは年、新しいものは時刻を出すのが ls の作法）。
    ///
    /// <b>時刻が出ている＝直近半年以内</b>ということなので、未来にはなりえない。
    /// 今年とみなして未来に飛んだら前年とする。1 日ぶんだけ余裕を持たせるのは、
    /// 相手の時計がこちらより少し進んでいることがあるため。
    /// </summary>
    private static DateTime ParseUnixDate(string month, string day, string third, DateTime now)
    {
        int monthNumber = MonthNumber(month);

        if (monthNumber == 0
            || !int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out int dayNumber))
        {
            return DateTime.MinValue;
        }

        // 「09:30」なら時刻、「2025」なら年
        int colon = third.IndexOf(':', StringComparison.Ordinal);

        if (colon < 0)
        {
            return int.TryParse(third, NumberStyles.None, CultureInfo.InvariantCulture, out int year)
                   && Valid(year, monthNumber, dayNumber)
                ? new DateTime(year, monthNumber, dayNumber, 0, 0, 0, DateTimeKind.Unspecified)
                : DateTime.MinValue;
        }

        if (!int.TryParse(third[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(third[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int minute)
            || hour > 23 || minute > 59
            || !Valid(now.Year, monthNumber, dayNumber))
        {
            return DateTime.MinValue;
        }

        var guess = new DateTime(now.Year, monthNumber, dayNumber, hour, minute, 0, DateTimeKind.Unspecified);

        // 未来に見えるなら去年のもの（時計のずれぶんだけ猶予を持たせる）
        return guess > now.AddDays(1) ? guess.AddYears(-1) : guess;
    }

    private static bool Valid(int year, int month, int day)
        => year is >= 1 and <= 9999 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    private static int MonthNumber(string text) => text switch
    {
        "Jan" => 1, "Feb" => 2, "Mar" => 3, "Apr" => 4, "May" => 5, "Jun" => 6,
        "Jul" => 7, "Aug" => 8, "Sep" => 9, "Oct" => 10, "Nov" => 11, "Dec" => 12,
        _ => 0,
    };

    /// <summary>先頭から <paramref name="words"/> 個の語を読み飛ばした位置。</summary>
    private static int IndexAfterWords(string line, int words)
    {
        int at = 0;

        for (int seen = 0; seen < words; seen++)
        {
            while (at < line.Length && line[at] == ' ') at++;
            if (at >= line.Length) return -1;

            while (at < line.Length && line[at] != ' ') at++;
        }

        while (at < line.Length && line[at] == ' ') at++;

        return at;
    }

    // ===== DOS 形式 =====

    /// <summary>
    /// <c>08-15-26  09:30AM       1234 name</c> / <c>08-15-26  09:30AM  &lt;DIR&gt;  name</c>
    ///
    /// IIS などが返す。年は 2 桁で、<b>70 以上は 1900 年代</b>とみなす
    /// （Windows の慣習に合わせる）。
    /// </summary>
    private static RemoteEntry? ParseDos(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 4) return null;

        string[] date = parts[0].Split('-', '/');

        if (date.Length != 3
            || !int.TryParse(date[0], NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            || !int.TryParse(date[1], NumberStyles.None, CultureInfo.InvariantCulture, out int day)
            || !int.TryParse(date[2], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
        {
            return null;
        }

        if (year < 100) year += year >= 70 ? 1900 : 2000;
        if (month is < 1 or > 12 || !Valid(year, month, day)) return null;

        DateTime modified = DateTime.TryParseExact(
            parts[1], ["hh:mmtt", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)
            ? new DateTime(year, month, day, time.Hour, time.Minute, 0, DateTimeKind.Unspecified)
            : new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);

        bool directory = parts[2].Equals("<DIR>", StringComparison.OrdinalIgnoreCase);

        long size = 0;
        if (!directory && !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out size))
            return null;

        int nameAt = IndexAfterWords(line, 3);
        if (nameAt < 0 || nameAt >= line.Length) return null;

        string name = line[nameAt..].Trim();

        return name.Length == 0 ? null : new RemoteEntry(name, directory, size, modified);
    }
}
