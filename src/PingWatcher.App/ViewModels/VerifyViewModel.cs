using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Verify;

namespace PingWatcher.App.ViewModels;

/// <summary>試験項目 1 行。入力欄と結果を同じ 1 クラスに持つ（収集タブと同じ作り）。</summary>
public sealed class VerifyRowViewModel : ObservableObject
{
    private string _name;
    private string _kind;
    private string _target;
    private string _expect;
    private string _status = "◌ 未実行";

    internal VerifyRowViewModel(CheckItem item)
    {
        _name = item.Name;
        _kind = CheckListParser.NameOf(item.Kind);
        _target = item.Target;
        _expect = item.Expect;
    }

    /// <summary>画面のコンボに出す選択肢。行ごとに持つのがこの表の作法。</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        [.. Enum.GetValues<CheckKind>().Select(CheckListParser.NameOf)];

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
                OnPropertyChanged(nameof(TargetHint));
        }
    }

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public string Expect { get => _expect; set => SetProperty(ref _expect, value); }

    /// <summary>記号と文字を併記する（色だけで表さない決まり）。</summary>
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }

    /// <summary>宛先欄に何を書けばよいか。種類で変わるので入力の助けに出す。</summary>
    public string TargetHint => CurrentKind switch
    {
        CheckKind.Http => "URL（例 https://portal.example.jp/）",
        CheckKind.Dns => "引きたい名前（例 www.example.jp）",
        CheckKind.Teams => "空でかまいません（Teams の既定の宛先を使います）",
        CheckKind.Download => "測る URL（大きめのファイル）",
        CheckKind.Upload => "送り先の URL（末尾に |20 と書くと 20MB 送ります）",
        CheckKind.FastCom => "空でかまいません（fast.com で上り下りを測ります）",
        CheckKind.Smtp => "host:port（省略すると 587）",
        CheckKind.Imap => "host:port（省略すると 993）",
        CheckKind.Pop3 => "host:port（省略すると 995）",
        _ => "host:port（例 fs01:445）",
    };

    internal CheckKind CurrentKind
        => CheckListParser.TryParseKind(_kind, out CheckKind kind) ? kind : CheckKind.Http;

    internal CheckItem ToItem() => new(Name.Trim(), CurrentKind, Target.Trim(), Expect.Trim());
}

/// <summary>試すプロキシ 1 件。チェックで選ぶ。</summary>
public sealed class ProxyChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    internal ProxyChoiceViewModel(ProxyChoice choice, bool selected)
    {
        Choice = choice;
        _isSelected = selected;
    }

    internal ProxyChoice Choice { get; }

    public string Name => Choice.Name;

    public string Summary => Choice.Summary;

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// 業務確認試験の画面。
///
/// <b>プロキシを切り替えて同じ試験を回せる</b>のが要。ただし
/// <b>プロキシが効くのは HTTP の項目だけ</b>で、TCP・メール・DNS・Teams は
/// 直接出る。誤解の元なので画面にも書く。
/// </summary>
public sealed class VerifyViewModel : ObservableObject
{
    private const string ProxyNotice =
        "ℹ プロキシを変えて結果が変わるのは HTTP の項目だけです。"
        + "TCP・メール・DNS・Teams は直接出るため、何本選んでも 1 回だけ試験します。";

    private string _proxyText = "";
    private string _status = "ひな型を入れるか項目を書いて「まとめて実行」を押します。";
    private bool _isBusy;
    private int _done;
    private int _total;
    private CancellationTokenSource? _cts;

    public VerifyViewModel()
    {
        Templates = [.. RecommendedChecks.Templates.Select(t => new CheckTemplate(t.Name, t.Text))];
        _selectedTemplate = Templates[0];

        AddRowCommand = new RelayCommand(() => AddRow(new CheckItem("", CheckKind.Http, "")));
        RemoveRowCommand = new RelayCommand<VerifyRowViewModel>(RemoveRow);
        ApplyTemplateCommand = new RelayCommand(ApplyTemplate, () => !IsBusy);
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && Rows.Count > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SaveCommand = new RelayCommand(Save, () => Results.Count > 0);
        LoadItemsCommand = new RelayCommand(LoadItems, () => !IsBusy);
        SaveItemsCommand = new RelayCommand(SaveItems, () => Rows.Count > 0);

        _proxyText = Settings.Current.VerifyProxies;

        foreach (CheckItem item in CheckListParser.Parse(Settings.Current.VerifyChecks))
            AddRow(item);

        RebuildProxies();
    }

