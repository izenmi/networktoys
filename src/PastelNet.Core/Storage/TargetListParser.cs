using System.Net;
using System.Text;
using PastelNet.Core.Addressing;
using PastelNet.Core.Models;

namespace PastelNet.Core.Storage;

/// <param name="LineNumber">1 始まりの行番号。</param>
/// <param name="Line">問題のあった行の内容。</param>
/// <param name="Message">理由。</param>
public sealed record TargetListError(int LineNumber, string Line, string Message);

public sealed class TargetListParseResult
{
    public List<Target> Targets { get; } = [];

    public List<TargetListError> Errors { get; } = [];

    /// <summary>注釈行として読み飛ばした行数。</summary>
    public int CommentLines { get; internal set; }

    /// <summary>範囲記法から展開されたアドレスの件数。</summary>
    public int ExpandedCount { get; internal set; }

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 宛先リストのテキストを解釈する。書式は EXPing に合わせている。
///
/// 公式ドキュメント(ExPing.txt)より:
///   「１アドレスで１行を使用します。各アドレスの後に半角スペースを挿入することにより、
///     それ以降行末まで備考となります」
///   「（注釈行と見なされる行頭文字） [ # ] [ ; ] [ ' ] および全角スペース」
///   「行の途中から注釈を設定することはできません」
///
/// これに加えて、まとめて登録しやすいよう次を独自に受け付ける:
///   ・区切りにタブも使える（表計算から貼り付けたとき用）
///   ・範囲記法 192.168.1.1-20 / 192.168.1.1-192.168.1.20 / 192.168.1.0/24 を展開する
///   ・末尾に :ポート を付けると ICMP ではなく TCP 接続で測る（例 example.jp:443）
/// </summary>
public static class TargetListParser
{
    /// <summary>EXPing と同じ注釈行の行頭文字。全角スペース(U+3000)を含む。</summary>
    private static readonly char[] CommentHeads = ['#', ';', '\'', '　'];

    public static TargetListParseResult Parse(string? text, int limit = IpRangeParser.DefaultLimit)
    {
        var result = new TargetListParseResult();
        if (string.IsNullOrEmpty(text))
            return result;

        int lineNumber = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
                continue;

            // 行頭文字の判定は空白を落とす前に行う（全角スペース始まりが注釈のため）
            if (Array.IndexOf(CommentHeads, line[0]) >= 0)
            {
                result.CommentLines++;
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            // 最初の半角スペース（またはタブ）以降が備考
            int separator = trimmed.IndexOfAny([' ', '\t']);
            string host = separator < 0 ? trimmed : trimmed[..separator];
            string comment = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();

            // 末尾の :ポート があれば TCP 接続で測る
            ProbeKind probeKind = ProbeKind.Icmp;
            int port = 0;
            if (TrySplitPort(host, out string hostWithoutPort, out int parsedPort))
            {
                probeKind = ProbeKind.Tcp;
                port = parsedPort;
                host = hostWithoutPort;
            }

            if (result.Targets.Count >= limit)
            {
                result.Errors.Add(new TargetListError(lineNumber, line, $"宛先が上限 {limit:N0} 件に達したため、これ以降を読み飛ばしました。"));
                break;
            }

            IpRangeKind kind = IpRangeParser.TryExpand(host, limit - result.Targets.Count, out List<IPAddress> expanded, out string? error);

            switch (kind)
            {
                case IpRangeKind.Invalid:
                    result.Errors.Add(new TargetListError(lineNumber, line, error ?? "解釈できませんでした。"));
                    break;

                case IpRangeKind.Expanded:
                    foreach (IPAddress address in expanded)
                        result.Targets.Add(new Target { Host = address.ToString(), Comment = comment, Kind = probeKind, Port = port });
                    result.ExpandedCount += expanded.Count;
                    break;

                default:
                    result.Targets.Add(new Target { Host = host, Comment = comment, Kind = probeKind, Port = port });
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 末尾の <c>:ポート</c> を切り出す。TCP 接続で測る宛先の指定。
    /// コロンが 2 つ以上ある場合（IPv6 リテラル）は対象にしない。
    /// </summary>
    private static bool TrySplitPort(string host, out string hostWithoutPort, out int port)
    {
        hostWithoutPort = host;
        port = 0;

        int colon = host.LastIndexOf(':');
        if (colon <= 0 || colon == host.Length - 1)
            return false;

        if (host.IndexOf(':') != colon)
            return false;   // IPv6 リテラルなど

        if (!int.TryParse(host[(colon + 1)..], out int parsed) || parsed is < 1 or > 65535)
            return false;

        hostWithoutPort = host[..colon];
        port = parsed;
        return true;
    }

    /// <summary>宛先リストを編集用のテキストに戻す。<see cref="Parse"/> で読み直せる形にする。</summary>
    public static string Format(IEnumerable<Target> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var builder = new StringBuilder();

        foreach (Target target in targets)
        {
            builder.Append(target.Host);

            if (target.Kind == ProbeKind.Tcp && target.Port > 0)
            {
                builder.Append(':');
                builder.Append(target.Port);
            }

            if (!string.IsNullOrWhiteSpace(target.Comment))
            {
                builder.Append(' ');
                builder.Append(target.Comment);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
