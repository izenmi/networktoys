using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using PastelNet.App.Mvvm;
using PastelNet.Core.Work;

namespace PastelNet.App.ViewModels;

/// <summary>貼り付けた出力の種類。何を落として何を構造化するかが変わる。</summary>
public enum DeviceCompareMode
{
    /// <summary>show ip route。経路として突き合わせる。</summary>
    RouteTable,

    /// <summary>show run。行として突き合わせる。</summary>
    Configuration,

    /// <summary>その他。素の行差分。</summary>
    PlainText,
}

/// <summary>
/// 機器の出力を作業前後で見比べる画面。
///
/// 左右に並べて差分を色分けする。<b>show ip route だけは行の差分にしない。</b>
/// 出力に経過時間が入っているため、設定を何も変えていなくても動的経路の行が
/// すべて差分になってしまうので、経路として構造化して比べる。
/// </summary>
public sealed class DeviceCompareViewModel : ObservableObject
{
    private string _beforeText = string.Empty;
    private string _afterText = string.Empty;
    private string _status = "作業前と作業後の出力を貼り付けて「比較」を押してください。";
    private string _headline = string.Empty;
    private bool _onlyDifferences = true;
    private bool _hideNoise = true;
    private bool _hasResult;
    private bool _isEditing = true;
    private DeviceCompareMode _mode = DeviceCompareMode.RouteTable;

    public DeviceCompareViewModel()
    {
        CompareCommand = new RelayCommand(Compare, () => BeforeText.Length > 0 || AfterText.Length > 0);
        EditCommand = new RelayCommand(() => IsEditing = true);
        SwapCommand = new RelayCommand(Swap);
        ClearCommand = new RelayCommand(Clear);
        LoadBeforeCommand = new RelayCommand(() => LoadInto(before: true));
        LoadAfterCommand = new RelayCommand(() => LoadInto(before: false));
    }

    /// <summary>左右に並べた差分。</summary>
    public ObservableCollection<SideBySideRow> Rows { get; } = [];

    /// <summary>経路として比べたときの変化。show ip route のときだけ入る。</summary>
    public ObservableCollection<RouteTableChange> RouteChanges { get; } = [];

    public RelayCommand CompareCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand SwapCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand LoadBeforeCommand { get; }
    public RelayCommand LoadAfterCommand { get; }

    /// <summary>並び順は <see cref="DeviceCompareMode"/> と対応させること。</summary>
    public string[] Modes { get; } = ["show ip route", "show run", "そのまま比較"];

    public string SelectedMode
    {
        get => Modes[(int)_mode];
        set
        {
            int index = Array.IndexOf(Modes, value);
            if (index < 0) return;

            var mode = (DeviceCompareMode)index;
            if (_mode == mode) return;

            _mode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRouteMode));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    public bool IsRouteMode => _mode == DeviceCompareMode.RouteTable;

    public string ModeHint => _mode switch
    {
        DeviceCompareMode.RouteTable =>
            "経路として突き合わせます。経過時間（00:15:23 など）は無視するので、時間が経っただけの違いは差分になりません。",
        DeviceCompareMode.Configuration =>
            "行として突き合わせます。Building configuration や Last configuration change のような、設定を触らなくても毎回変わる行は既定で除きます。",
        _ => "行として、そのまま突き合わせます。",
    };

    public string BeforeText
    {
        get => _beforeText;
        set
        {
            if (SetProperty(ref _beforeText, value))
                CompareCommand.RaiseCanExecuteChanged();
        }
    }

    public string AfterText
    {
        get => _afterText;
        set
        {
            if (SetProperty(ref _afterText, value))
                CompareCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>結果の要約。</summary>
    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    /// <summary>差のある行だけを出すか。長い設定を見るときに効く。</summary>
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (SetProperty(ref _onlyDifferences, value) && HasResult)
                Compare();
        }
    }

    /// <summary>毎回変わる行を除くか。</summary>
    public bool HideNoise
    {
        get => _hideNoise;
        set
        {
            if (SetProperty(ref _hideNoise, value) && HasResult)
                Compare();
        }
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    /// <summary>貼り付け欄を出しているか。比較したら結果に切り替える。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    private void Compare()
    {
        DiffNoiseFilter? filter = HideNoise
            ? _mode switch
            {
                DeviceCompareMode.Configuration => DiffNoiseFilter.CiscoConfig,
                _ => null,
            }
            : null;

        SideBySideResult result = SideBySideDiff.Build(BeforeText, AfterText, filter);

        Rows.Clear();
        RouteChanges.Clear();

        if (result.TooLarge)
        {
            Status = "出力が大きすぎるため差分を取れませんでした。";
            Headline = string.Empty;
            HasResult = false;
            return;
        }

        IEnumerable<SideBySideRow> rows = OnlyDifferences ? SideBySideDiff.OnlyDifferences(result) : result.Rows;

        foreach (SideBySideRow row in rows)
            Rows.Add(row);

        if (_mode == DeviceCompareMode.RouteTable)
            CompareRoutes();
        else
            Headline = result.HasChanges ? $"{result.ChangedCount} 行に違いがあります。" : "違いはありません。";

        string ignored = result.IgnoredLines > 0 ? $"（毎回変わる {result.IgnoredLines} 行は除いています）" : string.Empty;
        Status = Rows.Count == 0 && result.Rows.Count > 0
            ? $"表示できる行がありません{ignored}。"
            : $"比較しました{ignored}。";

        HasResult = true;
        IsEditing = false;
    }

    /// <summary>
    /// 経路として突き合わせる。
    /// 行の差分より先に、この結果を見てもらいたい（本当に知りたいのはこちらなので）。
    /// </summary>
    private void CompareRoutes()
    {
        IReadOnlyList<CiscoRoute> before = CiscoRouteParser.Parse(BeforeText);
        IReadOnlyList<CiscoRoute> after = CiscoRouteParser.Parse(AfterText);

        if (before.Count == 0 && after.Count == 0)
        {
            Headline = "経路を読み取れませんでした。show ip route の出力かどうか確かめてください。";
            return;
        }

        RouteTableSummary summary = RouteTableDiff.Compare(before, after);

        foreach (RouteTableChange change in summary.Changes)
            RouteChanges.Add(change);

        Headline = summary.Headline;
    }

    private void Swap()
    {
        (BeforeText, AfterText) = (AfterText, BeforeText);

        if (HasResult)
            Compare();
    }

    private void Clear()
    {
        BeforeText = string.Empty;
        AfterText = string.Empty;
        Rows.Clear();
        RouteChanges.Clear();
        Headline = string.Empty;
        HasResult = false;
        IsEditing = true;
        Status = "作業前と作業後の出力を貼り付けて「比較」を押してください。";
    }

    /// <summary>ログをファイルで持っている場合のために、読み込みも用意する。</summary>
    private void LoadInto(bool before)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "テキスト (*.txt;*.log;*.cfg)|*.txt;*.log;*.cfg|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            string text = File.ReadAllText(dialog.FileName);

            if (before)
                BeforeText = text;
            else
                AfterText = text;

            Status = $"{Path.GetFileName(dialog.FileName)} を読み込みました。";
            IsEditing = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"読み込めませんでした: {ex.Message}";
        }
    }
}
