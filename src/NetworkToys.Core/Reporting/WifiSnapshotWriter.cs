using System.Text;

namespace NetworkToys.Core.Reporting;

/// <summary>
/// 無線タブの「保存」で書き出すテキスト。現場の電波環境のスナップショットを
/// 単体で残す（レポート全体を作るまでもない、無線だけ控えたい場面用）。
/// 等幅で開く前提で桁を揃え、AP の表はレポートのテキスト版と同じ形にする。
/// </summary>
public static class WifiSnapshotWriter
{
    private const string Rule = "================================================================================";

    public static string Render(
        DateTime timestamp,
        IReadOnlyList<(string Label, string Value)>? connection,
        IReadOnlyList<WirelessAccessPoint> accessPoints)
    {
        var text = new StringBuilder();

        text.AppendLine($"無線 LAN の状況  ({timestamp:yyyy/MM/dd HH:mm:ss})");
        text.AppendLine(Rule);
        text.AppendLine();

        if (connection is { Count: > 0 })
        {
            foreach ((string label, string value) in connection)
                text.AppendLine($"  {TextWidth.Cell(label, 20)} {value}");

            text.AppendLine();
        }

        text.AppendLine($"周辺のアクセスポイント: {accessPoints.Count} 件 (* = 接続中)");

        if (accessPoints.Count > 0)
            TextReportWriter.WriteAccessPointTable(text, accessPoints, "");

        return text.ToString();
    }
}
