using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Assurance;
using NetworkToys.Core.Net;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Terminal;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// Cisco Catalyst Center を見に行くタブ。<b>読み取り専用</b>で、設定を書く口は持たない。
///
/// 通信は <see cref="DnacClient"/>、応答の解釈は Core の <see cref="DnacCatalog"/>。
/// ここは画面の状態と、取得の段取りだけを持つ。
///
/// <b>タブを選んだだけでは 1 バイトも出さない</b>（<c>OnActivated</c> を持たない）。
/// CI の Windows ランナーは外に出られるので、選んだだけで通信する作りにすると
/// 自己診断から本番の Catalyst Center へ要求が飛ぶ。
/// </summary>
public sealed class DnacViewModel : ObservableObject, IDisposable
{
    private const string Ready =
        "Catalyst Center のアドレスとユーザーを入れ、IP か MAC を渡すと接続先を調べます。";

    private readonly HttpMessageHandler? _handler;

    private CancellationTokenSource? _cts;

    private string _host = "";
    private string _userName = "";
    private string _password = "";
    private string _fingerprint = "";
    private string _query = "";
    private string _status = Ready;
    private string _notice = "";
    private bool _isBusy;
    private string _lastResponse = "";
    private string _lastUrl = "";
    private bool _showConnection = true;
    private string _lifecycleKind = "EoX（保守終了）";

    /// <summary>機器の uuid → 名前。保守と適合の表は uuid しか返さないので、これで置き換える。</summary>
    private IReadOnlyDictionary<string, string> _deviceNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // 期間の選択肢は Meraki と同じ形。同じものを 2 つ作らない
    private MerakiTimespan _selectedTimespan;

    /// <param name="handler">自己診断が偽の Catalyst Center を挿すための口。既定は本物。</param>
    public DnacViewModel(HttpMessageHandler? handler = null)
    {
        _handler = handler;
        _selectedTimespan = Timespans[1];

        FetchClientCommand = new RelayCommand(() => _ = FetchClientAsync(), CanFetch);
        FetchDevicesCommand = new RelayCommand(() => _ = FetchDevicesAsync(), CanFetch);
        FetchLifecycleCommand = new RelayCommand(() => _ = FetchLifecycleAsync(), CanFetch);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);

        SaveCsvCommand = new RelayCommand<string>(key => Save(key, xlsx: false), key => RowCount(key) > 0);
        SaveXlsxCommand = new RelayCommand<string>(key => Save(key, xlsx: true), key => RowCount(key) > 0);