    public ObservableCollection<VerifyRowViewModel> Rows { get; } = [];

    public ObservableCollection<ProxyChoiceViewModel> Proxies { get; } = [];

    public ObservableCollection<CheckResult> Results { get; } = [];

    public IReadOnlyList<CheckTemplate> Templates { get; }

    private CheckTemplate _selectedTemplate;

    public CheckTemplate SelectedTemplate
    {
        get => _selectedTemplate;
        set => SetProperty(ref _selectedTemplate, value);
    }

    public RelayCommand AddRowCommand { get; }
    public RelayCommand<VerifyRowViewModel> RemoveRowCommand { get; }
    public RelayCommand ApplyTemplateCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveCommand { get; }

    /// <summary>試験項目をファイルから読み込む。現場ごとに使い分けるため。</summary>
    public RelayCommand LoadItemsCommand { get; }

    /// <summary>試験項目をファイルへ保存する。</summary>
    public RelayCommand SaveItemsCommand { get; }

    public string Notice => ProxyNotice;

    /// <summary>
    /// 1 行 1 件で <c>名前,種類,アドレス</c>。種類は <c>pac</c> / <c>proxy</c>。
    /// 「直接」と「いまの設定」は常にあるので書かない。
    /// </summary>
    public string ProxyText
    {
        get => _proxyText;
        set
        {
            if (SetProperty(ref _proxyText, value))
                RebuildProxies();
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            ApplyTemplateCommand.RaiseCanExecuteChanged();
            LoadItemsCommand.RaiseCanExecuteChanged();
            StartCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private void AddRow(CheckItem item)
    {
        Rows.Add(new VerifyRowViewModel(item));
        StartCommand.RaiseCanExecuteChanged();
        SaveItemsCommand.RaiseCanExecuteChanged();
    }

    private void RemoveRow(VerifyRowViewModel? row)
    {
        if (row is null) return;

        Rows.Remove(row);
        StartCommand.RaiseCanExecuteChanged();
        SaveItemsCommand.RaiseCanExecuteChanged();
    }

    private void ApplyTemplate()
    {
        Rows.Clear();

        foreach (CheckItem item in CheckListParser.Parse(SelectedTemplate.Text))
            AddRow(item);

        Status = $"「{SelectedTemplate.Name}」を入れました。宛先を現場に合わせて書き換えてください。";
    }

    /// <summary>
    /// 定義のテキストから選択肢を作り直す。<b>選んであったものは名前で選び直す</b>
    /// （書き換えるたびにチェックが外れると使い物にならない）。
    /// </summary>
    private void RebuildProxies()
    {
        HashSet<string> selected = [.. Proxies.Where(p => p.IsSelected).Select(p => p.Name)];

        // 初回は「いまの設定」を選んでおく。まず現状が通ることを確かめるのが自然な順番
        bool first = Proxies.Count == 0;

        Proxies.Clear();

        foreach (ProxyChoice choice in ProxyListParser.Parse(_proxyText))
        {
            bool on = first
                ? choice.Mode == ProxyMode.System
                : selected.Contains(choice.Name);

            Proxies.Add(new ProxyChoiceViewModel(choice, on));
        }
    }

    private async Task RunAsync()
    {
        IReadOnlyList<CheckItem> items = [.. Rows.Select(r => r.ToItem()).Where(i => i.Name.Length > 0)];

        if (items.Count == 0)
        {
            Status = "項目名の入っている行がありません。";
            return;
        }

        IReadOnlyList<ProxyChoice> proxies =
            [.. Proxies.Where(p => p.IsSelected).Select(p => p.Choice)];

        IsBusy = true;
        Results.Clear();
        SaveCommand.RaiseCanExecuteChanged();

        foreach (VerifyRowViewModel row in Rows)
            row.Status = "◌ 待機";

        _cts = new CancellationTokenSource();

        var progress = new Progress<(int Done, int Total, string Name)>(p =>
        {
            _done = p.Done;
            _total = p.Total;

            Status = p.Name.Length > 0
                ? $"試験中… {p.Done}/{p.Total}　{p.Name}"
                : $"試験中… {p.Done}/{p.Total}";
        });

        try
        {
            IReadOnlyList<CheckResult> results = await CheckRunner.RunAsync(
                items, proxies, TeamsEndpoints.Default, progress, _cts.Token);

            foreach (CheckResult result in results)
                Results.Add(result);

            ApplyToRows(results);
            Status = CheckReport.Summarize(results);
        }
        catch (OperationCanceledException)
        {
            Status = $"⊘ 中断しました（{_done}/{_total} 件まで実行）。";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "VerifyViewModel.RunAsync");
            Status = $"試験に失敗しました: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 行の結果欄にまとめを戻す。1 項目に複数のプロキシぶんの結果があるので、
    /// <b>1 つでも不合格があれば不合格</b>として、どのプロキシで落ちたかを添える。
    /// </summary>
    private void ApplyToRows(IReadOnlyList<CheckResult> results)
    {
        foreach (VerifyRowViewModel row in Rows)
        {
            string name = row.Name.Trim();
            if (name.Length == 0) continue;

            CheckResult[] mine = [.. results.Where(r => r.Name == name)];

            if (mine.Length == 0)
            {
                row.Status = "◌ 未実行";
                continue;
            }

            CheckResult[] failed = [.. mine.Where(r => r.IsFail)];

            if (failed.Length == 0)
            {
                row.Status = mine.Length > 1 ? $"○ 合格（{mine.Length} 通り）" : "○ 合格";
                continue;
            }

            row.Status = mine.Length > 1
                ? $"✕ {string.Join("・", failed.Select(f => f.ProxyText))} で不合格"
                : "✕ 不合格";
        }
    }

    private void Save()
    {
        if (Services.CsvExport.Save("verify", CheckReport.ToCsv([.. Results])) is { } message)
            Status = message;
    }

    /// <summary>
    /// 試験項目をファイルから読み込む。
    ///
    /// 終了時に settings.json へも覚えているが、それは<b>「前回の続き」</b>のためのもの。
    /// 現場ごと・案件ごとに項目一式を使い分けたり、人に渡したりするにはファイルが要る。
    /// </summary>
    private void LoadItems()
    {
        var dialog = new OpenFileDialog
        {
            Title = "試験項目を読み込む",
            Filter = "試験項目 (*.txt;*.csv)|*.txt;*.csv|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IReadOnlyList<CheckItem> items = CheckListParser.Parse(File.ReadAllText(dialog.FileName));

            if (items.Count == 0)
            {
                Status = $"{Path.GetFileName(dialog.FileName)} から読める項目がありませんでした。";
                return;
            }

            Rows.Clear();
            foreach (CheckItem item in items)
                AddRow(item);

            Status = $"{Path.GetFileName(dialog.FileName)} から {items.Count} 件を読み込みました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"読み込めませんでした: {ex.Message}";
        }
    }

    private void SaveItems()
    {
        var dialog = new SaveFileDialog
        {
            Title = "試験項目を保存する",
            FileName = $"試験項目-{DateTime.Now:yyyyMMdd}.txt",
            DefaultExt = "txt",
            Filter = "試験項目 (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // BOM 付き UTF-8。メモ帳でも Excel でも文字化けせずに開ける
            File.WriteAllText(dialog.FileName,
                              CheckListParser.Format(Rows.Select(r => r.ToItem())),
                              new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました（{Rows.Count} 件）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>アプリを閉じるときに呼ぶ。項目とプロキシの定義だけ覚える。</summary>
    public void SaveSettings()
    {
        try
        {
            Settings.Current.VerifyChecks = CheckListParser.Format(Rows.Select(r => r.ToItem()));
            Settings.Current.VerifyProxies = _proxyText;
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても試験そのものは行える
        }
    }

    /// <summary>全消去から呼ばれる。</summary>
    public void Reset()
    {
        _cts?.Cancel();
        Results.Clear();
        SaveCommand.RaiseCanExecuteChanged();

        foreach (VerifyRowViewModel row in Rows)
            row.Status = "◌ 未実行";

        Status = "ひな型を入れるか項目を書いて「まとめて実行」を押します。";
    }
}

/// <summary>ひな型 1 つ。</summary>
public sealed record CheckTemplate(string Name, string Text);
