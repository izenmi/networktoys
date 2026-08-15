using System.IO;
using Microsoft.Win32;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Reporting;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// 記録画面。測定結果を持ち帰れる形にする。
///
/// HTML は 1 ファイル完結にしてあるので、そのままメールに添付できる。
/// 外部 CSS も JavaScript も参照しないため、客先のオフライン環境でも開ける。
/// </summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly MonitorViewModel _monitor;
    private readonly WifiViewModel _wifi;

    private string _title = "ネットワーク疎通確認";
    private string _note = string.Empty;
    private string _status = string.Empty;

    public ReportViewModel(MonitorViewModel monitor, WifiViewModel wifi)
    {
        _monitor = monitor;
        _wifi = wifi;

        SaveHtmlCommand = new RelayCommand(() => _ = SaveAsync(ReportFormat.Html), CanSave);
        SaveCsvCommand = new RelayCommand(() => _ = SaveAsync(ReportFormat.Csv), CanSave);
        SaveTextCommand = new RelayCommand(() => _ = SaveAsync(ReportFormat.Text), CanSave);
    }

    public RelayCommand SaveHtmlCommand { get; }
    public RelayCommand SaveCsvCommand { get; }

    /// <summary>
    /// テキストでの書き出し。報告書に貼る・チケットに残す用途では、
    /// HTML より扱いやすいことが多い。
    /// </summary>
    public RelayCommand SaveTextCommand { get; }

    /// <summary>レポートの見出し。現場名などを入れる。</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>実施者のメモ。作業内容や気づいたことを書いて残す。</summary>
    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private bool CanSave() => _monitor.Rows.Count > 0;

    private async Task SaveAsync(ReportFormat format)
    {
        (string extension, string filter) = format switch
        {
            ReportFormat.Html => ("html", "HTML レポート (*.html)|*.html|すべてのファイル (*.*)|*.*"),
            ReportFormat.Csv => ("csv", "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*"),
            _ => ("txt", "テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*"),
        };

        var dialog = new SaveFileDialog
        {
            FileName = ReportService.SuggestFileName(extension),
            DefaultExt = extension,
            Filter = filter,
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            Status = "レポートを作成しています…";

            // ipconfig /all は HTML とテキストに載せる（CSV は表なので馴染まない）
            string? ipConfig = null;
            if (format is ReportFormat.Html or ReportFormat.Text)
            {
                CommandCapture capture = await SystemInfoProbe.GetIpConfigAsync(CancellationToken.None);
                ipConfig = capture.Text;
            }

            ReportData data = ReportService.Build(
                Title,
                Note,
                _monitor.Rows,
                _monitor.NetworkInfo,
                _monitor.StartedAt,
                _monitor.IntervalMs,
                ipConfig,
                describeKind: _monitor.DescribeEffectiveKind,
                wireless: _wifi.DescribeForReport(),
                outages: _monitor.Tracker.Records,
                wirelessNote: _wifi.ReportNote,
                wirelessAccessPoints: _wifi.DescribeAccessPointsForReport());

            switch (format)
            {
                case ReportFormat.Html:
                    ReportService.SaveHtml(dialog.FileName, data);
                    break;
                case ReportFormat.Csv:
                    ReportService.SaveCsv(dialog.FileName, data);
                    break;
                default:
                    ReportService.SaveText(dialog.FileName, data);
                    break;
            }

            Status = $"{Path.GetFileName(dialog.FileName)} に書き出しました（{data.Rows.Count} 件）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"レポートの作成に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "ReportViewModel.Save");
        }
    }

    /// <summary>記録タブを開いたときに、保存できるかを見直す。</summary>
    public void OnActivated()
    {
        SaveHtmlCommand.RaiseCanExecuteChanged();
        SaveCsvCommand.RaiseCanExecuteChanged();
        SaveTextCommand.RaiseCanExecuteChanged();
    }

    /// <summary>入力を起動時の状態へ戻す。</summary>
    public void Reset()
    {
        Title = "ネットワーク疎通確認";
        Note = string.Empty;
        Status = string.Empty;
    }

}

/// <summary>書き出す形。</summary>
internal enum ReportFormat
{
    Html,
    Csv,
    Text,
}
