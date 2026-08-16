using System.IO;
using System.Text;
using Microsoft.Win32;
using PingWatcher.Core.Terminal;
using PingWatcher.Core.Work;

namespace PingWatcher.App.Services;

/// <summary>
/// 一覧を CSV に書き出す。画面ごとに同じ 20 行を写していたのを 1 か所にまとめたもの。
///
/// <b>BOM 付き UTF-8 で書く。</b>無いと日本語版 Excel がそのまま開いたときに文字化けする。
/// 行数は <see cref="CsvTable.Rows"/> から数えるので、呼ぶ側は渡すだけでよい。
/// </summary>
internal static class CsvExport
{
    /// <param name="namePrefix">既定のファイル名の頭（"scan" なら scan-20260816-1030.csv）。</param>
    /// <returns>
    /// 画面の状態欄に出す文字列。<b>取り消されたら null</b>（このときは状態を書き換えない）。
    /// </returns>
    public static string? Save(string namePrefix, CsvTable table)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{DeviceReport.Sanitize(namePrefix)}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = "csv",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return null;

        try
        {
            File.WriteAllText(dialog.FileName, table.ToCsv(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return $"{Path.GetFileName(dialog.FileName)} に保存しました（{table.Rows.Count} 件）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"保存できませんでした: {ex.Message}";
        }
    }
}
