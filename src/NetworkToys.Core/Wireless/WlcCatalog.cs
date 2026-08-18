using System.Globalization;
using NetworkToys.Core.Design;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Wireless;

/// <summary>無線につながっている端末の 1 行。</summary>
public sealed record WlcClientRow(
    string Mac,
    string Ip,
    string Vendor,
    string ApName,
    string Ssid,
    string Radio,
    int Rssi,
    string RssiText,
    string Quality,
    string Snr,
    string Speed,
    string State,
    SeverityKind StateKind,
    string AssociatedAt);

/// <summary>AP の 1 行。<b>繋がっていない AP も同じ表に混ぜる</b>（別の表にしない）。</summary>
public sealed record WlcApRow(
    string Name,
    string State,
    SeverityKind StateKind,
    bool IsJoined,
    string Ip,
    string Mac,
    string Model,
    string Version,
    string Radios,
    int Clients,
    string ClientsText,
    string Tags);

/// <summary>AP の参加・切断の 1 行。WLC が持っている最終値だけ。</summary>
public sealed record WlcJoinRow(
    string Name,
    string Mac,
    string State,
    SeverityKind StateKind,
    string LastJoin,
    string LastDisconnect,
    string Reason,
    string Joins,
    string Failures);

/// <summary>電波の混み具合の 1 行（AP の無線 1 本ぶん）。</summary>
public sealed record WlcRrmRow(
    string ApName,
    string Radio,
    string Channel,
    string Power,
    int Utilization,
    string UtilizationText,
    SeverityKind UtilizationKind,
    string Noise,
    int Clients,
    string ClientsText);

/// <summary>不正 AP・隣接 AP の 1 行。</summary>
public sealed record WlcRogueRow(
    string Kind,
    string Bssid,
    string Vendor,
    string Ssid,
    string Channel,
    int Rssi,
    string RssiText,
    string DetectedBy,
    string LastHeard,
    string Note);

/// <summary>SSID(WLAN) ごとの内訳の 1 行。</summary>
public sealed record WlcSsidRow(
    string Ssid,
    string Profile,
    string Id,
    string State,
    SeverityKind StateKind,
    int Clients,
    int Band24,
    int Band5,
    int Band6);

/// <summary>
/// WLC の画面が使う小物。
///
/// <b>取得は SSH だけ</b>（2026-08-18 ユーザー指示で RESTCONF を畳んだ）。
/// <c>show</c> の解釈は <see cref="WlcShow"/> にあり、ここに残っているのは
/// 行の型・一覧の絞り込み・CSV の書き出しだけ。
/// </summary>
public static class WlcCatalog
{
    /// <summary>
    /// 入力が IP でも MAC でも名前でも、同じ欄で探せるようにする。
    /// MAC は <c>aabb.ccdd.eeff</c> / <c>AA-BB-…</c> / <c>aa:bb:…</c> のどれで打たれても当てる。
    /// </summary>
    public static IReadOnlyList<WlcClientRow> FilterClients(IEnumerable<WlcClientRow> rows, string? query)
    {
        string text = (query ?? "").Trim();

        if (text.Length == 0) return [.. rows];

        string mac = NormalizeMac(text);
        bool looksLikeMac = mac.Length >= 4 && mac.All(Uri.IsHexDigit);

        return
        [
            .. rows.Where(r =>
                (looksLikeMac && NormalizeMac(r.Mac).Contains(mac, StringComparison.Ordinal))
                || r.Ip.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Mac.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.ApName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Ssid.Contains(text, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>比べるための MAC。<b>表示は機器が返した形のまま</b>で、比較にだけこれを使う。</summary>
    public static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";

        var text = new System.Text.StringBuilder(mac.Length);

        foreach (char c in mac)
        {
            if (c is ':' or '-' or '.' or ' ') continue;

            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    // ===== CSV / Excel =====

    public static CsvTable ToCsv(IReadOnlyList<WlcClientRow> rows) => new(
        ["MAC", "IP", "メーカー", "AP", "SSID", "電波", "RSSI", "品質", "SNR", "速度", "状態", "接続時刻"],
        [.. rows.Select(r => new[]
        {
            r.Mac, r.Ip, r.Vendor, r.ApName, r.Ssid, r.Radio, r.RssiText, r.Quality,
            r.Snr, r.Speed, r.State, r.AssociatedAt,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcApRow> rows) => new(
        ["状態", "AP", "IP", "MAC", "型番", "版", "無線", "台数", "タグ"],
        [.. rows.Select(r => new[]
        {
            r.State, r.Name, r.Ip, r.Mac, r.Model, r.Version, r.Radios, r.ClientsText, r.Tags,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcJoinRow> rows) => new(
        ["状態", "AP", "MAC", "最終参加", "最終切断", "切断理由", "参加", "失敗"],
        [.. rows.Select(r => new[]
        {
            r.State, r.Name, r.Mac, r.LastJoin, r.LastDisconnect, r.Reason, r.Joins, r.Failures,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcRrmRow> rows) => new(
        ["AP", "無線", "チャンネル", "出力", "使用率", "雑音", "台数"],
        [.. rows.Select(r => new[]
        {
            r.ApName, r.Radio, r.Channel, r.Power, r.UtilizationText, r.Noise, r.ClientsText,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcRogueRow> rows) => new(
        ["種別", "BSSID", "メーカー", "SSID", "チャンネル", "電波強度", "検知した AP", "最終受信", "備考"],
        [.. rows.Select(r => new[]
        {
            r.Kind, r.Bssid, r.Vendor, r.Ssid, r.Channel, r.RssiText, r.DetectedBy, r.LastHeard, r.Note,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcSsidRow> rows) => new(
        ["SSID", "プロファイル", "ID", "状態", "台数", "2.4GHz", "5GHz", "6GHz"],
        [.. rows.Select(r => new[]
        {
            r.Ssid, r.Profile, r.Id, r.State,
            r.Clients.ToString(CultureInfo.InvariantCulture),
            r.Band24.ToString(CultureInfo.InvariantCulture),
            r.Band5.ToString(CultureInfo.InvariantCulture),
            r.Band6.ToString(CultureInfo.InvariantCulture),
        })]);
}
