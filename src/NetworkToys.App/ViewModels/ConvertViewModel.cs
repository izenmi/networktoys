using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>種別 ComboBox の項目。Kind が null なら自動判定。</summary>
public sealed record ConvertModeViewModel(CiscoCommandKind? Kind, string Name);

/// <summary>
/// 変換タブ。Cisco コマンドの出力を貼り付けると表にして返す。
/// パースと整形は Core(<see cref="CiscoCsvConverter"/>)、xlsx の組み立ても
/// Core(<see cref="XlsxWriter"/>)で、ここは貼り付けのデバウンスと保存・コピーだけを持つ。
/// </summary>
public sealed class ConvertViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _debounce;

    private ConvertModeViewModel _selectedMode;

    /// <summary>xlsx はセル単位で書くのでプレビューの CSV 文字列からは戻せない。表のまま持つ。</summary>
    private CsvTable? _table;

    private string _inputText = "";
    private string _preview = "";
    private string _status = "Cisco コマンドの出力を貼り付けると、CSV に変換して下に表示します。";

    public ConvertViewModel()
    {
        Modes =
        [
            new ConvertModeViewModel(null, "自動判定"),
            new ConvertModeViewModel(CiscoCommandKind.IpRoute, "show ip route"),
            new ConvertModeViewModel(CiscoCommandKind.InterfaceBrief, "show ip interface brief"),
            new ConvertModeViewModel(CiscoCommandKind.CdpNeighbors, "show cdp neighbors"),
            new ConvertModeViewModel(CiscoCommandKind.MacTable, "show mac address-table"),
            new ConvertModeViewModel(CiscoCommandKind.InterfacesStatus, "show interfaces status"),
            new ConvertModeViewModel(CiscoCommandKind.Inventory, "show inventory"),
            new ConvertModeViewModel(CiscoCommandKind.Version, "show version"),
            new ConvertModeViewModel(CiscoCommandKind.Logging, "show logging"),
        ];
        _selectedMode = Modes[0];

        SaveCommand = new RelayCommand(Save, () => Preview.Length > 0);
        SaveXlsxCommand = new RelayCommand(SaveXlsx, () => _table is not null);
        CopyCommand = new RelayCommand(Copy, () => Preview.Length > 0);

        // 貼り付け直後に 1 文字ごとのパースが走らないよう、打ち終わりを待つ
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Parse();
        };
    }

    public ConvertModeViewModel[] Modes { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveXlsxCommand { get; }
    public RelayCommand CopyCommand { get; }

    public ConvertModeViewModel SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is not null && SetProperty(ref _selectedMode, value))
                Parse();
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (!SetProperty(ref _inputText, value))
                return;

            _debounce.Stop();
            _debounce.Start();
        }
    }

    public string Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                SaveXlsxCommand.RaiseCanExecuteChanged();
                CopyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Reset()
    {
        _debounce.Stop();
        InputText = "";
        SelectedMode = Modes[0];
        Show(null);
        Status = "Cisco コマンドの出力を貼り付けると、CSV に変換して下に表示します。";
    }

    private void Parse()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            Show(null);
            Status = "Cisco コマンドの出力を貼り付けると、CSV に変換して下に表示します。";
            return;
        }

        CiscoCommandKind? kind = SelectedMode.Kind ?? CiscoCsvConverter.Detect(InputText);
        if (kind is not { } resolved)
        {
            Show(null);
            Status = "コマンドの種類を判定できませんでした。上の一覧から種別を選んでください。";
            return;
        }

        CsvTable table = CiscoCsvConverter.Convert(resolved, InputText);
        string name = Modes.First(m => m.Kind == resolved).Name;

        if (table.Rows.Count == 0)
        {
            Show(null);
            Status = $"{name} として読みましたが、行を読み取れませんでした。出力の形を確かめてください。";
            return;
        }

        Show(table);
        Status = SelectedMode.Kind is null
            ? $"{name} と判定 — {table.Rows.Count} 行を変換しました。"
            : $"{name} — {table.Rows.Count} 行を変換しました。";
    }

    /// <summary>
    /// 変換結果を画面に出す。表とプレビューは必ず同時に入れ替える
    /// （片方だけ更新すると、前の表を xlsx で保存できてしまう）。
    /// </summary>
    private void Show(CsvTable? table)
    {
        _table = table;
        Preview = table?.ToCsv() ?? "";
    }

    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"convert-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = "csv",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // BOM 付き。無いと日本語版 Excel で文字化けする(既存の CSV 出力と同じ)
            File.WriteAllText(dialog.FileName, Preview, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// Excel ブックで保存する。CSV と違い、オートフィルタと見出し行の固定が付いた状態で開く。
    /// 生成は Core(<see cref="XlsxWriter"/>)側。ここは保存先を聞いて流し込むだけ。
    /// </summary>
    private void SaveXlsx()
    {
        if (_table is not { } table) return;

        var dialog = new SaveFileDialog
        {
            FileName = $"convert-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
            DefaultExt = "xlsx",
            Filter = "Excel ブック (*.xlsx)|*.xlsx|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using (FileStream file = File.Create(dialog.FileName))
            {
                XlsxWriter.Write(file, table);
            }

            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました（フィルタ設定済み）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    private void Copy()
    {
        try
        {
            Clipboard.SetText(Preview);
            Status = "CSV をクリップボードへコピーしました。";
        }
        catch (Exception ex)
        {
            Status = "コピーできませんでした。";
            CrashLog.Write(ex, "ConvertViewModel.Copy");
        }
    }
}
