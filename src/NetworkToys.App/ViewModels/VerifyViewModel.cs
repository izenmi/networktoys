using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Net;
using NetworkToys.Core.Verify;

namespace NetworkToys.App.ViewModels;

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
        CheckKind.Dns => "引きたい名前。空なら自分のホスト名を引きます",
        CheckKind.Teams => "空でかまいません（Teams の既定の宛先を使います）",
        CheckKind.Download => "測る URL（大きめのファイル）",
        CheckKind.Upload => "送り先の URL（末尾に |20 と書くと 20MB 送ります）",
        CheckKind.FastCom => "空でかまいません（fast.com で上り下りを測ります）",
        CheckKind.Manual => "ブラウザで開く URL",
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

    /// <summary>一覧に出す名前。PAC の URL は長いのでファイル名だけにする。</summary>
    public string Name => Choice.ShortName;

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
    private string _proxyText = "";
    private string _status = "ひな型を入れるか項目を書いて「まとめて実行」を押します。";
    private bool _isBusy;
    private int _done;
    private int _total;
    private CancellationTokenSource? _cts;

    public VerifyViewModel()
    {
        // コマンドを先に作る。SelectedTemplate の setter が
        // DeleteTemplateCommand を触るので、逆順にすると生成時に落ちる
        AddRowCommand = new RelayCommand(() => AddRow(new CheckItem("", CheckKind.Http, "")));
        RemoveRowCommand = new RelayCommand<VerifyRowViewModel>(RemoveRow);
        ApplyTemplateCommand = new RelayCommand(ApplyTemplate, () => !IsBusy);
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && Rows.Count > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SaveCommand = new RelayCommand(Save, () => Results.Count > 0);
        SaveHtmlCommand = new RelayCommand(SaveHtml, () => Results.Count > 0);
        LoadItemsCommand = new RelayCommand(LoadItems, () => !IsBusy);
        MarkPassCommand = new RelayCommand<CheckResult>(r => Mark(r, CheckVerdict.Pass));
        MarkFailCommand = new RelayCommand<CheckResult>(r => Mark(r, CheckVerdict.Fail));
        SaveItemsCommand = new RelayCommand(SaveItems, () => Rows.Count > 0);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => Rows.Count > 0);
        DeleteTemplateCommand = new RelayCommand(DeleteTemplate, () => SelectedTemplate.IsMine);

        RebuildTemplates();

        _proxyText = Settings.Current.VerifyProxies;

        foreach (CheckItem item in CheckListParser.Parse(Settings.Current.VerifyChecks))
            AddRow(item);

        RebuildProxies();
    }

    public ObservableCollection<VerifyRowViewModel> Rows { get; } = [];

    public ObservableCollection<ProxyChoiceViewModel> Proxies { get; } = [];

    public ObservableCollection<CheckResult> Results { get; } = [];

    /// <summary>ひな型の選択肢。組み込みのぶんと、自分で保存したぶん。</summary>
    public ObservableCollection<CheckTemplate> Templates { get; } = [];

    // 空のひな型で始める。RebuildTemplates() が組み込みのものへ差し替える
    private CheckTemplate _selectedTemplate = new("", "");

    public CheckTemplate SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
                DeleteTemplateCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand AddRowCommand { get; }
    public RelayCommand<VerifyRowViewModel> RemoveRowCommand { get; }
    public RelayCommand ApplyTemplateCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveCommand { get; }

    /// <summary>
    /// 試験結果を HTML の報告書にする。CSV は試験成績書へ貼るためのもので、
    /// <b>そのまま人に渡す・添付する形</b>は別に要る。
    /// </summary>
    public RelayCommand SaveHtmlCommand { get; }

    /// <summary>試験項目をファイルから読み込む。現場ごとに使い分けるため。</summary>
    public RelayCommand LoadItemsCommand { get; }

    /// <summary>試験項目をファイルへ保存する。</summary>
    public RelayCommand SaveItemsCommand { get; }

    /// <summary>
    /// いまの項目を、名前を付けてひな型として残す。
    /// 現場ごとに試す項目は決まっているので、毎回書き直させない。
    /// </summary>
    public RelayCommand SaveTemplateCommand { get; }

    /// <summary>自分で作ったひな型を消す。組み込みのものは消せない。</summary>
    public RelayCommand DeleteTemplateCommand { get; }

    /// <summary>ひな型に付ける名前を聞く。画面側が差し込む（VM から窓を開かないため）。</summary>
    public Func<string, string?>? AskTemplateName { get; set; }

    /// <summary>
    /// ひな型を消してよいかを聞く。画面が結線する。
    /// <b>結線前の既定は「いいえ」</b>（宛先リスト・ファイル転送と同じ決まり）。
    /// </summary>
    public Func<string, bool>? ConfirmDelete { get; set; }

    /// <summary>目視の項目に合格を付ける。</summary>
    public RelayCommand<CheckResult> MarkPassCommand { get; }

    /// <summary>目視の項目に不合格を付ける。</summary>
    public RelayCommand<CheckResult> MarkFailCommand { get; }

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

    public string Status { get => _status; internal set => SetProperty(ref _status, value); }

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

    // ===== 進み具合 =====
    //
    // 項目数 × プロキシの本数だけ逐次に回すので、まとめて実行すると何分もかかる。
    // 「押したのに何も起きない」に見えないよう、いま何をしているかと、
    // 済んだところまでの合否を常に出しておく。

    /// <summary>進捗の帯を出すか。一度でも走らせたら出したままにする（結果の内訳が読める）。</summary>
    public bool HasProgress => _total > 0;

    /// <summary>0〜100。帯の長さ。</summary>
    public double ProgressPercent => _total > 0 ? _done * 100.0 / _total : 0;

    public string ProgressText => _total > 0 ? $"{_done} / {_total}" : "";

    public int PassCount => Results.Count(r => r.IsPass);
    public int FailCount => Results.Count(r => r.IsFail);
    public int WarnCount => Results.Count(r => r.IsWarn);

    /// <summary>人の判定を待っている件数。<b>0 になるまで試験は終わっていない。</b></summary>
    public int PersonCount => Results.Count(r => r.NeedsPerson);

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(PassCount));
        OnPropertyChanged(nameof(FailCount));
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(PersonCount));
    }

    private void AddRow(CheckItem item)
    {
        Rows.Add(new VerifyRowViewModel(item));
        StartCommand.RaiseCanExecuteChanged();
        SaveItemsCommand.RaiseCanExecuteChanged();
        SaveTemplateCommand.RaiseCanExecuteChanged();
    }

    private void RemoveRow(VerifyRowViewModel? row)
    {
        if (row is null) return;

        Rows.Remove(row);
        StartCommand.RaiseCanExecuteChanged();
        SaveItemsCommand.RaiseCanExecuteChanged();
        SaveTemplateCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 選択肢を組み直す。<b>組み込みが先、自分のものが後。</b>
    /// 同じ名前なら自分のものだけを残す（自分で作ったものが勝つ方が驚きが少ない）。
    /// </summary>
    private void RebuildTemplates()
    {
        string? keep = Templates.Count > 0 ? SelectedTemplate.Name : null;

        Templates.Clear();

        Dictionary<string, string> mine = Settings.Current.VerifyTemplates;

        foreach ((string name, string text) in RecommendedChecks.Templates)
        {
            if (!mine.ContainsKey(name))
                Templates.Add(new CheckTemplate(name, text));
        }

        foreach ((string name, string text) in mine.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            Templates.Add(new CheckTemplate(name, text, IsMine: true));

        if (keep is not null && Templates.FirstOrDefault(t => t.Name == keep) is { } same)
            SelectedTemplate = same;
        else if (Templates.Count > 0)
            SelectedTemplate = Templates[0];
    }

    /// <summary>
    /// いまの項目をひな型として残す。
    ///
    /// 名前を聞くのは画面側（<see cref="AskTemplateName"/>）。VM から窓を開くと
    /// 自己診断でここを通せなくなる。
    /// </summary>
    private void SaveTemplate()
    {
        string suggested = SelectedTemplate.IsMine ? SelectedTemplate.Name : "";

        if (AskTemplateName?.Invoke(suggested) is not { } name) return;

        name = name.Trim();
        if (name.Length == 0)
        {
            Status = "ひな型の名前が空です。";
            return;
        }

        SaveTemplate(name, CheckListParser.Format(Rows.Select(r => r.ToItem())));
    }

    /// <summary>名前と中身を決めて残す。自己診断からも呼べるように分けてある。</summary>
    internal void SaveTemplate(string name, string text)
    {
        Settings.Current.VerifyTemplates[name] = text;

        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"ひな型を残せませんでした: {ex.Message}";
            return;
        }

        RebuildTemplates();

        if (Templates.FirstOrDefault(t => t.Name == name) is { } saved)
            SelectedTemplate = saved;

        Status = $"「{name}」をひな型に残しました（{Rows.Count} 件）。";
    }

    /// <summary>自分で作ったひな型を消す。組み込みのものには触らない。</summary>
    private void DeleteTemplate()
    {
        CheckTemplate target = SelectedTemplate;

        if (!target.IsMine)
        {
            Status = "組み込みのひな型は消せません。";
            return;
        }

        // 消すのは取り消せない。必ず聞く（2026-08-18 ユーザー指示。宛先リストと同じ扱い）。
        // 結線前の既定は「いいえ」— 聞けないなら消さない方が安全
        if (ConfirmDelete?.Invoke($"ひな型「{target.Name}」を消します。\n\nいまの試験項目は残ります。") != true)
            return;

        Settings.Current.VerifyTemplates.Remove(target.Name);

        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"消せませんでした: {ex.Message}";
            return;
        }

        RebuildTemplates();
        Status = $"ひな型「{target.Name}」を消しました。";
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

        Proxies.Clear();

        // 初回は 1 つも選ばない（2026-08-16 ユーザー指示）。
        // 選ばれていなければ「直接」だけで 1 周するので、押せば必ず何かは動く
        foreach (ProxyChoice choice in ProxyListParser.Parse(_proxyText))
            Proxies.Add(new ProxyChoiceViewModel(choice, selected.Contains(choice.Name)));
    }

    private async Task RunAsync()
    {
        IReadOnlyList<CheckItem> all = [.. Rows.Select(r => r.ToItem()).Where(i => i.Name.Length > 0)];

        // 目視の項目は、プロキシを切り替えながら 1 件ずつ回す（下の StartBrowserQueue）。
        // まとめて開くと、どのプロキシで見た画面なのか分からなくなる（2026-08-18 ユーザー指示）
        IReadOnlyList<CheckItem> items = [.. all.Where(i => i.Kind != CheckKind.Manual)];
        IReadOnlyList<CheckItem> manual = [.. all.Where(i => i.Kind == CheckKind.Manual)];

        if (all.Count == 0)
        {
            Status = "項目名の入っている行がありません。";
            return;
        }

        IReadOnlyList<ProxyChoice> proxies =
            [.. Proxies.Where(p => p.IsSelected).Select(p => p.Choice)];

        IsBusy = true;
        Results.Clear();
        _done = 0;
        _total = 0;
        SaveCommand.RaiseCanExecuteChanged();
        SaveHtmlCommand.RaiseCanExecuteChanged();
        RaiseProgress();

        foreach (VerifyRowViewModel row in Rows)
            row.Status = "◌ 待機";

        _cts = new CancellationTokenSource();

        // これから試す項目。Progress は UI スレッドに戻してくれるので、
        // ここから画面の値を直に触ってよい
        var progress = new Progress<(int Done, int Total, string Name)>(p =>
        {
            _done = p.Done;
            _total = p.Total;

            MarkRunning(p.Name);
            RaiseProgress();

            Status = p.Name.Length > 0
                ? $"試験中… {p.Done}/{p.Total}　{p.Name}"
                : $"試験中… {p.Done}/{p.Total}";
        });

        // 1 件終わるごとに一覧へ足す。全部終わるまで真っ白では、
        // 長い試験のあいだ「動いているのか」すら分からない
        var finished = new Progress<CheckResult>(result =>
        {
            Results.Add(result);
            ApplyToRow(result.Name);
            RaiseProgress();
        });

        try
        {
            if (items.Count > 0)
            {
                await CheckRunner.RunAsync(
                    items, proxies, TeamsEndpoints.Default, progress, _cts.Token, finished);
            }

            Status = CheckReport.Summarize([.. Results]);

            StartBrowserQueue(manual, proxies);
        }
        catch (OperationCanceledException)
        {

            // 中断しても、そこまでの結果は残す（消すと試験をやり直すことになる）
            foreach (VerifyRowViewModel row in Rows)
            {
                if (row.Status is "◌ 待機" or "▶ 試験中") row.Status = "◌ 未実行";
            }

            Status = $"⊘ 中断しました（{_done}/{_total} 件まで実行）。そこまでの結果は残してあります。";
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
            SaveHtmlCommand.RaiseCanExecuteChanged();
            RaiseProgress();
        }
    }

    /// <summary>
    /// 1 項目だけ、指定したプロキシで試す（2026-08-18 ユーザー指示）。
    ///
    /// 手で確かめるときは<b>「この 1 件を、このプロキシで」</b>が要る。
    /// まとめて実行は全項目 × 選んだプロキシぶん回るので、切り分けには重い。
    /// <b>結果はいつもの一覧に足す</b>（別扱いにすると証跡が 2 か所に散る）。
    /// </summary>
    internal async Task RunOneAsync(VerifyRowViewModel row, ProxyChoice proxy)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(proxy);

        if (IsBusy) return;

        CheckItem item = row.ToItem();

        if (item.Name.Length == 0)
        {
            Status = "項目名が空です。";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();

        row.Status = "▶ 試験中";

        var finished = new Progress<CheckResult>(result =>
        {
            Results.Add(result);
            ApplyToRow(result.Name);
            RaiseProgress();
        });

        try
        {
            await CheckRunner.RunAsync(
                [item], [proxy], TeamsEndpoints.Default, null, _cts.Token, finished);

            Status = item.UsesProxy
                ? $"「{item.Name}」を {proxy.Name} で試しました。"
                : $"「{item.Name}」を試しました（この種類はプロキシを通りません）。";
        }
        catch (OperationCanceledException)
        {
            if (row.Status == "▶ 試験中") row.Status = "◌ 未実行";

            Status = "⊘ 中断しました。";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "VerifyViewModel.RunOneAsync");
            Status = $"試験に失敗しました: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            SaveCommand.RaiseCanExecuteChanged();
            SaveHtmlCommand.RaiseCanExecuteChanged();
            RaiseProgress();
        }
    }

    /// <summary>いま試している行に印を付ける。まだ結果の出ていない行だけに触る。</summary>
    private void MarkRunning(string name)
    {
        foreach (VerifyRowViewModel row in Rows)
        {
            if (row.Status == "▶ 試験中") row.Status = "◌ 待機";
        }

        if (name.Length == 0) return;

        foreach (VerifyRowViewModel row in Rows)
        {
            if (row.Name.Trim() == name && row.Status == "◌ 待機")
                row.Status = "▶ 試験中";
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
            if (Describe(row.Name.Trim(), results) is { } status)
                row.Status = status;
        }
    }

    /// <summary>1 項目ぶんだけ結果欄を書き換える。試験中に済んだところから見せるため。</summary>
    private void ApplyToRow(string name)
    {
        if (Describe(name.Trim(), [.. Results]) is not { } status) return;

        foreach (VerifyRowViewModel row in Rows)
        {
            if (row.Name.Trim() == name.Trim()) row.Status = status;
        }
    }

    /// <summary>
    /// その項目の結果欄に出す文字。<b>まだ結果が 1 件も無いなら null</b>
    /// （「待機」「試験中」の表示を消してしまわないため）。
    /// </summary>
    private static string? Describe(string name, IReadOnlyList<CheckResult> results)
    {
        if (name.Length == 0) return null;

        CheckResult[] mine = [.. results.Where(r => r.Name == name)];

        if (mine.Length == 0) return null;

        if (mine.Any(r => r.NeedsPerson)) return "◍ 目視で確認";

        CheckResult[] failed = [.. mine.Where(r => r.IsFail)];

        if (failed.Length > 0)
        {
            return mine.Length > 1
                ? $"✕ {string.Join("・", failed.Select(f => f.ProxyText))} で不合格"
                : "✕ 不合格";
        }

        if (mine.Any(r => r.IsWarn)) return "△ 注意";

        return mine.Length > 1 ? $"○ 合格（{mine.Length} 通り）" : "○ 合格";
    }

    /// <summary>
    /// ブラウザで開いた項目に、人が合否を付ける。
    /// <b>結果の行を差し替える</b>ので、CSV にもそのまま反映される。
    /// </summary>
    private void Mark(CheckResult? result, CheckVerdict verdict)
    {
        if (result is null) return;

        int at = Results.IndexOf(result);
        if (at < 0) return;

        string mark = verdict == CheckVerdict.Pass ? "目視で確認しました" : "目視で問題を確認しました";

        Results[at] = result with { Verdict = verdict, Detail = $"{mark}（{result.Detail}）" };

        ApplyToRows([.. Results]);
        RaiseProgress();
        Status = CheckReport.Summarize([.. Results]);

        // 判定が入ったら、次のプロキシに切り替えて次の 1 件を開く
        OpenNextForPerson();
    }

    // ===== 目視の項目（プロキシを切り替えながら 1 件ずつ） =====

    /// <summary>まだ開いていない「目視の項目 × プロキシ」。</summary>
    private readonly Queue<(CheckItem Item, ProxyChoice Proxy)> _browserQueue = new();

    /// <summary>切り替える前の Windows のプロキシ設定。<b>終わったら必ず戻す。</b></summary>
    private ProxyState? _originalProxy;

    /// <summary>
    /// 目視の項目を順番に開く。
    ///
    /// <b>ブラウザは Windows のプロキシ設定に従う</b>ので、プロキシごとに見るには
    /// その設定を切り替えるしかない（2026-08-18 ユーザー指示）。
    /// <b>切り替えたら必ず元に戻す</b> — 戻し忘れは、ほかのアプリまで巻き込む事故になる。
    /// </summary>
    private void StartBrowserQueue(IReadOnlyList<CheckItem> manual, IReadOnlyList<ProxyChoice> proxies)
    {
        _browserQueue.Clear();

        if (manual.Count == 0) return;

        // 1 つも選ばれていなければ、いまの設定のまま 1 周だけ
        IReadOnlyList<ProxyChoice> targets = proxies.Count > 0 ? proxies : [ProxyChoice.System];

        foreach (CheckItem item in manual)
        {
            foreach (ProxyChoice proxy in targets) _browserQueue.Enqueue((item, proxy));
        }

        OpenNextForPerson();
    }

    /// <summary>次の 1 件を開く。無ければ Windows の設定を元に戻す。</summary>
    private void OpenNextForPerson()
    {
        if (_browserQueue.Count == 0)
        {
            RestoreProxy();
            return;
        }

        // まだ判定していない目視の項目が残っているなら、そちらが先
        if (Results.Any(r => r.NeedsPerson)) return;

        (CheckItem item, ProxyChoice proxy) = _browserQueue.Dequeue();

        string? failure = UseProxy(proxy);

        CheckResult result = CheckRunner.OpenForPerson(item, proxy, failure);

        Results.Add(result);
        ApplyToRows([.. Results]);
        RaiseProgress();

        Status = $"「{item.Name}」を {proxy.ShortName} で開きました。見て ○ か ✕ を押してください。";
    }

    /// <summary>
    /// ブラウザが従う設定（WinINET）を、そのプロキシに切り替える。
    /// <b>「Windows のプロキシ設定」を選んだときは触らない</b>（いまの設定で見る、の意味）。
    /// </summary>
    private string? UseProxy(ProxyChoice proxy)
    {
        if (proxy.Mode == NetworkToys.Core.Verify.ProxyMode.System) return null;

        _originalProxy ??= ProxySettings.Read();

        ProxyPlan plan = proxy.Mode switch
        {
            NetworkToys.Core.Verify.ProxyMode.Pac => new ProxyPlan(NetworkToys.Core.Net.ProxyMode.Pac, proxy.Address, "", ""),
            NetworkToys.Core.Verify.ProxyMode.Fixed => new ProxyPlan(
                NetworkToys.Core.Net.ProxyMode.Fixed, "", StripScheme(proxy.Address), ""),
            _ => new ProxyPlan(NetworkToys.Core.Net.ProxyMode.None, "", "", ""),
        };

        string? error = ProxySettings.Apply(plan);

        Notice = error is null
            ? $"⚠ ブラウザで見るために、Windows のプロキシ設定を「{proxy.ShortName}」に変えています。"
              + "すべての判定が終わると元に戻します。"
            : $"⚠ Windows のプロキシ設定を変えられませんでした: {error}";

        return error;
    }

    /// <summary>切り替える前の設定へ戻す。<b>戻せなければ、そう言う。</b></summary>
    private void RestoreProxy()
    {
        if (_originalProxy is not { } original) return;

        _originalProxy = null;

        ProxyPlan plan = original.Mode switch
        {
            NetworkToys.Core.Net.ProxyMode.Pac => new ProxyPlan(NetworkToys.Core.Net.ProxyMode.Pac, original.PacUrl, "", ""),
            NetworkToys.Core.Net.ProxyMode.Fixed => new ProxyPlan(
                NetworkToys.Core.Net.ProxyMode.Fixed, "", original.Server, original.Bypass),
            _ => new ProxyPlan(NetworkToys.Core.Net.ProxyMode.None, "", "", ""),
        };

        Notice = ProxySettings.Apply(plan) is { } error
            ? $"⚠ Windows のプロキシ設定を元に戻せませんでした（{error}）。IP設定タブで確かめてください。"
            : $"Windows のプロキシ設定を元（{original.Summary}）に戻しました。";
    }

    /// <summary>WinINET の ProxyServer は <c>host:port</c>。<c>http://</c> は付けない。</summary>
    private static string StripScheme(string address)
    {
        int at = address.IndexOf("://", StringComparison.Ordinal);

        return at < 0 ? address : address[(at + 3)..].TrimEnd('/');
    }

    private void Save()
    {
        if (Services.CsvExport.Save("verify", CheckReport.ToCsv([.. Results])) is { } message)
            Status = message;
    }

    /// <summary>
    /// 試験の結果を HTML の報告書にする。
    ///
    /// <b>1 ファイル完結</b>なのでそのままメールに添付でき、客先のオフライン環境でも開ける
    /// （記録タブの書き出しと同じ作り）。試したプロキシの一覧も証跡として載せる。
    /// </summary>
    private void SaveHtml()
    {
        var dialog = new SaveFileDialog
        {
            Title = "試験結果を HTML で保存する",
            FileName = $"業務確認試験-{DateTime.Now:yyyyMMdd-HHmm}.html",
            DefaultExt = "html",
            Filter = "HTML レポート (*.html)|*.html|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            ReportService.SaveHtml(dialog.FileName, BuildReport());

            Status = $"{Path.GetFileName(dialog.FileName)} に書き出しました（{Results.Count} 件）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>試験だけの報告書。記録タブから呼ばれるときは測定結果と合わさる。</summary>
    internal Core.Reporting.ReportData BuildReport()
    {
        var environment = new List<(string, string)>
        {
            ("試験した端末", Environment.MachineName),
            ("試験した人", Environment.UserName),
        };

        string tried = string.Join("・", Proxies.Where(p => p.IsSelected).Select(p => p.Name));

        if (tried.Length > 0)
            environment.Add(("試したプロキシ", tried));

        return new Core.Reporting.ReportData(
            "業務確認試験",
            DateTime.Now,
            Note: "",
            StartedAt: null,
            IntervalMs: 0,
            environment,
            Rows: [],
            Checks: [.. Results]);
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
            LoadItemsFrom(File.ReadAllText(dialog.FileName), Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"読み込めませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// テキストから項目を読み込む。<b>置き換える</b>（足すのではない）。
    /// ファイル選択からも、欄への放り込みからも、ここを通る。
    /// </summary>
    public void LoadItemsFrom(string text, string source)
    {
        IReadOnlyList<CheckItem> items = CheckListParser.Parse(text);

        if (items.Count == 0)
        {
            Status = $"{source} から読める項目がありませんでした。";
            return;
        }

        Rows.Clear();
        foreach (CheckItem item in items)
            AddRow(item);

        Status = $"{source} から {items.Count} 件を読み込みました。";
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
        // 目視の途中で片付けられても、Windows の設定は必ず戻す
        _browserQueue.Clear();
        RestoreProxy();

        _cts?.Cancel();
        Results.Clear();
        _done = 0;
        _total = 0;
        SaveCommand.RaiseCanExecuteChanged();
        SaveHtmlCommand.RaiseCanExecuteChanged();
        RaiseProgress();

        foreach (VerifyRowViewModel row in Rows)
            row.Status = "◌ 未実行";

        Status = "ひな型を入れるか項目を書いて「まとめて実行」を押します。";
    }
}

/// <summary>
/// ひな型 1 つ。
/// </summary>
/// <param name="IsMine">
/// 自分で作ったものか。<b>組み込みのひな型は消せない</b>ので、その出し分けに使う。
/// </param>
public sealed record CheckTemplate(string Name, string Text, bool IsMine = false)
{
    /// <summary>コンボに出す文字。自分のものは印を付けて見分けられるようにする。</summary>
    public string Label => IsMine ? $"★ {Name}" : Name;
}
