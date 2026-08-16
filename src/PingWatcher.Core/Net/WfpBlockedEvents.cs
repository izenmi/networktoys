using System.Globalization;
using System.Net;
using PingWatcher.Core.Work;

namespace PingWatcher.Core.Net;

/// <summary>遮断された通信の向き。WFP の msFwpDirection をそのまま写す。</summary>
public enum WfpDirection
{
    Outbound = 0,
    Inbound = 1,
    Unknown = 2,
}

/// <summary>
/// WFP が落とした通信 1 件。
///
/// Windows 依存の型を持たない（App 側の P/Invoke がポインタから読み出して詰めるだけ）。
/// ここから先の整形・畳み込み・CSV 化はすべて純関数なのでそのまま検証できる。
/// </summary>
public sealed record WfpBlockedEvent(
    DateTime TimeUtc,
    WfpDirection Direction,
    byte Protocol,
    IPAddress? Local,
    ushort LocalPort,
    IPAddress? Remote,
    ushort RemotePort,
    uint ScopeId,
    string AppIdRaw,
    ulong FilterId,
    ushort LayerId,
    bool IsLoopback,
    uint DirectionRaw = 0);

/// <summary>
/// 一覧に出す行。同じ遮断が何度も起きるので、まとめた結果を持つ。
/// record（不変）なので <see cref="OrderedListSync"/> の同位置置換で更新できる。
/// </summary>
public sealed record WfpBlockedRow(
    string TimeText,
    DateTime LatestUtc,
    string DirectionText,
    string Protocol,
    string Local,
    string Remote,
    string ProcessName,
    string ProcessPath,
    int Count,
    string CountText,
    string FilterId,
    string LayerId,
    string SortKey,
    WfpDirection Direction = WfpDirection.Unknown)
{
    /// <summary>外へ出ようとして落ちた。画面の色分けに使う。</summary>
    public bool IsOutbound => Direction == WfpDirection.Outbound;

    /// <summary>外から来て落ちた。画面の色分けに使う。</summary>
    public bool IsInbound => Direction == WfpDirection.Inbound;
}

/// <summary>WFP のイベントを人が読める文字列にする。</summary>
public static class WfpFormat
{
    /// <summary>
    /// WFP の IPv4 アドレスは<b>ホストバイトオーダー</b>で入っている。
    ///
    /// iphlpapi の各種テーブル（接続一覧で使っている GetExtendedTcpTable など）は
    /// ネットワークバイトオーダーなので、あちらを写経すると 1.0.168.192 のように
    /// 逆さまに出る。混ざらないよう変換をこの 1 関数に閉じてある。
    /// </summary>
    public static IPAddress Ipv4FromHostOrder(uint address)
        => new(new[]
        {
            (byte)(address >> 24),
            (byte)(address >> 16),
            (byte)(address >> 8),
            (byte)address,
        });