        // 前に繋いだ相手を出しておく
        _host = HostHistory().FirstOrDefault() ?? "";
        _userName = RememberedUser(_host);
    }

    // ===== 一覧 =====

    public ObservableCollection<DnacConnectionRow> ConnectionRows { get; } = [];
    public ObservableCollection<DnacEventRow> EventRows { get; } = [];
    public ObservableCollection<DnacDeviceRow> DeviceRows { get; } = [];
    public ObservableCollection<DnacLifecycleRow> LifecycleRows { get; } = [];

    // ===== コマンド =====

    public RelayCommand FetchClientCommand { get; }
    public RelayCommand FetchDevicesCommand { get; }
    public RelayCommand FetchLifecycleCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleConnectionCommand { get; }

    /// <summary>
    /// 保守と適合で見るもの。<b>3 つを 1 枚の表に寄せる</b> —
    /// どれも「機器ごとに 1 行、状態と日付と一言」で、列が同じになるため。
    /// </summary>
    public IReadOnlyList<string> LifecycleKinds { get; } = ["EoX（保守終了）", "適合性", "ライセンス"];

    public string SelectedLifecycleKind
    {
        get => _lifecycleKind;
        set => SetProperty(ref _lifecycleKind, value);
    }

    public RelayCommand<string> SaveCsvCommand { get; }
    public RelayCommand<string> SaveXlsxCommand { get; }

    /// <summary>イベントを遡る幅。<b>Meraki の選択肢をそのまま使う</b>。</summary>
    public IReadOnlyList<MerakiTimespan> Timespans { get; } =
    [
        new("1 時間", 3600),
        new("1 日", 86400),
        new("7 日", 604800),
    ];

    public MerakiTimespan SelectedTimespan
    {
        get => _selectedTimespan;
        set => SetProperty(ref _selectedTimespan, value);
    }

    /// <summary>
    /// 直前に取れた生の応答。<b>1 行目に投げた URL を添える</b> —
    /// リーフ名の思い違いは実機でしか分からず、どのパスを叩いたかが要るため。
    /// </summary>
    public string LastResponse => _lastUrl.Length > 0 ? $"{_lastUrl}\n\n{_lastResponse}" : _lastResponse;

    /// <summary>
    /// 証明書の指紋を見せて受け入れるかを聞く。画面が結線する。
    /// <b>結線前の既定は「いいえ」</b>。
    /// </summary>
    public Func<string, bool> ConfirmFingerprint { get; set; } = _ => false;

    /// <summary>画面の <c>PasswordBox</c> を空にしてもらう合図。</summary>
    public event EventHandler? PasswordCleared;

    /// <summary>
    /// 接続の欄を開いているか。<b>取得できたら畳む</b> — 一覧に画面を使いたいので。
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

    public string Fingerprint
    {
        get => _fingerprint;
        private set => SetProperty(ref _fingerprint, value);
    }

    /// <summary>探す端末。<b>IP でも MAC でもよい</b>（どちらかは見て決める）。</summary>
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value)) RefreshFetchCommands(); }
    }

    // ===== 状態 =====

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice.Length > 0;

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

    /// <summary>
    /// 「調べる」。IP か MAC から接続先を引き、続けてその端末のイベントを取る。
    ///
    /// <b>MAC が分からないとイベントは引けない</b>（Catalyst Center の索引が MAC だけ）。
    /// IP で引いて MAC が判明したら、そこから続ける。
    /// </summary>
    private Task FetchClientAsync() => RunAsync("端末", async (client, token) =>
    {
        DnacEntityKind kind = DnacCatalog.EntityKindOf(Query);

        if (kind == DnacEntityKind.Unknown)
        {
            Status = "IP アドレスか MAC アドレスを入れてください。";
            return;
        }

        string value = Query.Trim();
        string json = await client.ClientAsync(DnacCatalog.EntityTypeOf(kind), value, token).ConfigureAwait(true);

        Record(client, json);

        IReadOnlyList<DnacConnectionRow> rows = DnacCatalog.ParseConnections(DnacJson.Rows(json));

        // 版が古いと enrichment が無い。MAC が分かっているときだけ client-detail に落とす
        if (rows.Count == 0 && kind == DnacEntityKind.Mac)
        {
            string detail = await client.GetAsync(
                DnacCatalog.ClientDetailPath(value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token)
                .ConfigureAwait(true);

            Record(client, detail);
            rows = DnacCatalog.ParseClientDetail(DnacJson.One(detail));
        }

        Replace(ConnectionRows, rows);

        if (rows.Count == 0)
        {
            EventRows.Clear();
            Status = $"{value} は見つかりませんでした。";
            Notice = "⚠ Catalyst Center が知らない端末か、しばらく通信していない端末です。";
            return;
        }

        Status = $"{value} は {rows.Count} 経路で見えています（{DateTime.Now:HH:mm:ss}）。";

        string mac = rows.Select(r => r.Mac).FirstOrDefault(m => m.Length > 0)
                     ?? (kind == DnacEntityKind.Mac ? value : "");

        if (mac.Length == 0)
        {
            EventRows.Clear();
            Notice = "⚠ MAC が分からないので、この端末のイベントは引けませんでした。";
            return;
        }

        await FetchEventsAsync(client, mac, json, token).ConfigureAwait(true);
    });

    /// <summary>
    /// その端末に起きたこと。<b>この API は 2.3.5 より前の版に無い</b>ので、
    /// 無ければ enrichment が持っている「問題」を代わりに出す（空の表を出すより、持っているものを出す）。
    /// </summary>
    private async Task FetchEventsAsync(
        DnacClient client, string mac, string enrichment, CancellationToken token)
    {
        long end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long start = end - ((long)SelectedTimespan.Seconds * 1000);

        try
        {
            string json = await client.GetAsync(DnacCatalog.EventsPath(mac, start, end), token)
                .ConfigureAwait(true);

            Record(client, json);
            Replace(EventRows, DnacCatalog.ParseEvents(DnacJson.Rows(json)));

            if (EventRows.Count == 0)
                Notice = $"⚠ この {SelectedTimespan.Name}のあいだ、記録されたイベントはありませんでした。";

            return;
        }
        catch (DnacApiException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
        {
            // 版が古い。持っているもので代える
        }

        Replace(EventRows, DnacCatalog.ParseIssuesAsEvents(DnacJson.Rows(enrichment)));

        Notice = "⚠ この版にはイベントの一覧がありません。代わりに Assurance が挙げている問題を出しています。";
    }

    /// <summary>
    /// 機器の一覧。在庫を取り切ってから健全度を重ねる。
    /// <b>健全度が取れなくても在庫だけは必ず出す</b>（在庫に居るのに消えると「その機器は無い」と誤読される）。
    /// </summary>
    private Task FetchDevicesAsync() => RunAsync("機器", async (client, token) =>
    {
        IReadOnlyList<string> pages = await client.DevicesAsync(token).ConfigureAwait(true);

        Record(client, pages.Count > 0 ? pages[^1] : "");

        List<JsonElement> inventory = [.. pages.SelectMany(DnacJson.Rows)];
        IReadOnlyList<JsonElement> health = [];

        try
        {
            string json = await client.GetFirstAsync(DnacCatalog.DeviceHealthPaths, token).ConfigureAwait(true);

            health = DnacJson.Rows(json);
        }
        catch (DnacApiException)
        {
            // 健全度の在り処は版で違う。取れなくても在庫は出す
            Notice = "⚠ 健全度は取れませんでした（この版には無い問い合わせ先のようです）。";
        }

        Replace(DeviceRows, DnacCatalog.ParseDevices(inventory, health));

        _deviceNames = DeviceNames(inventory);

        Status = $"機器 {DeviceRows.Count} 台（{DateTime.Now:HH:mm:ss}）";
    });

    /// <summary>
    /// 保守と適合。<b>EoX・適合性・ライセンスを同じ 5 列に寄せる</b>（種別で選ぶ）。
    /// 機器の名前は uuid でしか返らないので、先に取った機器の一覧で置き換える。
    /// </summary>
    private Task FetchLifecycleAsync() => RunAsync(SelectedLifecycleKind, async (client, token) =>
    {
        IReadOnlyList<string> paths = DnacCatalog.EoxPaths;
        Func<IEnumerable<JsonElement>, IReadOnlyList<DnacLifecycleRow>> parse = DnacCatalog.ParseEox;

        if (SelectedLifecycleKind == "適合性")
        {
            paths = DnacCatalog.CompliancePaths;
            parse = DnacCatalog.ParseCompliance;
        }
        else if (SelectedLifecycleKind == "ライセンス")
        {
            paths = DnacCatalog.LicensePaths;
            parse = DnacCatalog.ParseLicenses;
        }

        string json = await client.GetFirstAsync(paths, token).ConfigureAwait(true);

        Record(client, json);

        IReadOnlyList<DnacLifecycleRow> rows = parse(DnacJson.Rows(json));

        // uuid のままでは読めないので、分かるものだけ名前にする
        Replace(LifecycleRows, [.. rows.Select(r =>
            _deviceNames.TryGetValue(r.Device, out string? name) ? r with { Device = name } : r)]);

        Status = $"{SelectedLifecycleKind} {LifecycleRows.Count} 件（{DateTime.Now:HH:mm:ss}）";

        if (LifecycleRows.Count == 0)
            Notice = "⚠ 1 件も返りませんでした。この環境では使えない機能か、まだスキャンしていない可能性があります。";
        else if (_deviceNames.Count == 0)
            Notice = "⚠ 先に「機器」を取っておくと、機器の欄が uuid ではなく名前になります。";
    });

    private static IReadOnlyDictionary<string, string> DeviceNames(IEnumerable<JsonElement> inventory)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement device in inventory)
        {
            string id = DnacJson.First(device, "id", "instanceUuid");
            string name = DnacJson.First(device, "hostname", "name");

            if (id.Length > 0 && name.Length > 0) names[id] = name;
        }

        return names;
    }

    /// <summary>「応答を表示」のための控え。大きすぎるものは頭だけ。</summary>
    private void Record(DnacClient client, string body)
    {
        _lastUrl = client.LastUrl;
        _lastResponse = body.Length > 256 * 1024 ? body[..(256 * 1024)] : body;
        OnPropertyChanged(nameof(LastResponse));
    }

    /// <summary>
    /// 取得の 1 まとまり。<b>1 回の取得につきログインは 1 回</b>。
    /// 証明書を受け入れていなければ、指紋を見せて聞き、
    /// <b>受け入れられたときだけ 1 度だけ</b>やり直す（繰り返さない）。
    /// </summary>
    private async Task RunAsync(string what, Func<DnacClient, CancellationToken, Task> work)
    {
        if (IsBusy) return;

        IsBusy = true;
        Notice = "";
        _cts = new CancellationTokenSource();

        try
        {
            string host = HttpsHost.Normalize(Host);
            bool retried = false;

            while (true)
            {
                Settings.Current.DnacFingerprints.TryGetValue(host, out string? accepted);

                try
                {
                    Status = $"{what}を取得しています…";

                    using var client = new DnacClient(host, accepted, _handler);

                    await client.LoginAsync(UserName.Trim(), Password, _cts.Token).ConfigureAwait(true);
                    await work(client, _cts.Token).ConfigureAwait(true);

                    if (client.WasTruncated)
                        Notice = "⚠ 件数が多いので途中で打ち切りました。すべては出ていません。";

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

                    Settings.Current.DnacFingerprints[host] = ex.Fingerprint;
                    Settings.Save();
                }
            }
        }
        catch (DnacApiException ex)
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
            CrashLog.Write(ex, "DnacViewModel.RunAsync");
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

        return head + "\n\nCatalyst Center の画面に出ている指紋と見比べて、同じであれば受け入れてください。";
    }

    // ===== 保存 =====

    private int RowCount(string? key) => key switch
    {
        "conn" => ConnectionRows.Count,
        "event" => EventRows.Count,
        "dev" => DeviceRows.Count,
        "life" => LifecycleRows.Count,
        _ => 0,
    };

    private CsvTable TableOf(string? key) => key switch
    {
        "conn" => DnacCatalog.ToCsv([.. ConnectionRows]),
        "dev" => DnacCatalog.ToCsv([.. DeviceRows]),
        "life" => DnacCatalog.ToCsv([.. LifecycleRows]),
        _ => DnacCatalog.ToCsv([.. EventRows]),
    };

    private void Save(string? key, bool xlsx)
    {
        if (RowCount(key) == 0) return;

        CsvTable table = TableOf(key);

        if (!xlsx)
        {
            if (CsvExport.Save($"dnac-{key}", table) is { } message) Status = message;
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{DeviceReport.Sanitize($"dnac-{key}")}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
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

        ConnectionRows.Clear();
        EventRows.Clear();
        DeviceRows.Clear();
        LifecycleRows.Clear();

        _deviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _lastResponse = "";
        _lastUrl = "";
        Query = "";
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
        FetchClientCommand.RaiseCanExecuteChanged();
        FetchDevicesCommand.RaiseCanExecuteChanged();
        FetchLifecycleCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSaveCommands()
    {
        SaveCsvCommand.RaiseCanExecuteChanged();
        SaveXlsxCommand.RaiseCanExecuteChanged();
    }

    private void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        target.Clear();

        foreach (T row in rows) target.Add(row);

        RefreshSaveCommands();
    }

    private static IEnumerable<string> HostHistory()
        => Settings.Current.DnacHosts
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string RememberedUser(string host)
        => Settings.Current.DnacUserNames.TryGetValue(
            HttpsHost.Normalize(host), out string? user) ? user : "";

    /// <summary>繋がった相手とユーザー名だけ覚える。<b>パスワードは覚えない。</b></summary>
    private void Remember(string host)
    {
        if (host.Length == 0) return;

        string[] hosts = [host, .. HostHistory().Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase))];

        Settings.Current.DnacHosts = string.Join('\n', hosts.Take(8));
        Settings.Current.DnacUserNames[host] = UserName.Trim();
        Settings.Save();
    }
}
