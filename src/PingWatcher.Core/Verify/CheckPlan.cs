using System.Globalization;
using System.Text;

namespace PingWatcher.Core.Verify;

/// <summary>試験の種類。</summary>
public enum CheckKind
{
    /// <summary>GET して応答を見る。<b>プロキシが効くのはこれだけ。</b></summary>
    Http,

    /// <summary>接続できるか（ファイル共有 445 など）。</summary>
    Tcp,

    /// <summary>接続してバナーが 220 で始まるか。</summary>
    Smtp,

    /// <summary>接続してバナーが * OK で始まるか。</summary>
    Imap,

    /// <summary>接続してバナーが +OK で始まるか。</summary>
    Pop3,

    /// <summary>名前を引けるか。<b>宛先が空なら自分のホスト名を引く。</b></summary>
    Dns,

    /// <summary>Teams 一式（名前解決・TCP 443・UDP の STUN）。</summary>
    Teams,

    /// <summary>指定した URL からダウンロードして速度を測る。プロキシが効く。</summary>
    Download,

    /// <summary>指定した URL へアップロードして速度を測る。プロキシが効く。</summary>
    Upload,

    /// <summary>fast.com で測る。<b>非公式なので先方の変更で壊れうる。</b></summary>
    FastCom,

    /// <summary>
    /// ブラウザで開いて、人が見て合否を付ける。
    ///
    /// <b>アプリからは判定できないページがある</b>。ログインが要る、証明書を選ぶ、
    /// JavaScript で描画される、といったものは HTTP で叩いても意味のある答えが返らない。
    /// そこは人が見るしかないので、<b>開くところまでを肩代わりして判定は人に委ねる</b>。
    /// </summary>
    Manual,
}

/// <summary>
/// 試験項目 1 件。
/// </summary>
/// <param name="Name">項目名。試験成績書にそのまま出る。</param>
/// <param name="Kind">種類。</param>
/// <param name="Target">
/// 宛先。<see cref="CheckKind.Http"/> は URL、接続系は <c>host:port</c>、
/// <see cref="CheckKind.Dns"/> は名前、<see cref="CheckKind.Teams"/> は空でよい（既定の宛先を使う）。
/// </param>
/// <param name="Expect">
/// 追加の期待。HTTP なら本文に含まれるべき文字列。空なら見ない。
/// </param>
public sealed record CheckItem(string Name, CheckKind Kind, string Target, string Expect = "")
{
    /// <summary>
    /// プロキシを変えて結果が変わりうるか。<b>HTTP でやり取りする種類が真。</b>
    ///
    /// 速度はプロキシがボトルネックかを見たい要なので当然入る。
    /// <b>Teams も入る</b> — 署名・チャット・在席は HTTPS なのでプロキシを通る。
    /// 直接出るのは音声・映像の UDP だけで、そちらは経路が別。
    /// </summary>
    public bool UsesProxy
        => Kind is CheckKind.Http or CheckKind.Download or CheckKind.Upload
                or CheckKind.FastCom or CheckKind.Teams;

    /// <summary>種類ごとの既定ポート。0 なら宛先の指定に従う。</summary>
    public static int DefaultPort(CheckKind kind) => kind switch
    {
        CheckKind.Smtp => 587,
        CheckKind.Imap => 993,
        CheckKind.Pop3 => 995,
        _ => 0,
    };
}

/// <summary>
/// 試験項目の一覧を、設定ファイルに置けるテキストと相互に変換する。
///
/// 画面は表、設定ファイルは 1 行 1 項目のテキスト、という分業は
/// 収集タブ（<see cref="Terminal.DeviceListParser"/>）と同じ。
/// 書式は <c>項目名,種類,宛先,期待</c> で、<b>区切りはカンマとタブ</b>。
/// 項目名に読点を書きたい場面があるので、<b>分割は前から 3 回だけ</b>にして
/// 4 つ目（期待）には残り全部を渡す。
/// </summary>
public static class CheckListParser
{
    private const char Separator = ',';

    /// <summary>行頭がこれらの行は注釈。宛先リストと同じ規則。</summary>
    private static bool IsComment(string line)
        => line.StartsWith('#') || line.StartsWith(';') || line.StartsWith('\'') || line.StartsWith('　');

