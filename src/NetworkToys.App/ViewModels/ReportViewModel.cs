using System.IO;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Reporting;

namespace NetworkToys.App.ViewModels;

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
    }

    public RelayCommand SaveHtmlCommand { get; }
    public RelayCommand SaveCsvCommand { get; }

    /// <summary>
    /// テキストでの書き出し。報告書に貼る・チケットに残す用途では、
    /// HTML より扱いやすいことが多い。
    /// </summary>
    public RelayCommand SaveTextCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
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

    /// <summary>
    /// 保存できるかを見直す。<b>ファイルメニューを開くたびに呼ぶ。</b>
    ///
    /// 記録の画面は畳んだ（2026-08-16）ので、ここを呼ぶ機会がメニューを開く瞬間しかない。
    /// <see cref="RelayCommand"/> は <c>CommandManager</c> に乗っていないので、
    /// 誰かが知らせない限り「まだ何も無い」判定のまま固まり、保存が押せなくなる。
    /// </summary>
    public void RefreshSaveCommands()
    {
        SaveHtmlCommand.RaiseCanExecuteChanged();
        SaveCsvCommand.RaiseCanExecuteChanged();
        SaveTextCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 自己診断から中身を確かめるための口。
    /// <b>ipconfig も無線 API も触らない</b>（CI から OS を叩かない決まり）。
    /// </summary>
    internal ReportData BuildReportForSelfTest() => Build(ipConfig: null);

    /// <summary>起動時の状態へ戻す。</summary>
    public void Reset() => Status = string.Empty;

    /// <summary>
    /// 書き出す内容を組む(表題とメモは 2026-08-16 に廃止)。
    ///
    /// <b>Ping と TCP の両方を載せる。</b>種別の言い表し方は画面ごとに違うので、
    /// 組にして渡す。不通の記録も両方から集める。
    /// </summary>
    private ReportData BuildData(string? ipConfig)
    {
        // 無線タブを開いていなくても記録に載せる。スキャンはせず、
        // OS がすでに持っている内容を 1 度だけ読む（2026-08-16 ユーザー指示）
        _wifi.EnsureLoadedForReport();

        return Build(ipConfig);
    }

    private ReportData Build(string? ipConfig) => ReportService.Build(
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
