using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

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
    // 既定は全行表示(前後の文脈ごと読みたい、というユーザー指示)
    private bool _onlyDifferences;
    private bool _hasResult;
    private bool _isEditing = true;
    private string _note = string.Empty;
    private DeviceOutputKind _mode = DeviceOutputKind.Configuration;   // 既定は show run（ユーザー指示）
    private int _selectedIndex = -1;

    private DeviceEntryViewModel _selectedDevice;

    /// <summary>
    /// 差し替えの最中は貼り付けを預けない。
    /// 作業前を入れ終える前に作業後がまだ古いままなので、
    /// 途中の状態を預けると別の機器の内容が混ざる。
    /// </summary>
    private bool _restoring;

    public DeviceCompareViewModel()
    {
        _selectedDevice = new DeviceEntryViewModel("機器 1");
        Devices.Add(_selectedDevice);

        CompareCommand = new RelayCommand(Compare, () => BeforeText.Length > 0 || AfterText.Length > 0);
        // 貼り付け欄を出している間は押しても何も起きない。
        // 押せるままにしておくと「押しても効かない」と読まれる（実際にそう報告された）
        EditCommand = new RelayCommand(() => IsEditing = true, () => !IsEditing);
        LoadBeforeCommand = new RelayCommand(() => LoadInto(before: true));
        LoadAfterCommand = new RelayCommand(() => LoadInto(before: false));
        SaveBeforeCommand = new RelayCommand(() => SaveFrom(before: true), () => BeforeText.Length > 0);
        SaveAfterCommand = new RelayCommand(() => SaveFrom(before: false), () => AfterText.Length > 0);

        NextDifferenceCommand = new RelayCommand(() => MoveToDifference(forward: true), () => DifferenceCount > 0);
        PreviousDifferenceCommand = new RelayCommand(() => MoveToDifference(forward: false), () => DifferenceCount > 0);

        SaveChangesCommand = new RelayCommand(SaveChanges, () => Changes.Count > 0);

        AddDeviceCommand = new RelayCommand(AddDevice);
        RemoveDeviceCommand = new RelayCommand(RemoveDevice, () => Devices.Count > 1);

        RefreshModeMarks();
    }

    /// <summary>
    /// 見比べる機器。
    ///
    /// 1 回の作業で何台も触ることがあるので、機器ごとに貼り付けを分けて持つ。
    /// 台数ぶんの「作業前」を先に集めておき、作業後に順に突き合わせる使い方になる。
    /// </summary>
    public ObservableCollection<DeviceEntryViewModel> Devices { get; } = [];

    public RelayCommand AddDeviceCommand { get; }
    public RelayCommand RemoveDeviceCommand { get; }

    public DeviceEntryViewModel SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedDevice)) return;

            // いまの機器の貼り付けを預けてから移る
            RememberInto(_selectedDevice);
            _selectedDevice = value;

            OnPropertyChanged();
            Restore();
            RefreshModeMarks();
        }
    }

    private void AddDevice()
    {
        RememberInto(_selectedDevice);

        // 名前は後から書き換えられる。連番なのは、まず並べてから名前を付けることが多いため
        var device = new DeviceEntryViewModel($"機器 {Devices.Count + 1}");
        Devices.Add(device);
        RemoveDeviceCommand.RaiseCanExecuteChanged();

        SelectedDevice = device;
    }

    private void RemoveDevice()
    {
        if (Devices.Count <= 1) return;

        int index = Devices.IndexOf(_selectedDevice);
        Devices.Remove(_selectedDevice);
        RemoveDeviceCommand.RaiseCanExecuteChanged();

        // 消したら隣へ移る。末尾を消したときは 1 つ前
        _selectedDevice = Devices[Math.Min(index, Devices.Count - 1)];

        OnPropertyChanged(nameof(SelectedDevice));
        Restore();
        RefreshModeMarks();
    }

    /// <summary>左右に並べた差分。</summary>
    public ObservableCollection<SideBySideRow> Rows { get; } = [];

    /// <summary>構造として比べたときの変化。対象によっては空（show run など）。</summary>
    public ObservableCollection<DeviceChange> Changes { get; } = [];

    /// <summary>
    /// 比べた結果を CSV に出す。貼り付けた原文は保存できるのに、
    /// 肝心の「何が変わったか」だけ持ち出せなかった。
    /// </summary>
    private void SaveChanges()
    {
        var table = new CsvTable(
            ["重さ", "対象", "種類", "内容"],
            [.. Changes.Select(c => new[]
            {
                c.Severity switch
                {
                    ChangeSeverity.Critical => "重大",
                    ChangeSeverity.Warning => "注意",
                    _ => "情報",
                },
                c.Key, c.Kind, c.Description,
            })]);

        if (Services.CsvExport.Save("diff", table) is { } message)
            Status = message;
    }

    public RelayCommand NextDifferenceCommand { get; }
    public RelayCommand PreviousDifferenceCommand { get; }

    /// <summary>
    /// いま見ている行。移動ボタンがここを動かし、一覧が追いかける。
    /// 一覧側の選択も同じ値を書き戻すので、クリックで選んでから
    /// 「次へ」を押すと、その位置の続きから進む。
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value))
                OnPropertyChanged(nameof(PositionText));
        }
    }

    /// <summary>表示している行のうち、差のあるものの数。</summary>
    public int DifferenceCount => Rows.Count(r => r.IsDifferent);

    /// <summary>「3 / 12 か所目」のような現在位置。どこまで見たかが分かる。</summary>
    public string PositionText
    {
        get
        {
            int total = DifferenceCount;
            if (total == 0) return string.Empty;

            // 選んでいる行が差分なら何番目かを出す。そうでなければ総数だけ
            if (SelectedIndex < 0 || SelectedIndex >= Rows.Count || !Rows[SelectedIndex].IsDifferent)
                return $"差分 {total} か所";

            int ordinal = 0;
            for (int i = 0; i <= SelectedIndex; i++)
            {
                if (Rows[i].IsDifferent) ordinal++;
            }

            return $"{ordinal} / {total} か所目";
        }
    }

    private void RefreshDifferenceState()
    {
        OnPropertyChanged(nameof(DifferenceCount));
        OnPropertyChanged(nameof(PositionText));
        NextDifferenceCommand.RaiseCanExecuteChanged();
        PreviousDifferenceCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 次（前）の差分へ移る。端まで来たら反対の端へ回り込む。
    /// 長い設定を見ているときに、端で止まって押し直すのは煩わしい。
    /// </summary>
    private void MoveToDifference(bool forward)
    {
        if (Rows.Count == 0) return;

        int step = forward ? 1 : -1;
        int start = SelectedIndex < 0 ? (forward ? -1 : Rows.Count) : SelectedIndex;

        for (int offset = 1; offset <= Rows.Count; offset++)
        {
            int index = ((start + (step * offset)) % Rows.Count + Rows.Count) % Rows.Count;

            if (Rows[index].IsDifferent)
            {
                SelectedIndex = index;
                RequestScrollIntoView?.Invoke(this, index);
                return;
            }
        }
    }

    /// <summary>
    /// 移った先を見えるところへ持ってきてほしい、という合図。
    /// スクロールは画面側の仕事なので、ここでは頼むだけにする。
    /// </summary>
    public event EventHandler<int>? RequestScrollIntoView;

    public RelayCommand CompareCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand LoadBeforeCommand { get; }
    public RelayCommand LoadAfterCommand { get; }
    public RelayCommand SaveBeforeCommand { get; }
    public RelayCommand SaveAfterCommand { get; }
    public RelayCommand SaveChangesCommand { get; }

    /// <summary>
    /// 比較対象の選択肢。<see cref="DeviceOutputKind"/> の一部だけを載せる
    /// （interface brief / cdp / mac address-table は 2026-08-15 に削除）。
    /// 参照は <see cref="SelectedMode"/> で Kind から引くので、並び順に依存しない。
    /// </summary>
    public DeviceModeViewModel[] Modes { get; } =
    [
        new(DeviceOutputKind.Configuration, "show run"),
        new(DeviceOutputKind.RouteTable, "show ip route"),
        new(DeviceOutputKind.PlainText, "そのまま比較"),
    ];

    public DeviceModeViewModel SelectedMode
    {
        get => Array.Find(Modes, m => m.Kind == _mode) ?? Modes[0];
        set
        {
            if (value is null || value.Kind == _mode) return;

            // いまの対象の貼り付けを預けてから切り替え、行き先の分を出す
            RememberInto(_selectedDevice);
            _mode = value.Kind;
            Restore();

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStructuredView));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    /// <summary>貼り付けの有無を選択肢に映す。どの対象を record 済みかが一目で分かる。</summary>
    private void RefreshModeMarks()
    {
        // 貼っている最中も機器側の数を合わせる。切り替えるまで反映されないと、
        // 「何台ぶん集め終えたか」がその場で分からない
        if (!_restoring)
            RememberInto(_selectedDevice);

        foreach (DeviceModeViewModel mode in Modes)
        {
            (string before, string after) = mode.Kind == _mode
                ? (BeforeText, AfterText)
                : _selectedDevice.Find(mode.Kind) is { } pair ? (pair.Before, pair.After) : (string.Empty, string.Empty);

            mode.SetState(before.Length > 0, after.Length > 0);
        }

        OnPropertyChanged(nameof(PastedSummary));
    }

    /// <summary>貼り付け済みの対象を並べた一言。作業前の取りこぼしに気づけるようにする。</summary>
    public string PastedSummary
    {
        get
        {
            string[] done = [.. Modes.Where(m => m.HasAnything).Select(m => m.Name + m.StateMark)];

            return done.Length == 0
                ? $"{SelectedDevice.Name}: まだ何も貼り付けていません。"
                : $"{SelectedDevice.Name}: " + string.Join(" ／ ", done);
        }
    }

    private void RememberInto(DeviceEntryViewModel device)
        => device.Remember(_mode, BeforeText, AfterText);

    /// <summary>
    /// 貼り付けてある出力を丸ごと控える（昇格で起動し直すときの引き継ぎ用）。
    /// いま画面に出ている分をまず預けてから集める。
    /// </summary>
    internal void CaptureInto(NetworkToys.Core.Storage.HandoverPanels panels)
    {
        RememberInto(_selectedDevice);

        panels.SelectedDevice = _selectedDevice.Name;
        panels.SelectedMode = (int)_mode;
        panels.DiffNote = Note;

        foreach (DeviceEntryViewModel device in Devices)
        {
            var saved = new NetworkToys.Core.Storage.HandoverDevice { Name = device.Name };

            foreach ((DeviceOutputKind kind, PastedPair pair) in device.AllPasted())
            {
                saved.Pasted.Add(new NetworkToys.Core.Storage.HandoverPaste
                {
                    Kind = (int)kind,
                    Before = pair.Before,
                    After = pair.After,
                });
            }

            panels.Devices.Add(saved);
        }
    }

    /// <summary>控えた貼り付けを書き戻す。機器の並びごと作り直す。</summary>
    internal void ApplyHandover(NetworkToys.Core.Storage.HandoverPanels panels)
    {
        if (panels.Devices.Count == 0) return;

        Devices.Clear();

        foreach (NetworkToys.Core.Storage.HandoverDevice saved in panels.Devices)
        {
            var device = new DeviceEntryViewModel(saved.Name);

            foreach (NetworkToys.Core.Storage.HandoverPaste paste in saved.Pasted)
                device.Remember((DeviceOutputKind)paste.Kind, paste.Before, paste.After);

            Devices.Add(device);
        }

        _selectedDevice = Devices.FirstOrDefault(d => d.Name == panels.SelectedDevice) ?? Devices[0];
        _mode = Enum.IsDefined((DeviceOutputKind)panels.SelectedMode)
            ? (DeviceOutputKind)panels.SelectedMode
            : _mode;

        // 選んだ機器と対象の貼り付けを画面へ出す
        Restore();

        RemoveDeviceCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(HasStructuredView));
        OnPropertyChanged(nameof(ModeHint));
    }

    private void Restore()
    {
        PastedPair? pair = _selectedDevice.Find(_mode);
        _restoring = true;

        // 結果は対象ごとに持たない。切り替えたら貼り付け欄に戻し、必要なら比べ直す
        Rows.Clear();
        Changes.Clear();
        SaveChangesCommand.RaiseCanExecuteChanged();
        Headline = string.Empty;
        Note = string.Empty;
        HasResult = false;
        IsEditing = true;
        SelectedIndex = -1;

        BeforeText = pair?.Before ?? string.Empty;
        AfterText = pair?.After ?? string.Empty;

        _restoring = false;

        OnPropertyChanged(nameof(HasNote));
        RefreshDifferenceState();

        Status = (BeforeText.Length, AfterText.Length) switch
        {
            (0, 0) => "作業前と作業後の出力を貼り付けて「比較」を押してください。",
            (> 0, 0) => "作業前だけ貼ってあります。作業後を貼ると比べられます。",
            (0, > 0) => "作業後だけ貼ってあります。作業前を貼ると比べられます。",
            _ => "作業前と作業後が揃っています。「比較」を押してください。",
        };
    }



    /// <summary>構造として比べられる対象か。行差分より上に変化の一覧を出す。</summary>
    public bool HasStructuredView => _mode
        is DeviceOutputKind.RouteTable
        or DeviceOutputKind.InterfaceBrief
        or DeviceOutputKind.CdpNeighbors
        or DeviceOutputKind.MacTable;

    /// <summary>読むときの注意。対象によって出す。</summary>
    public string Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    public bool HasNote => Note.Length > 0;

    public string ModeHint => _mode switch
    {
        DeviceOutputKind.RouteTable =>
            "経路として突き合わせます。経過時間（00:15:23 など）は無視するので、時間が経っただけの違いは差分になりません。",
        DeviceOutputKind.InterfaceBrief =>
            "ポートの状態を突き合わせます。up から落ちたポートを見つけます。",
        DeviceOutputKind.CdpNeighbors =>
            "隣接機器と接続先のポートを突き合わせます。Holdtime は無視するので、時間が経っただけの違いは差分になりません。ケーブルを別のポートに挿し直していれば見つかります。",
        DeviceOutputKind.MacTable =>
            "MAC がどのポートに見えるかを突き合わせます。動的エントリは通信が無いと数分で消えるため、増減より「ポートが移った」を見てください。",
        DeviceOutputKind.Configuration =>
            "行として突き合わせます。Building configuration や Last configuration change のような、設定を触らなくても毎回変わる行は既定で除きます。",
        _ => "行として、そのまま突き合わせます。",
    };

    public string BeforeText
    {
        get => _beforeText;
        set
        {
            if (!SetProperty(ref _beforeText, value)) return;

            CompareCommand.RaiseCanExecuteChanged();
            SaveBeforeCommand.RaiseCanExecuteChanged();
            RefreshModeMarks();
        }
    }

    public string AfterText
    {
        get => _afterText;
        set
        {
            if (!SetProperty(ref _afterText, value)) return;

            CompareCommand.RaiseCanExecuteChanged();
            SaveAfterCommand.RaiseCanExecuteChanged();
            RefreshModeMarks();
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

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    /// <summary>貼り付け欄を出しているか。比較したら結果に切り替える。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value)) EditCommand.RaiseCanExecuteChanged();
        }
    }

    private void Compare()
    {
        // 毎回変わる行(Building configuration など)は常に除く。切り替え UI は
        // 2026-08-16 のユーザー指示で廃止した
        DiffNoiseFilter? filter = DeviceComparison.NoiseFilterFor(_mode);

        SideBySideResult result = SideBySideDiff.Build(BeforeText, AfterText, filter);

        Rows.Clear();
        Changes.Clear();
        SaveChangesCommand.RaiseCanExecuteChanged();
        Note = string.Empty;
        OnPropertyChanged(nameof(HasNote));   // 空にした側も通知しないと前回の注意書きが残る

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

        DeviceCompareOutcome? outcome = DeviceComparison.Compare(_mode, BeforeText, AfterText);

        if (outcome is not null)
        {
            // 行の差分より先に、この結果を見てもらいたい（本当に知りたいのはこちらなので）
            foreach (DeviceChange change in outcome.Changes)
                Changes.Add(change);

            Headline = outcome.Headline;
            Note = outcome.Note ?? string.Empty;
            OnPropertyChanged(nameof(HasNote));
        }

        else
        {
            Headline = result.HasChanges ? $"{result.ChangedCount} 行に違いがあります。" : "違いはありません。";
        }

        SaveChangesCommand.RaiseCanExecuteChanged();

        string ignored = result.IgnoredLines > 0 ? $"（毎回変わる {result.IgnoredLines} 行は除いています）" : string.Empty;
        Status = Rows.Count == 0 && result.Rows.Count > 0
            ? $"表示できる行がありません{ignored}。"
            : $"比較しました{ignored}。";

        HasResult = true;
        IsEditing = false;

        // 比較し直したら先頭から見る
        SelectedIndex = -1;
        RefreshDifferenceState();
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

    /// <summary>作業前／作業後の貼り付けをそのままテキストファイルへ保存する。</summary>
    private void SaveFrom(bool before)
    {
        string text = before ? BeforeText : AfterText;
        if (text.Length == 0) return;

        var dialog = new SaveFileDialog
        {
            Filter = "テキスト (*.txt)|*.txt|ログ (*.log)|*.log|すべてのファイル (*.*)|*.*",
            FileName = $"{SelectedMode.Kind}-{(before ? "before" : "after")}.txt",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // BOM 付き UTF-8。メモ帳でも Excel でも文字化けしない（レポートと同じ判断）
            File.WriteAllText(dialog.FileName, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>貼り付けた内容と結果を起動時の状態へ戻す。</summary>
    public void Reset()
    {
        Devices.Clear();
        _selectedDevice = new DeviceEntryViewModel("機器 1");
        Devices.Add(_selectedDevice);
        OnPropertyChanged(nameof(SelectedDevice));
        RemoveDeviceCommand.RaiseCanExecuteChanged();

        // 旧「消去」相当(2026-08-16 にボタンは廃止。全消去のときだけ使う)
        _selectedDevice.Remember(_mode, string.Empty, string.Empty);
        BeforeText = string.Empty;
        AfterText = string.Empty;
        Rows.Clear();
        Changes.Clear();
        SaveChangesCommand.RaiseCanExecuteChanged();
        Headline = string.Empty;
        HasResult = false;
        IsEditing = true;
        Status = "作業前と作業後の出力を貼り付けて「比較」を押してください。";
        SelectedIndex = -1;
        RefreshDifferenceState();

        SelectedMode = Modes[0];
        RefreshModeMarks();
        Note = string.Empty;
        OnPropertyChanged(nameof(HasNote));
        CompareCommand.RaiseCanExecuteChanged();
    }

}

/// <summary>
/// 比較する対象ひとつ。貼り付けの有無を持たせて、
/// どの対象を record 済みかを選択肢の上で分かるようにしている。
/// </summary>
public sealed class DeviceModeViewModel : ObservableObject
{
    private string _stateMark = string.Empty;

    public DeviceModeViewModel(DeviceOutputKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public DeviceOutputKind Kind { get; }

    public string Name { get; }

    /// <summary>「（前）」「（前後）」のような印。何も貼っていなければ空。</summary>
    public string StateMark
    {
        get => _stateMark;
        private set
        {
            if (SetProperty(ref _stateMark, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    public bool HasAnything => StateMark.Length > 0;

    /// <summary>選択肢に出す文字列。</summary>
    public string Label => StateMark.Length == 0 ? Name : $"{Name}　{StateMark}";

    internal void SetState(bool before, bool after)
        => StateMark = (before, after) switch
        {
            (true, true) => "（前後）",
            (true, false) => "（前）",
            (false, true) => "（後）",
            _ => string.Empty,
        };
}

/// <summary>対象ごとに預けてある貼り付け。</summary>
public sealed record PastedPair(string Before, string After);

/// <summary>
/// 見比べる機器 1 台。
///
/// 対象（show ip route / show run など）ごとの貼り付けを抱える。
/// 1 回の作業で何台も設定変更することがあるので、台数ぶん並べて
/// 「作業前」を先に集めておけるようにしている。
/// </summary>
public sealed class DeviceEntryViewModel : ObservableObject
{
    private readonly Dictionary<DeviceOutputKind, PastedPair> _pasted = [];
    private string _name;

    public DeviceEntryViewModel(string name) => _name = name;

    /// <summary>機器名。ホスト名を入れておくと記録を見返すときに分かる。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>貼り付けてある対象の数。</summary>
    public int PastedCount => _pasted.Count;

    /// <summary>一覧に出す表示。貼り付け済みの数を添えて、集め忘れに気づけるようにする。</summary>
    public string Label => PastedCount == 0 ? Name : $"{Name}　{PastedCount}";

    public PastedPair? Find(DeviceOutputKind kind)
        => _pasted.TryGetValue(kind, out PastedPair? pair) ? pair : null;

    /// <summary>預かっている貼り付けを全部返す（昇格で起動し直すときの引き継ぎ用）。</summary>
    internal IEnumerable<KeyValuePair<DeviceOutputKind, PastedPair>> AllPasted() => _pasted;

    /// <summary>貼り付けを預かる。両方とも空なら覚えない（数に含めない）。</summary>
    public void Remember(DeviceOutputKind kind, string before, string after)
    {
        if (before.Length == 0 && after.Length == 0)
            _pasted.Remove(kind);
        else
            _pasted[kind] = new PastedPair(before, after);

        OnPropertyChanged(nameof(PastedCount));
        OnPropertyChanged(nameof(Label));
    }
}
