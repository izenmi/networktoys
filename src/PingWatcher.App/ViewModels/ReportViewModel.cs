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
    private readonly MonitorViewModel _tcp;
    private readonly WifiViewModel _wifi;
    private readonly VerifyViewModel _verify;

    private string _status = string.Empty;
    private string _preview = string.Empty;

    /// <param name="tcp">TCP 画面。宛先も結果も Ping とは別に持つので、別に受け取る。</param>
    /// <param name="verify">
    /// 試験画面。<b>測ったことと試したことは 1 つの証跡</b>なので、同じ記録に載せる。
    /// </param>
    public ReportViewModel(
        MonitorViewModel monitor, MonitorViewModel tcp, WifiViewModel wifi, VerifyViewModel verify)
    {
        _monitor = monitor;
        _tcp = tcp;
        _wifi = wifi;
        _verify = verify;

        SaveHtmlCommand = new RelayCommand(() => _ = SaveAsync(ReportFormat.Html), CanSave);
        SaveCsvCommand = new RelayCommand(() => _ = SaveAsync(ReportFormat.Csv), CanSave);
        SaveTextCommand = new RelayCommand(() => _ = SaveAsync(ReportFormat.Text), CanSave);
        RefreshPreviewCommand = new RelayCommand(RefreshPreview);
    }

    public RelayCommand SaveHtmlCommand { get; }
    public RelayCommand SaveCsvCommand { get; }

    /// <summary>
    /// テキストでの書き出し。報告書に貼る・チケットに残す用途では、
    /// HTML より扱いやすいことが多い。
    /// </summary>
    public RelayCommand SaveTextCommand { get; }

    /// <summary>
    /// プレビューの手動更新。タブを開いたときにも作り直すが、測定を流しながら
    /// このタブを開きっぱなしにする使い方では手で更新できたほうが早い。
    /// </summary>
    public RelayCommand RefreshPreviewCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>書き出される内容のプレビュー（テキスト形式）。タブを開くたびに作り直す。</summary>
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    // 試験だけを回した日もある。測定が空でも書き出せるようにする
    private bool CanSave() => _monitor.Rows.Count > 0 || _tcp.Rows.Count > 0 || _verify.Results.Count > 0;

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

            ReportData data = BuildData(ipConfig);

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

            Status = $"{Path.GetFileName(dialog.FileName)} に書き出しました"
                     + $"（測定 {data.Rows.Count} 件 / 試験 {data.Checks?.Count ?? 0} 件）。";
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

    /// <summary>記録タブを開いたときに、保存できるかとプレビューを見直す。</summary>
    public void OnActivated()
    {
        SaveHtmlCommand.RaiseCanExecuteChanged();
        SaveCsvCommand.RaiseCanExecuteChanged();
        SaveTextCommand.RaiseCanExecuteChanged();
        RefreshPreview();
    }

    /// <summary>起動時の状態へ戻す。</summary>
    public void Reset()
    {
        Status = string.Empty;
        Preview = string.Empty;
    }

    /// <summary>
    /// 出力内容のプレビューを作り直す。ipconfig /all は取得に時間がかかるので
    /// プレビューには入れず、保存時にだけ取得して差し込む。
    /// </summary>
    private void RefreshPreview()
    {
        try
        {
            Preview = !CanSave()
                ? "測定結果も試験結果もまだありません。Ping・TCP で測るか、試験タブで実行すると、"
                  + "ここに書き出される内容が表示されます。"
                : TextReportWriter.Render(BuildData(ipConfig: null))
                  + Environment.NewLine
                  + "※ HTML/テキストでの保存時は、ここに [ipconfig /all] の節も加わります。";
        }
        catch (Exception ex)
        {
            Preview = "プレビューを作れませんでした。";
            CrashLog.Write(ex, "ReportViewModel.RefreshPreview");
        }
    }

    /// <summary>
    /// 保存とプレビューで同じ内容を組む(表題とメモは 2026-08-16 に廃止)。
    ///
    /// <b>Ping と TCP の両方を載せる。</b>種別の言い表し方は画面ごとに違うので、
    /// 組にして渡す。不通の記録も両方から集める。
    /// </summary>
    private ReportData BuildData(string? ipConfig) => ReportService.Build(
        "",   // 空なら既定の表題になる
        "",
        [
            new ReportService.ReportSource(_monitor.Rows, _monitor.DescribeEffectiveKind),
            new ReportService.ReportSource(_tcp.Rows, _tcp.DescribeEffectiveKind),
        ],
        _monitor.NetworkInfo,
        _monitor.StartedAt ?? _tcp.StartedAt,
        _monitor.IntervalMs,
        ipConfig,
        wireless: _wifi.DescribeForReport(),
        outages: [.. _monitor.Tracker.Records, .. _tcp.Tracker.Records],
        wirelessNote: _wifi.ReportNote,
        wirelessAccessPoints: _wifi.DescribeAccessPointsForReport(),
        checks: [.. _verify.Results]);
}

/// <summary>書き出す形。</summary>
internal enum ReportFormat
{
    Html,
    Csv,
    Text,
}