    public static string Protocol(byte protocol) => protocol switch
    {
        1 => "ICMP",
        2 => "IGMP",
        6 => "TCP",
        17 => "UDP",
        47 => "GRE",
        50 => "ESP",
        51 => "AH",
        58 => "ICMPv6",
        // 知らない番号は言い換えずに番号のまま出す
        _ => protocol.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// 向きは記号と文字を併記する（色だけで状態を表さない決まり）。
    ///
    /// 知らない値のときは<b>生の数字を出す</b>。「—」で潰すと、
    /// 対応表が違うのか値が取れていないのかを切り分けられない
    /// （実機で「方向が出ない」と報告を受けた。原因の値をそのまま見せる）。
    /// </summary>
    public static string Direction(WfpDirection direction, uint raw = 0) => direction switch
    {
        WfpDirection.Outbound => "→ 送信",
        WfpDirection.Inbound => "← 受信",
        _ => raw == 0 ? "—" : $"? {raw}",
    };

    /// <summary>
    /// msFwpDirection を向きにする。
    ///
    /// WFP には向きの定数が 2 系統ある。<c>FWP_DIRECTION</c> の 0/1 と、
    /// フィルタ条件で使う <c>FWP_DIRECTION_IN/OUT</c>（0x3900 台）で、
    /// どちらが入るかは実機で確かめるほかない。両方を受ける。
    /// </summary>
    public static WfpDirection DirectionOf(uint msFwpDirection) => msFwpDirection switch
    {
        0 => WfpDirection.Outbound,      // FWP_DIRECTION_OUTBOUND
        1 => WfpDirection.Inbound,       // FWP_DIRECTION_INBOUND
        0x3900 => WfpDirection.Inbound,  // FWP_DIRECTION_IN
        0x3901 => WfpDirection.Outbound, // FWP_DIRECTION_OUT
        _ => WfpDirection.Unknown,
    };

    /// <summary>IPv6 は角括弧で囲む。スコープ付きは "[fe80::1%12]:443"。</summary>
    public static string Endpoint(IPAddress? address, ushort port, uint scopeId)
    {
        if (address is null) return "—";

        string text = address.ToString();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (scopeId != 0)
                text += "%" + scopeId.ToString(CultureInfo.InvariantCulture);

            text = "[" + text + "]";
        }

        return port == 0 ? text : text + ":" + port.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>WFP が返す NT パス（\device\harddiskvolume4\... 形式）の整形。</summary>
public static class NtPathText
{
    /// <summary>
    /// 印が立っているのに読み出せなかったパス。
    /// 「そもそも無い」（空文字）と区別できるようにしておく — 実機で切り分けるときに、
    /// この差が「こちらの読み方が違う」のか「機器が出していない」のかを分ける。
    /// </summary>
    public const string Unreadable = "\u0001unreadable";

    /// <summary>
    /// FWP_BYTE_BLOB の中身（終端 NUL 付きの UTF-16）を文字列にする。
    ///
    /// size がバイト数で終端を含む、という理解で読んでいるが、含まない場合でも
    /// 末尾の NUL を落とすだけなので同じ結果になる（奇数バイトも切り捨てで安全側）。
    /// </summary>
    public static string FromBlobBytes(byte[] bytes)
    {
        if (bytes.Length < 2) return "";

        // 文字数に切り下げる。奇数バイトなら最後の 1 バイトは捨てる
        string text = System.Text.Encoding.Unicode.GetString(bytes, 0, bytes.Length / 2 * 2);
        return text.TrimEnd('\0');
    }

    /// <summary>末尾のファイル名だけ取り出す。一覧の主表示はこれで足りる。</summary>
    public static string FileName(string ntPath)
    {
        if (ntPath.Length == 0) return "";

        int slash = ntPath.LastIndexOf('\\');
        if (slash < 0) return ntPath;

        string name = ntPath[(slash + 1)..];

        // 末尾が \ で終わっていたらファイル名が無い。元の文字列を返して情報を捨てない
        return name.Length > 0 ? name : ntPath;
    }
}

/// <summary>イベントの列を一覧の行に畳む。</summary>
public static class WfpEventView
{
    /// <summary>
    /// 同じ遮断（プロセス・宛先・ポート・プロトコル・フィルタ）をまとめて件数にする。
    ///
    /// WFP の遮断はまったく同じ組み合わせが数百件並ぶことが普通で、
    /// 生のまま並べると一覧として使えない。時刻は「最後に起きた時刻」を出す。
    ///
    /// 並びは決定的な全順序にする（<see cref="OrderedListSync"/> が前提にしている）。
    /// </summary>
    public static IReadOnlyList<WfpBlockedRow> Group(
        IEnumerable<WfpBlockedEvent> events,
        string filter = "",
        string sortColumn = "",
        bool descending = false)
    {
        Dictionary<string, List<WfpBlockedEvent>> groups = [];

        foreach (WfpBlockedEvent e in events)
        {
            string key = string.Join('|',
                e.AppIdRaw,
                (int)e.Direction,
                e.Protocol,
                e.Remote?.ToString() ?? "",
                e.RemotePort,
                e.FilterId);

            if (!groups.TryGetValue(key, out List<WfpBlockedEvent>? list))
                groups[key] = list = [];

            list.Add(e);
        }

        List<WfpBlockedRow> rows = [];

        foreach ((string key, List<WfpBlockedEvent> list) in groups)
        {
            WfpBlockedEvent latest = list.MaxBy(e => e.TimeUtc)!;
            string path = latest.AppIdRaw;

            rows.Add(new WfpBlockedRow(
                TimeText: latest.TimeUtc.ToLocalTime().ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
                LatestUtc: latest.TimeUtc,
                DirectionText: WfpFormat.Direction(latest.Direction, latest.DirectionRaw),
                Direction: latest.Direction,
                Protocol: WfpFormat.Protocol(latest.Protocol),
                Local: WfpFormat.Endpoint(latest.Local, latest.LocalPort, latest.ScopeId),
                Remote: WfpFormat.Endpoint(latest.Remote, latest.RemotePort, latest.ScopeId),
                ProcessName: path.Length == 0 ? "—"
                    : path == NtPathText.Unreadable ? "⚠ 読み取れず"
                    : NtPathText.FileName(path),
                ProcessPath: path == NtPathText.Unreadable ? "" : path,
                Count: list.Count,
                CountText: list.Count.ToString(CultureInfo.InvariantCulture),
                FilterId: latest.FilterId.ToString(CultureInfo.InvariantCulture),
                LayerId: latest.LayerId.ToString(CultureInfo.InvariantCulture),
                // 同一性キー。時刻や件数が変わっても同じ行として更新される
                SortKey: key));
        }

        if (filter.Length > 0)
        {
            rows = [.. rows.Where(r =>
                r.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.ProcessPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Local.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        return Sort(rows, sortColumn, descending);
    }

    /// <summary>
    /// 並べ替え。第 2 キーに SortKey を必ず置いて全順序にする
    /// （同値で順が揺れると差分同期が毎回全行を書き換えてしまう）。
    /// </summary>
    private static IReadOnlyList<WfpBlockedRow> Sort(
        List<WfpBlockedRow> rows, string sortColumn, bool descending)
    {
        Func<WfpBlockedRow, IComparable> key = sortColumn switch
        {
            "Process" => r => r.ProcessName,
            "Direction" => r => r.DirectionText,
            "Protocol" => r => r.Protocol,
            "Local" => r => r.Local,
            "Remote" => r => r.Remote,
            "Count" => r => r.Count,
            "Filter" => r => r.FilterId,
            // 時刻は表示文字列ではなく DateTime で比べる(年をまたぐと文字列順が狂う)
            _ => r => r.LatestUtc,
        };

        // 列を選んでいないときは新しい遮断を上にする
        bool newestFirst = sortColumn.Length == 0 || descending;

        IOrderedEnumerable<WfpBlockedRow> ordered = newestFirst
            ? rows.OrderByDescending(key)
            : rows.OrderBy(key);

        return [.. ordered.ThenBy(r => r.SortKey, StringComparer.Ordinal)];
    }

    public static CsvTable ToCsv(IReadOnlyList<WfpBlockedRow> rows) => new(
        ["時刻", "方向", "プロトコル", "送信元", "宛先", "プロセス", "件数", "フィルタ ID", "レイヤ ID", "パス"],
        [.. rows.Select(r => new[]
        {
            r.TimeText, r.DirectionText, r.Protocol, r.Local, r.Remote,
            r.ProcessName, r.CountText, r.FilterId, r.LayerId, r.ProcessPath,
        })]);
}
