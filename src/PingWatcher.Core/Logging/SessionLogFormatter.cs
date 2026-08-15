using System.Globalization;
using PingWatcher.Core.Models;
using PingWatcher.Core.Work;

namespace PingWatcher.Core.Logging;

/// <summary>
/// 測定ログ（logs フォルダに自動追記されるテキスト）の各行を整形する。
///
/// 1 サンプル 1 行のタブ区切り。メモ帳で読め、Excel にそのまま貼れて、
/// grep もできる形を最優先にしている（EXPing のログと同じ発想）。
/// 時刻は引数で受け取る — Core は自分では時計を持たない。
/// 書式は必ず InvariantCulture（カンマ小数点のカルチャで列が壊れないように）。
/// </summary>
public static class SessionLogFormatter
{
    /// <summary>ファイル冒頭。複数行を返すが改行は含まない（書き込み側が CRLF を付ける）。</summary>
    public static IReadOnlyList<string> Header(DateTime startedAt, int intervalMs, int targetCount, string method)
    {
        return
        [
            "# PingWatcher 測定ログ",
            string.Create(CultureInfo.InvariantCulture,
                $"# 開始: {startedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)}  方式: {method}  間隔: {intervalMs} ms  宛先: {targetCount} 件"),
            "時刻\t宛先\tIP\t状態\tRTT(ms)",
        ];
    }

    /// <summary>測定結果 1 件分の行。</summary>
    public static string Sample(DateTime at, string host, string? address, in ProbeSample sample)
    {
        // 応答（成功・拒否）だけ RTT を持つ。拒否も RST が返るまでの実測値
        string rtt = sample.Status is ProbeStatus.Success or ProbeStatus.Refused
            ? sample.RttMs.ToString("0.0", CultureInfo.InvariantCulture)
            : string.Empty;

        // OK だけは短く。異常は日本語の状態名（レポートと表記を揃える）
        string status = sample.Status == ProbeStatus.Success ? "OK" : sample.Status.Describe();

        return $"{Time(at)}\t{host}\t{address}\t{status}\t{rtt}";
    }

    /// <summary>不通が開いたときのイベント行。目で流し読みしても拾えるよう *** を付ける。</summary>
    public static string OutageOpened(DateTime at, OutageRecord record)
        => $"{Time(at)}\t*** 不通 {record.Host} — 2回連続で応答がありません";

    /// <summary>不通が閉じたときのイベント行。閉じた理由で文言を変える。</summary>
    public static string OutageClosed(DateTime at, OutageRecord record)
    {
        string body = record.CloseReason switch
        {
            OutageCloseReason.Recovered => $"*** 復旧 {record.Host} — 不通は {record.DurationText}",
            OutageCloseReason.Removed => $"*** 打ち切り {record.Host} — 宛先が一覧から外れました（不通は {record.DurationText}）",
            _ => $"*** 打ち切り {record.Host} — 測定を停止しました（不通は {record.DurationText}）",
        };

        return $"{Time(at)}\t{body}";
    }

    /// <summary>ファイル末尾。</summary>
    public static string Footer(DateTime stoppedAt)
        => string.Create(CultureInfo.InvariantCulture,
            $"# 停止: {stoppedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)}");

    private static string Time(DateTime at) => at.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
}