    public static IReadOnlyList<CheckItem> Parse(string? text)
    {
        var items = new List<CheckItem>();
        if (string.IsNullOrWhiteSpace(text)) return items;

        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || IsComment(line)) continue;

            string[] parts = line.Replace('\t', Separator).Split(Separator, 4);
            if (parts.Length < 2) continue;

            if (!TryParseKind(parts[1], out CheckKind kind)) continue;

            items.Add(new CheckItem(
                Name: parts[0].Trim(),
                Kind: kind,
                Target: parts.Length > 2 ? parts[2].Trim() : "",
                Expect: parts.Length > 3 ? parts[3].Trim() : ""));
        }

        return items;
    }

    public static string Format(IEnumerable<CheckItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var text = new StringBuilder();

        foreach (CheckItem item in items)
        {
            // 空の項目は書き戻さない（画面で行だけ足して放置されたもの）
            if (item.Name.Length == 0 && item.Target.Length == 0) continue;

            text.Append(item.Name).Append(Separator)
                .Append(NameOf(item.Kind)).Append(Separator)
                .Append(item.Target);

            if (item.Expect.Length > 0)
                text.Append(Separator).Append(item.Expect);

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>画面のコンボに出す名前。設定ファイルにもこの綴りで書く。</summary>
    public static string NameOf(CheckKind kind) => kind switch
    {
        CheckKind.Http => "HTTP",
        CheckKind.Tcp => "TCP",
        CheckKind.Smtp => "SMTP",
        CheckKind.Imap => "IMAP",
        CheckKind.Pop3 => "POP3",
        CheckKind.Dns => "DNS",
        CheckKind.Download => "速度",
        CheckKind.Upload => "速度上り",
        CheckKind.FastCom => "fast.com",
        CheckKind.Manual => "手動",
        _ => "Teams",
    };

    /// <summary>大文字小文字は問わない（手で書き換える人がいる）。</summary>
    public static bool TryParseKind(string? text, out CheckKind kind)
    {
        kind = CheckKind.Http;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToUpperInvariant())
        {
            case "HTTP": case "HTTPS": kind = CheckKind.Http; return true;
            case "TCP": kind = CheckKind.Tcp; return true;
            case "SMTP": kind = CheckKind.Smtp; return true;
            case "IMAP": kind = CheckKind.Imap; return true;
            case "POP3": case "POP": kind = CheckKind.Pop3; return true;
            case "DNS": kind = CheckKind.Dns; return true;
            case "TEAMS": kind = CheckKind.Teams; return true;
            case "速度": case "SPEED": case "DOWNLOAD": kind = CheckKind.Download; return true;
            case "速度上り": case "UPLOAD": kind = CheckKind.Upload; return true;
            case "FAST.COM": case "FASTCOM": case "FAST": kind = CheckKind.FastCom; return true;
            case "手動": case "MANUAL": case "BROWSER": kind = CheckKind.Manual; return true;
            default: return false;
        }
    }

    /// <summary>
    /// 接続系の宛先から <c>host</c> と <c>port</c> を切り出す。
    ///
    /// <b>コロンが 1 つのときだけ切る</b>（素の IPv6 を壊さない）。宛先リストと同じ規則。
    /// ポートが無ければ種類ごとの既定を使い、それも無ければ 0 を返す。
    /// </summary>
    public static (string Host, int Port) SplitTarget(string target, CheckKind kind)
    {
        string text = (target ?? "").Trim();
        int fallback = CheckItem.DefaultPort(kind);

        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close > 1)
            {
                string host = text[1..close];
                string rest = text[(close + 1)..];

                return rest.StartsWith(':')
                    && int.TryParse(rest[1..], CultureInfo.InvariantCulture, out int bracketed)
                    && bracketed is > 0 and <= 65535
                        ? (host, bracketed)
                        : (host, fallback);
            }
        }

        int colon = text.LastIndexOf(':');
        if (colon <= 0 || text.IndexOf(':') != colon) return (text, fallback);

        return int.TryParse(text[(colon + 1)..], CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535
                ? (text[..colon], port)
                : (text, fallback);
    }
}
