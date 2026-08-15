using System.IO;
using Microsoft.Win32;
using PastelNet.App.Mvvm;
using PastelNet.App.Services;
using PastelNet.Core.Reporting;

namespace PastelNet.App.ViewModels;

/// <summary>
/// 記録画面。測定結果を持ち帰れる形にする。
///
/// HTML は 1 ファイル完結にしてあるので、そのままメールに添付できる。
/// 外部 CSS も JavaScript も参照しないため、客先のオフライン環境でも開ける。
/// </summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly MonitorViewModel _monitor;

    private string _title = "ネットワーク疎通確認";
    private string _note = string.Empty;
    private string _status = string.Empty;

    public ReportViewModel(MonitorViewModel monitor)
    {
        _monitor = monitor;

        SaveHtmlCommand = new RelayCommand(() => _ = SaveAsync(html: true), CanSave);
        SaveCsvCommand = new RelayCommand(() => _ = SaveAsync(html: false), CanSave);
    }

    public RelayCommand SaveHtmlCommand { get; }
    public RelayCommand SaveCsvCommand { get; }

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

    private async Task SaveAsync(bool html)
    {
        string extension = html ? "html" : "csv";

        var dialog = new SaveFileDialog
        {
            FileName = ReportService.SuggestFileName(extension),
            DefaultExt = extension,
            Filter = html
                ? "HTML レポート (*.html)|*.html|すべてのファイル (*.*)|*.*"
                : "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            Status = "レポートを作成しています…";

            // ipconfig /all は HTML にだけ載せる（CSV は表なので馴染まない）
            string? ipConfig = null;
            if (html)
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
                ipConfig);

            if (html)
                ReportService.SaveHtml(dialog.FileName, data);
            else
                ReportService.SaveCsv(dialog.FileName, data);

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
    }
}
