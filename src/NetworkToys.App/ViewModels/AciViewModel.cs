using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Fabric;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Terminal;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>ポートを引く相手。<c>Dn</c> は問い合わせの絞り込みに使う形（末尾が <c>/sys</c>）。</summary>
public sealed record AciNodeItem(string Name, string Dn);

/// <summary>ログの種別。</summary>
public sealed record AciLogKind(string Name, string ClassName);

/// <summary>構成の種別。1 種別につき複数のクラスを順に引く。</summary>
public sealed record AciConfigKind(string Name, string Kind, string[] Classes);

/// <summary>
/// Cisco ACI（APIC）を見に行くタブ。<b>読み取り専用</b>で、設定を書く口は持たない。
///
/// 通信は <see cref="ApicClient"/>、応答の解釈は Core の <see cref="AciCatalog"/>。
/// ここは画面の状態と、取得の段取りだけを持つ。
///
/// <b>タブを選んだだけでは 1 バイトも出さない</b>（<c>OnActivated</c> を持たない）。
/// CI の Windows ランナーは外に出られるので、選んだだけで通信する作りにすると
/// 自己診断から本番の APIC へ要求が飛ぶ。
/// </summary>
public sealed class AciViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 練習用に使える公開の APIC。Cisco DevNet の常時稼働サンドボックス。
    /// <b>資格情報は埋め込まない</b>（DevNet のページで確認してもらう。こちらで転記すると腐る）。
    /// </summary>
    private const string SandboxHost = "sandboxapicdc.cisco.com";

    private const string Ready = "APIC のアドレスとユーザーを入れて「取得」を押すと、ヘルスと Faults を取ってきます。";

    private readonly HttpMessageHandler? _handler;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<AciEpgMemberRow> _allMembers = [];

    private string _host = "";
    private string _userName = "";
    private string _password = "";
    private string _fingerprint = "";
    private string _status = Ready;
    private string _notice = "";
    private string _fabricHealth = "—";
    private bool _isBusy;
    private AciNodeItem? _selectedNode;
    private AciEpgRow? _selectedEpg;
    private string? _selectedTenant;
    private string _before = "";
    private string _after = "";
    private string _beforeLabel = "まだ取っていません";
    private string _afterLabel = "まだ取っていません";
    private string _diffHeadline = "";
    private bool _onlyDifferences;
    private AciLogKind _selectedLogKind;
    private AciConfigKind _selectedConfigKind;
    private string _lastResponse = "";
    private bool _showConnection = true;

    /// <param name="handler">自己診断が偽の APIC を挿すための口。既定は本物。</param>
    public AciViewModel(HttpMessageHandler? handler = null)
    {
        _handler = handler;

        LogKinds =
        [
            new AciLogKind("Faults の履歴", "faultRecord"),
            new AciLogKind("イベント", "eventRecord"),
        ];
        _selectedLogKind = LogKinds[0];

        ConfigKinds =
        [
            new AciConfigKind("テナント構成", "tenant", ["fvTenant", "fvCtx", "fvBD", "fvAEPg", "vzBrCP"]),
            new AciConfigKind("ファブリック構成", "fabric", ["fabricNode"]),
            new AciConfigKind("インターフェース関連ポリシー", "interface",
                ["infraAttEntityP", "physDomP", "fvnsVlanInstP", "infraAccPortGrp"]),
            new AciConfigKind("設定バックアップ", "backup", ["configSnapshot", "configJob"]),
        ];
        _selectedConfigKind = ConfigKinds[0];

        FetchCommand = new RelayCommand(() => _ = FetchBasicsAsync(), CanFetch);
        FetchPortsCommand = new RelayCommand(() => _ = FetchPortsAsync(), () => CanFetch() && SelectedNode is not null);
        FetchEpgsCommand = new RelayCommand(() => _ = FetchEpgsAsync(), CanFetch);
        FetchEndpointsCommand = new RelayCommand(() => _ = FetchEndpointsAsync(), CanFetch);
        FetchLogCommand = new RelayCommand(() => _ = FetchLogAsync(), CanFetch);
        FetchConfigCommand = new RelayCommand(() => _ = FetchConfigAsync(), CanFetch);
        FetchBeforeCommand = new RelayCommand(
            () => _ = FetchTenantConfigAsync(before: true),
            () => CanFetch() && SelectedTenant is { Length: > 0 });

        FetchAfterCommand = new RelayCommand(
            () => _ = FetchTenantConfigAsync(before: false),
            () => CanFetch() && SelectedTenant is { Length: > 0 });

        CompareCommand = new RelayCommand(Compare, () => _before.Length > 0 && _after.Length > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);

        SaveCsvCommand = new RelayCommand<string>(key => Save(key, xlsx: false), key => RowCount(key) > 0);
        SaveXlsxCommand = new RelayCommand<string>(key => Save(key, xlsx: true), key => RowCount(key) > 0);

        // 前に繋いだ相手を出しておく。無ければ練習用の公開 APIC
        _host = HostHistory().FirstOrDefault() ?? SandboxHost;
        _userName = RememberedUser(_host);
    }

    // ===== 一覧 =====

    public ObservableCollection<AciHealthRow> HealthRows { get; } = [];
    public ObservableCollection<AciFaultRow> FaultRows { get; } = [];
    public ObservableCollection<AciLogRow> LogRows { get; } = [];
    public ObservableCollection<AciPortRow> PortRows { get; } = [];
    public ObservableCollection<AciEpgRow> EpgRows { get; } = [];
    public ObservableCollection<AciEpgMemberRow> MemberRows { get; } = [];
    public ObservableCollection<AciEndpointRow> EndpointRows { get; } = [];
    public ObservableCollection<AciConfigRow> ConfigRows { get; } = [];

    /// <summary>作業前後の差分（左右に並べた行）。</summary>
    public ObservableCollection<SideBySideRow> DiffRows { get; } = [];

    public ObservableCollection<AciNodeItem> Nodes { get; } = [];

    /// <summary>テナントの名前。「取得」を押したときに埋まる（ノードと同じ扱い）。</summary>
    public ObservableCollection<string> Tenants { get; } = [];

    public AciLogKind[] LogKinds { get; }
    public AciConfigKind[] ConfigKinds { get; }

    // ===== コマンド =====

    public RelayCommand FetchCommand { get; }
    public RelayCommand FetchPortsCommand { get; }
    public RelayCommand FetchEpgsCommand { get; }
    public RelayCommand FetchEndpointsCommand { get; }
    public RelayCommand FetchLogCommand { get; }
    public RelayCommand FetchConfigCommand { get; }

    /// <summary>選んだテナントの設定を「作業前」として取る。</summary>
    public RelayCommand FetchBeforeCommand { get; }

    /// <summary>選んだテナントの設定を「作業後」として取る。</summary>
    public RelayCommand FetchAfterCommand { get; }

    /// <summary>取った 2 つを見比べる。</summary>
    public RelayCommand CompareCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand<string> SaveCsvCommand { get; }
    public RelayCommand<string> SaveXlsxCommand { get; }

    /// <summary>直前に取れた生の応答を見せる。属性名の思い違いを実機で 1 分で切り分けるための逃げ道。</summary>
    public string LastResponse => _lastResponse;

    /// <summary>
    /// 証明書の指紋を見せて受け入れるかを聞く。画面が結線する。
    /// <b>結線前の既定は「いいえ」</b>（黙って受け入れるより、何も起きない方がよい）。
    /// </summary>
    public Func<string, bool> ConfirmFingerprint { get; set; } = _ => false;

    /// <summary>画面の <c>PasswordBox</c> を空にしてもらう合図（VM からは中身を書けない）。</summary>
    public event EventHandler? PasswordCleared;

    /// <summary>
    /// 接続の欄を開いているか。<b>取得できたら畳む</b> — 一覧に画面を使いたいので。
    /// もう一度出したいときは「接続先」を押す。
    /// </summary>
    public bool ShowConnection
    {
        get => _showConnection;
        private set
        {
            if (SetProperty(ref _showConnection, value)) OnPropertyChanged(nameof(ConnectionToggleText));
        }
    }

    /// <summary>畳んでいる間も、どこへ何で繋いだかは 1 行で見えるようにしておく。</summary>
    public string ConnectionSummary => Host.Trim().Length > 0 ? $"{Host}／{UserName}" : "未接続";

    public string ConnectionToggleText => ShowConnection ? "接続先 ▴" : "接続先 ▾";

    public RelayCommand ToggleConnectionCommand { get; }

    /// <summary>取得できたら接続の欄を畳む。押せば戻る。</summary>
    private void Collapse()
    {
        ShowConnection = false;
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    // ===== 入力 =====

    public string Host
    {
        get => _host;
        set
        {
            if (!SetProperty(ref _host, value)) return;

            RefreshFetchCommands();

            // 前にこのホストで使ったユーザー名を思い出す（パスワードは覚えない）
            if (RememberedUser(value) is { Length: > 0 } user) UserName = user;
        }
    }

    public string UserName
    {
        get => _userName;
        set { if (SetProperty(ref _userName, value)) RefreshFetchCommands(); }
    }

    /// <summary><b>どこにも保存しない。</b>画面の PasswordBox から流し込まれるだけ。</summary>
    public string Password
    {
        get => _password;
        set { if (SetProperty(ref _password, value)) RefreshFetchCommands(); }
    }

    /// <summary>相手が出した証明書の指紋。見比べられるよう画面に出しっぱなしにする。</summary>
    public string Fingerprint
    {
        get => _fingerprint;
        private set => SetProperty(ref _fingerprint, value);
    }

    public AciNodeItem? SelectedNode
    {
        get => _selectedNode;
        set { if (SetProperty(ref _selectedNode, value)) FetchPortsCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>設定を書き出すテナント。「取得」で埋まった一覧から選ぶ。</summary>
    public string? SelectedTenant
    {
        get => _selectedTenant;
        set
        {
            if (!SetProperty(ref _selectedTenant, value)) return;

            FetchBeforeCommand.RaiseCanExecuteChanged();
            FetchAfterCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>見比べる 2 つ。<b>いつ・どのテナントを取ったか</b>を見出しに出す。</summary>
    public string BeforeLabel => _beforeLabel;

    public string AfterLabel => _afterLabel;

    /// <summary>差分の要約（違いの数）。</summary>
    public string DiffHeadline
    {
        get => _diffHeadline;
        private set => SetProperty(ref _diffHeadline, value);
    }

    /// <summary>違う行だけを出すか。切り替えたら、取り直さずに並べ直す。</summary>
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set { if (SetProperty(ref _onlyDifferences, value) && DiffRows.Count > 0) Compare(); }
    }

    /// <summary>選ばれた EPG。メンバー一覧はこれで絞る（取得のたびに引き直さない）。</summary>
    public AciEpgRow? SelectedEpg
    {
        get => _selectedEpg;
        set { if (SetProperty(ref _selectedEpg, value)) ShowMembers(); }
    }

    public AciLogKind SelectedLogKind
    {
        get => _selectedLogKind;
        set { if (value is not null) SetProperty(ref _selectedLogKind, value); }
    }

    public AciConfigKind SelectedConfigKind
    {
        get => _selectedConfigKind;
        set { if (value is not null) SetProperty(ref _selectedConfigKind, value); }
    }

    // ===== 状態 =====

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>打ち切りなど、消えては困る断り書き。</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice.Length > 0;

    /// <summary>ファブリック全体のヘルス。上段に大きく出す。</summary>
    public string FabricHealth
    {
        get => _fabricHealth;
        private set => SetProperty(ref _fabricHealth, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            RefreshFetchCommands();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    // ===== 取得 =====

    /// <summary>ヘルスと Faults、それにポートで使うノードの一覧。「取得」で走る。</summary>
    private Task FetchBasicsAsync() => RunAsync("ヘルスと Faults", async (client, token) =>
    {
        IReadOnlyList<AciMo> total = await FetchClassAsync(client, "fabricHealthTotal", null, null, token);

        // ノードは fabricNode ではなく topSystem から取る。
        // スイッチのヘルスは fabricNode ではなく sys（＝topSystem）にぶら下がっており、
        // topSystem の dn はそのままポートの絞り込みに使える形（…/sys）でもある
        IReadOnlyList<AciMo> nodes = await FetchClassAsync(client, "topSystem", Health, null, token);
        IReadOnlyList<AciMo> tenants = await FetchClassAsync(client, "fvTenant", Health, null, token);

        var health = new List<AciHealthRow>();
        health.AddRange(AciCatalog.ParseHealth("ファブリック", total));
        health.AddRange(AciCatalog.ParseHealth("ノード", nodes));
        health.AddRange(AciCatalog.ParseHealth("テナント", tenants));
        Replace(HealthRows, health);

        FabricHealth = health.FirstOrDefault(r => r.Kind == "ファブリック") is { } fabric
            ? $"{fabric.ScoreText}　{fabric.State}"
            : "—";

        SyncNodes(nodes);
        SyncTenants(tenants);

        IReadOnlyList<AciMo> faults =
            await FetchClassAsync(client, "faultInst", AciCatalog.FaultFilter, null, token);

        // 重い順に並べる。表示文字列から戻すのではなく、元の値のまま並べてから行にする
        Replace(FaultRows, AciCatalog.ParseFaults(
            faults.OrderBy(m => AciCatalog.SeverityRank(m["severity"]))));

        Status = $"ヘルス {HealthRows.Count} 件 / Faults {FaultRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchPortsAsync() => RunAsync("ポート", async (client, token) =>
    {
        if (SelectedNode is not { } node) return;

        IReadOnlyList<AciMo> mos =
            await FetchClassAsync(client, "l1PhysIf", Children, node.Dn, token);

        // 束(ポートチャネル)。子の pcRsMbrIfs がメンバーの物理インターフェースを指す
        IReadOnlyList<AciMo> bundles =
            await FetchClassAsync(client, "pcAggrIf", Children, node.Dn, token);

        // 「どの口にどの EPG と VLAN が載っているか」は EPG 側の静的パスが持っている
        IReadOnlyList<AciMo> epgs = await FetchClassAsync(client, "fvAEPg", Children, null, token);
        _allMembers = AciCatalog.ParseEpgMembers(epgs);

        Replace(PortRows, AciCatalog.ParsePorts(mos, _allMembers, bundles));

        int assigned = PortRows.Count(p => p.Epgs.Length > 0);

        Status = $"{node.Name}: ポート {PortRows.Count} 件"
                 + (assigned > 0 ? $"（EPG の割り当てあり {assigned} 件）" : "")
                 + $"（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchEpgsAsync() => RunAsync("EPG", async (client, token) =>
    {
        IReadOnlyList<AciMo> mos = await FetchClassAsync(client, "fvAEPg", Children, null, token);

        Replace(EpgRows, AciCatalog.ParseEpgs(mos));
        _allMembers = AciCatalog.ParseEpgMembers(mos);

        SelectedEpg = EpgRows.FirstOrDefault();
        ShowMembers();

        Status = $"EPG {EpgRows.Count} 件 / 静的パス {_allMembers.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchEndpointsAsync() => RunAsync("エンドポイント", async (client, token) =>
    {
        IReadOnlyList<AciMo> mos = await FetchClassAsync(client, "fvCEp", Children, null, token);

        Replace(EndpointRows, AciCatalog.ParseEndpoints(mos));
        Status = $"エンドポイント {EndpointRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchLogAsync() => RunAsync("ログ", async (client, token) =>
    {
        AciLogKind kind = SelectedLogKind;
        IReadOnlyList<AciMo> mos = await FetchClassAsync(client, kind.ClassName, null, null, token);

        Replace(LogRows, [.. AciCatalog.ParseLog(kind.Name, mos).OrderByDescending(r => r.Time)]);
        Status = $"{kind.Name} {LogRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchConfigAsync() => RunAsync("構成", async (client, token) =>
    {
        AciConfigKind kind = SelectedConfigKind;
        var mos = new List<AciMo>();

        foreach (string className in kind.Classes)
            mos.AddRange(await FetchClassAsync(client, className, ChildrenFor(className), null, token));

        IReadOnlyList<AciConfigRow> rows = kind.Kind switch
        {
            "tenant" => AciCatalog.ParseTenantConfig(mos),
            "fabric" => AciCatalog.ParseFabricConfig(mos),
            "interface" => AciCatalog.ParseInterfacePolicy(mos),
            _ => AciCatalog.ParseBackupConfig(mos),
        };

        Replace(ConfigRows, rows);
        Status = $"{kind.Name} {ConfigRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private const string Children = "rsp-subtree=children";

    /// <summary>
    /// ヘルスを子として付けて引く。
    ///
    /// <b><c>required</c> を付けないこと。</b>あれは「ヘルスを持つものだけ返す」という意味で、
    /// ヘルスの付いていないクラスに使うと<b>1 件も返らない</b>。
    /// 実際 <c>fabricNode</c> に付けてノードが 1 台も出ず、ポートの画面が使えなかった
    /// （2026-08-17 に実機で発覚）。持っていないものは「—」で出す方がよい。
    /// </summary>
    private const string Health = "rsp-subtree-include=health";

    /// <summary>VLAN プールだけは範囲が子に入っているので、そこだけ子を引く。</summary>
    private static string? ChildrenFor(string className) => className == "fvnsVlanInstP" ? Children : null;

    private async Task<IReadOnlyList<AciMo>> FetchClassAsync(
        ApicClient client, string className, string? options, string? scopeDn, CancellationToken token)
    {
        IReadOnlyList<string> pages = await client.ClassAsync(className, options, scopeDn, token)
            .ConfigureAwait(true);

        // 実機で属性名の思い違いを切り分けるための控え（1 クラスぶんだけ持つ）
        _lastResponse = pages.Count > 0 ? pages[0] : "";
        OnPropertyChanged(nameof(LastResponse));

        return AciMoReader.Parse(pages);
    }

    /// <summary>
    /// ログインして <paramref name="work"/> を回し、必ずログアウトする。
    ///
    /// 証明書を受け入れていなければ、指紋を見せて聞き、<b>受け入れられたときだけ 1 度だけ</b>
    /// やり直す。繰り返さない（認証の失敗を繰り返すとアカウントが固まる）。
    /// </summary>
    private async Task RunAsync(string what, Func<ApicClient, CancellationToken, Task> work)
    {
        if (IsBusy) return;

        IsBusy = true;
        Notice = "";
        _cts = new CancellationTokenSource();

        try
        {
            string host = AciCatalog.NormalizeHost(Host);
            bool retried = false;

            while (true)
            {
                Settings.Current.AciFingerprints.TryGetValue(host, out string? accepted);

                try
                {
                    Status = $"{what}を取得しています…";

                    using var client = new ApicClient(host, accepted, _handler);

                    // ログインドメインは使わない（2026-08-17 ユーザー指示で欄ごと外した）。
                    // 必要になったら AciCatalog.LoginName が apic:ドメイン\ユーザー を組み立てる
                    await client.LoginAsync(UserName, Password, "", _cts.Token).ConfigureAwait(true);
                    await work(client, _cts.Token).ConfigureAwait(true);

                    if (client.WasTruncated)
                        Notice = "⚠ 件数が多いため途中で打ち切りました。絞り込んでから取得し直してください。";

                    await client.LogoutAsync().ConfigureAwait(true);

                    Remember(host);
                    Collapse();
                    return;
                }
                catch (PinnedCertificateException ex)
                {
                    Fingerprint = ex.Fingerprint;

                    if (retried)
                    {
                        Status = "証明書を受け入れても接続できませんでした。";
                        return;
                    }

                    retried = true;

                    if (!ConfirmFingerprint(FingerprintQuestion(host, ex.Fingerprint, accepted)))
                    {
                        Status = "証明書を受け入れなかったので接続しませんでした。";
                        return;
                    }

                    // 受け入れたものだけ覚える。勝手に足すとすり替わりに気づけなくなる
                    Settings.Current.AciFingerprints[host] = ex.Fingerprint;
                    Settings.Save();
                }
            }
        }
        catch (ApicApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = "取得できませんでした。";
            CrashLog.Write(ex, "AciViewModel.RunAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    private static string FingerprintQuestion(string host, string fingerprint, string? accepted)
    {
        string head = accepted is { Length: > 0 }
            ? $"{host} の証明書が、前に受け入れたものと変わっています。\n入れ替えた覚えが無ければ、繋がないでください。\n\n前の指紋\n{accepted}\n\n今の指紋\n{fingerprint}"
            : $"{host} の証明書は、既知の認証局では確認できませんでした。\n\n指紋 (SHA-256)\n{fingerprint}";

        return head + "\n\nAPIC の画面に出ている指紋と見比べて、同じであれば受け入れてください。";
    }

    // ===== 保存 =====

    private int RowCount(string? key) => key switch
    {
        "health" => HealthRows.Count,
        "faults" => FaultRows.Count,
        "log" => LogRows.Count,
        "ports" => PortRows.Count,
        "epg" => EpgRows.Count,
        "member" => MemberRows.Count,
        "endpoint" => EndpointRows.Count,
        "config" => ConfigRows.Count,
        _ => 0,
    };

    private CsvTable TableOf(string? key) => key switch
    {
        "health" => AciCatalog.ToCsv([.. HealthRows]),
        "faults" => AciCatalog.ToCsv([.. FaultRows]),
        "log" => AciCatalog.ToCsv([.. LogRows]),
        "ports" => AciCatalog.ToCsv([.. PortRows]),
        "epg" => AciCatalog.ToCsv([.. EpgRows]),
        "member" => AciCatalog.ToCsv([.. MemberRows]),
        "endpoint" => AciCatalog.ToCsv([.. EndpointRows]),
        _ => AciCatalog.ToCsv([.. ConfigRows]),
    };

    private void Save(string? key, bool xlsx)
    {
        if (RowCount(key) == 0) return;

        CsvTable table = TableOf(key);

        if (!xlsx)
        {
            if (CsvExport.Save($"aci-{key}", table) is { } message) Status = message;
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{DeviceReport.Sanitize($"aci-{key}")}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
            DefaultExt = "xlsx",
            Filter = "Excel ブック (*.xlsx)|*.xlsx|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return;

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

    // ===== 片付け =====

    public void Reset()
    {
        _cts?.Cancel();

        foreach (var rows in (System.Collections.IList[])
                 [HealthRows, FaultRows, LogRows, PortRows, EpgRows, MemberRows, EndpointRows, ConfigRows,
                  DiffRows, Nodes, Tenants])
        {
            rows.Clear();
        }

        _allMembers = [];
        _lastResponse = "";
        _before = "";
        _after = "";
        _beforeLabel = "まだ取っていません";
        _afterLabel = "まだ取っていません";
        OnPropertyChanged(nameof(BeforeLabel));
        OnPropertyChanged(nameof(AfterLabel));
        DiffHeadline = "";
        SelectedNode = null;
        SelectedTenant = null;
        SelectedEpg = null;
        FabricHealth = "—";
        Fingerprint = "";
        Password = "";
        PasswordCleared?.Invoke(this, EventArgs.Empty);
        Notice = "";
        Status = Ready;

        ShowConnection = true;
        RefreshSaveCommands();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ===== 小物 =====

    private bool CanFetch()
        => !IsBusy && Host.Trim().Length > 0 && UserName.Trim().Length > 0 && Password.Length > 0;

    private void RefreshFetchCommands()
    {
        FetchCommand.RaiseCanExecuteChanged();
        FetchPortsCommand.RaiseCanExecuteChanged();
        FetchEpgsCommand.RaiseCanExecuteChanged();
        FetchEndpointsCommand.RaiseCanExecuteChanged();
        FetchLogCommand.RaiseCanExecuteChanged();
        FetchConfigCommand.RaiseCanExecuteChanged();
        FetchBeforeCommand.RaiseCanExecuteChanged();
        FetchAfterCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSaveCommands()
    {
        SaveCsvCommand.RaiseCanExecuteChanged();
        SaveXlsxCommand.RaiseCanExecuteChanged();
    }

    private void ShowMembers()
    {
        string dn = SelectedEpg?.Dn ?? "";

        Replace(MemberRows, [.. _allMembers.Where(m => m.EpgDn == dn)]);
        RefreshSaveCommands();
    }

    /// <summary>
    /// テナント 1 つの設定を、枝ごと丸ごと取って「作業前」か「作業後」に入れる。
    ///
    /// <b>ページングは重ねない</b>（枝を 1 応答で返す問い合わせ）。並び順は APIC 任せなので、
    /// <see cref="AciConfigExport"/> が並べ替えて字下げした行に直す — そうしないと
    /// <b>同じ設定でも並びが違うだけで差分だらけになる</b>。
    /// </summary>
    private Task FetchTenantConfigAsync(bool before) => RunAsync(
        before ? "作業前の設定" : "作業後の設定",
        async (client, token) =>
        {
            if (SelectedTenant is not { Length: > 0 } tenant) return;

            string json = await client.SubtreeAsync(AciCatalog.TenantExportPath(tenant), token)
                .ConfigureAwait(true);

            // 枝を丸ごと取るので、大きなテナントでは数 MB になる。
            // 控えは頭だけにする（切り分けに要るのは形であって、全文ではない）
            _lastResponse = json.Length > 256 * 1024 ? json[..(256 * 1024)] : json;
            OnPropertyChanged(nameof(LastResponse));

            IReadOnlyList<AciMo> mos = AciMoReader.Parse(json);

            if (mos.Count == 0)
            {
                Status = $"{tenant} の設定を読み取れませんでした。";
                Notice = "⚠ 「応答を表示」で生の応答を確かめてください（権限が足りないことがあります）。";
                return;
            }

            string label = $"{tenant}（{DateTime.Now:MM-dd HH:mm:ss}）";
            string text = AciConfigExport.Render($"{Host} / テナント {label}", mos);

            if (before)
            {
                _before = text;
                _beforeLabel = label;
                OnPropertyChanged(nameof(BeforeLabel));
            }
            else
            {
                _after = text;
                _afterLabel = label;
                OnPropertyChanged(nameof(AfterLabel));
            }

            CompareCommand.RaiseCanExecuteChanged();

            Status = _before.Length > 0 && _after.Length > 0
                ? $"{(before ? "作業前" : "作業後")}を取りました。「比較」で見比べられます。"
                : $"{(before ? "作業前" : "作業後")}を取りました。もう片方も取ってください。";
        });

    /// <summary>
    /// 取った 2 つを左右に並べる。<b>行として突き合わせる</b> —
    /// 書き出しの段で並べ替えと字下げを済ませてあるので、ここは素の行差分でよい。
    /// </summary>
    private void Compare()
    {
        SideBySideResult result = SideBySideDiff.Build(_before, _after);

        DiffRows.Clear();

        if (result.TooLarge)
        {
            DiffHeadline = "";
            Status = "設定が大きすぎるため差分を取れませんでした。";
            return;
        }

        foreach (SideBySideRow row in OnlyDifferences ? SideBySideDiff.OnlyDifferences(result) : result.Rows)
            DiffRows.Add(row);

        DiffHeadline = result.HasChanges
            ? $"{result.ChangedCount} 行に違いがあります。"
            : "違いはありません。";

        Status = $"{_beforeLabel} と {_afterLabel} を見比べました。";
    }

    private void SyncTenants(IEnumerable<AciMo> mos)
    {
        string? keep = SelectedTenant;

        Tenants.Clear();

        foreach (string name in mos.Select(m => m["name"]).Where(n => n.Length > 0)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Tenants.Add(name);
        }

        SelectedTenant = Tenants.FirstOrDefault(t => t == keep) ?? Tenants.FirstOrDefault();
    }

    private void SyncNodes(IEnumerable<AciMo> mos)
    {
        string? keep = SelectedNode?.Dn;

        Nodes.Clear();

        foreach (AciMo mo in mos)
        {
            string dn = mo["dn"];
            if (dn.Length == 0) continue;

            // ポートは必ずノードで絞る。ファブリック全体の l1PhysIf は数万行になる。
            // topSystem の dn は既に …/sys なので、そのまま絞り込みに使える
            Nodes.Add(new AciNodeItem(
                Name: $"{AciCatalog.DescribeNodeRole(mo["role"])} {AciDn.Value(dn, "node")} {mo["name"]}".Trim(),
                Dn: dn.EndsWith("/sys", StringComparison.Ordinal) ? dn : dn + "/sys"));
        }

        SelectedNode = Nodes.FirstOrDefault(n => n.Dn == keep) ?? Nodes.FirstOrDefault();
    }

    private void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        target.Clear();

        foreach (T row in rows) target.Add(row);

        RefreshSaveCommands();
    }

    private static IEnumerable<string> HostHistory()
        => Settings.Current.AciHosts
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string RememberedUser(string host)
        => Settings.Current.AciUserNames.TryGetValue(AciCatalog.NormalizeHost(host), out string? user) ? user : "";

    /// <summary>繋がった相手とユーザー名だけ覚える。<b>パスワードは覚えない。</b></summary>
    private void Remember(string host)
    {
        if (host.Length == 0) return;

        string[] hosts = [host, .. HostHistory().Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase))];

        Settings.Current.AciHosts = string.Join('\n', hosts.Take(8));
        Settings.Current.AciUserNames[host] = UserName.Trim();
        Settings.Save();
    }
}
