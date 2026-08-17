using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Net;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Terminal;
using NetworkToys.Core.Wireless;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// Cisco WLC（Catalyst 9800）を見に行くタブ。<b>読み取り専用</b>で、設定を書く口は持たない。
///
/// 通信は <see cref="WlcClient"/>（RESTCONF）、応答の解釈は Core の <see cref="WlcCatalog"/>。
/// ここは画面の状態と、取得の段取りだけを持つ。
///
/// <b>タブを選んだだけでは 1 バイトも出さない</b>（<c>OnActivated</c> を持たない）。
/// CI の Windows ランナーは外に出られるので、選んだだけで通信する作りにすると
/// 自己診断から本番の WLC へ要求が飛ぶ。
/// </summary>
public sealed class WlcViewModel : ObservableObject, IDisposable
{
    private const string Ready = "WLC のアドレスとユーザーを入れて「取得」を押すと、端末と AP を取ってきます。";

    private readonly HttpMessageHandler? _handler;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<WlcClientRow> _allClients = [];
    private IReadOnlyList<WlcApRow> _allAps = [];

    private string _host = "";
    private string _userName = "";
    private string _password = "";
    private string _fingerprint = "";
    private string _status = Ready;
    private string _notice = "";
    private string _clientQuery = "";
    private bool _onlyDisconnected;
    private bool _isBusy;
    private string _lastResponse = "";
    private string _lastUrl = "";

    /// <param name="handler">自己診断が偽の WLC を挿すための口。既定は本物。</param>
    public WlcViewModel(HttpMessageHandler? handler = null)
    {
        _handler = handler;

        FetchCommand = new RelayCommand(() => _ = FetchBasicsAsync(), CanFetch);
        FetchJoinsCommand = new RelayCommand(() => _ = FetchJoinsAsync(), CanFetch);
        FetchRrmCommand = new RelayCommand(() => _ = FetchRrmAsync(), CanFetch);
        FetchRoguesCommand = new RelayCommand(() => _ = FetchRoguesAsync(), CanFetch);
        FetchSsidsCommand = new RelayCommand(() => _ = FetchSsidsAsync(), CanFetch);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);

        SortWeakestCommand = new RelayCommand(SortWeakest, () => ClientRows.Count > 0);

        SaveCsvCommand = new RelayCommand<string>(key => Save(key, xlsx: false), key => RowCount(key) > 0);
        SaveXlsxCommand = new RelayCommand<string>(key => Save(key, xlsx: true), key => RowCount(key) > 0);

        KnownHosts = [.. HostHistory()];
        _host = KnownHosts.Count > 0 ? KnownHosts[0] : "";
        _userName = RememberedUser(_host);
    }

    // ===== 一覧 =====

    public ObservableCollection<WlcClientRow> ClientRows { get; } = [];
    public ObservableCollection<WlcApRow> ApRows { get; } = [];
    public ObservableCollection<WlcJoinRow> JoinRows { get; } = [];
    public ObservableCollection<WlcRrmRow> RrmRows { get; } = [];
    public ObservableCollection<WlcRogueRow> RogueRows { get; } = [];
    public ObservableCollection<WlcSsidRow> SsidRows { get; } = [];

    public ObservableCollection<string> KnownHosts { get; }

    // ===== コマンド =====

    public RelayCommand FetchCommand { get; }
    public RelayCommand FetchJoinsCommand { get; }
    public RelayCommand FetchRrmCommand { get; }
    public RelayCommand FetchRoguesCommand { get; }
    public RelayCommand FetchSsidsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SortWeakestCommand { get; }

    public RelayCommand<string> SaveCsvCommand { get; }
    public RelayCommand<string> SaveXlsxCommand { get; }

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

    /// <summary>
    /// 端末の絞り込み。<b>IP でも MAC でも AP 名でも同じ欄に入れてよい</b>
    /// （取ってきた後に絞るので、打つたびに通信はしない）。
    /// </summary>
    public string ClientQuery
    {
        get => _clientQuery;
        set { if (SetProperty(ref _clientQuery, value)) ShowClients(); }
    }

    /// <summary>AP 一覧を「繋がっていないものだけ」にする。</summary>
    public bool OnlyDisconnected
    {
        get => _onlyDisconnected;
        set { if (SetProperty(ref _onlyDisconnected, value)) ShowAps(); }
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
    /// 「取得」。<b>端末と AP を一度に取る</b> — 知りたいことの大半はこの 2 つに入っている。
    /// AP の台数は端末の側から数えるので、順番にも意味がある。
    /// </summary>
    private Task FetchBasicsAsync() => RunAsync("端末と AP", async (client, token) =>
    {
        IReadOnlyList<JsonElement> common = await GetAsync(client, WlcCatalog.ClientCommonPaths, token);
        IReadOnlyList<JsonElement> dot11 = await GetAsync(client, WlcCatalog.ClientDot11Paths, token);
        IReadOnlyList<JsonElement> traffic = await GetAsync(client, WlcCatalog.ClientTrafficPaths, token);
        IReadOnlyList<JsonElement> sisf = await GetAsync(client, WlcCatalog.ClientSisfPaths, token);

        _allClients = WlcCatalog.ParseClients(common, dot11, traffic, sisf, OuiCatalog.FindVendor);
        ShowClients();

        IReadOnlyList<JsonElement> capwap = await GetAsync(client, WlcCatalog.ApCapwapPaths, token);
        IReadOnlyList<JsonElement> radios = await GetAsync(client, WlcCatalog.ApRadioPaths, token);
        IReadOnlyList<JsonElement> tags = await GetAsync(client, WlcCatalog.ApTagPaths, token, optional: true);
        IReadOnlyList<JsonElement> joins = await GetAsync(client, WlcCatalog.ApJoinPaths, token, optional: true);

        _allAps = WlcCatalog.ParseAps(capwap, radios, tags, joins, WlcCatalog.CountClientsByAp(_allClients));
        ShowAps();

        int missing = _allAps.Count(a => !a.IsJoined);

        Status = $"端末 {_allClients.Count} 台 / AP {_allAps.Count} 台"
                 + (missing > 0 ? $"（うち未接続 {missing} 台）" : "")
                 + $"（{DateTime.Now:HH:mm:ss}）";

        if (missing > 0)
            Notice = $"⚠ 繋がっていない AP が {missing} 台あります。AP の一覧で「未接続だけ」を押すと絞れます。";
    });

    private Task FetchJoinsAsync() => RunAsync("参加・切断", async (client, token) =>
    {
        IReadOnlyList<JsonElement> joins = await GetAsync(client, WlcCatalog.ApJoinPaths, token);

        Replace(JoinRows, WlcCatalog.ParseJoins(joins, _allAps));
        Status = $"参加・切断 {JoinRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchRrmAsync() => RunAsync("電波", async (client, token) =>
    {
        IReadOnlyList<JsonElement> measurements = await GetAsync(client, WlcCatalog.RrmPaths, token);

        Replace(RrmRows, WlcCatalog.ParseRrm(measurements, _allAps));
        Status = $"電波 {RrmRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchRoguesAsync() => RunAsync("不正 AP", async (client, token) =>
    {
        IReadOnlyList<JsonElement> rogues = await GetAsync(client, WlcCatalog.RoguePaths, token, optional: true);
        IReadOnlyList<JsonElement> neighbors = await GetAsync(client, WlcCatalog.NeighborPaths, token, optional: true);

        Replace(RogueRows, WlcCatalog.ParseRogues(rogues, neighbors, OuiCatalog.FindVendor));
        Status = $"不正・隣接 {RogueRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    private Task FetchSsidsAsync() => RunAsync("SSID", async (client, token) =>
    {
        IReadOnlyList<JsonElement> wlans = await GetAsync(client, WlcCatalog.WlanPaths, token);

        Replace(SsidRows, WlcCatalog.ParseSsids(wlans, _allClients));
        Status = $"SSID {SsidRows.Count} 件（{DateTime.Now:HH:mm:ss}）";
    });

    /// <summary>
    /// 1 本取って行にほどく。<paramref name="optional"/> のものは、
    /// 無くても取得全体を止めない（版によって持っていないデータがある）。
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>> GetAsync(
        WlcClient client, IReadOnlyList<string> paths, CancellationToken token, bool optional = false)
    {
        try
        {
            string body = await client.GetFirstAsync(paths, token).ConfigureAwait(true);

            _lastUrl = client.LastUrl;
            _lastResponse = body.Length > 256 * 1024 ? body[..(256 * 1024)] : body;
            OnPropertyChanged(nameof(LastResponse));

            return WlcYang.Rows(body);
        }
        catch (WlcApiException) when (optional)
        {
            // 無いものは無いままにする。ここで止めると、取れるはずの一覧まで道連れになる
            return [];
        }
    }

    /// <summary>
    /// 取得の 1 まとまり。証明書を受け入れていなければ、指紋を見せて聞き、
    /// <b>受け入れられたときだけ 1 度だけ</b>やり直す（繰り返さない）。
    /// </summary>
    private async Task RunAsync(string what, Func<WlcClient, CancellationToken, Task> work)
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
                Settings.Current.WlcFingerprints.TryGetValue(host, out string? accepted);

                try
                {
                    Status = $"{what}を取得しています…";

                    using var client = new WlcClient(host, UserName, Password, accepted, _handler);

                    await work(client, _cts.Token).ConfigureAwait(true);

                    Remember(host);
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

                    Settings.Current.WlcFingerprints[host] = ex.Fingerprint;
                    Settings.Save();
                }
            }
        }
        catch (WlcApiException ex)
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
            CrashLog.Write(ex, "WlcViewModel.RunAsync");
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

        return head + "\n\nWLC の画面に出ている指紋と見比べて、同じであれば受け入れてください。";
    }

    // ===== 画面に出す =====

    private void ShowClients() => Replace(ClientRows, WlcCatalog.FilterClients(_allClients, ClientQuery));

    private void ShowAps()
    {
        IReadOnlyList<WlcApRow> rows = OnlyDisconnected
            ? [.. _allAps.Where(a => !a.IsJoined)]
            : _allAps;

        Replace(ApRows, rows);
    }

    /// <summary>電波の弱い順に並べ替える。「無線が遅い」と言われたときに最初に見る並び。</summary>
    private void SortWeakest()
    {
        // 取れなかった端末(0)は最後に回す。0 を「いちばん強い」ことにしない
        Replace(ClientRows, [.. ClientRows.OrderBy(r => r.Rssi == 0 ? int.MaxValue : r.Rssi)]);
        Status = "電波の弱い順に並べ替えました。";
    }

    // ===== 保存 =====

    private int RowCount(string? key) => key switch
    {
        "client" => ClientRows.Count,
        "ap" => ApRows.Count,
        "join" => JoinRows.Count,
        "rrm" => RrmRows.Count,
        "rogue" => RogueRows.Count,
        "ssid" => SsidRows.Count,
        _ => 0,
    };

    private CsvTable TableOf(string? key) => key switch
    {
        "client" => WlcCatalog.ToCsv([.. ClientRows]),
        "ap" => WlcCatalog.ToCsv([.. ApRows]),
        "join" => WlcCatalog.ToCsv([.. JoinRows]),
        "rrm" => WlcCatalog.ToCsv([.. RrmRows]),
        "rogue" => WlcCatalog.ToCsv([.. RogueRows]),
        _ => WlcCatalog.ToCsv([.. SsidRows]),
    };

    private void Save(string? key, bool xlsx)
    {
        if (RowCount(key) == 0) return;

        CsvTable table = TableOf(key);

        if (!xlsx)
        {
            if (CsvExport.Save($"wlc-{key}", table) is { } message) Status = message;
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{DeviceReport.Sanitize($"wlc-{key}")}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
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
                 [ClientRows, ApRows, JoinRows, RrmRows, RogueRows, SsidRows])
        {
            rows.Clear();
        }

        _allClients = [];
        _allAps = [];
        _lastResponse = "";
        _lastUrl = "";
        ClientQuery = "";
        OnlyDisconnected = false;
        Fingerprint = "";
        Password = "";
        PasswordCleared?.Invoke(this, EventArgs.Empty);
        Notice = "";
        Status = Ready;

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
        FetchJoinsCommand.RaiseCanExecuteChanged();
        FetchRrmCommand.RaiseCanExecuteChanged();
        FetchRoguesCommand.RaiseCanExecuteChanged();
        FetchSsidsCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSaveCommands()
    {
        SaveCsvCommand.RaiseCanExecuteChanged();
        SaveXlsxCommand.RaiseCanExecuteChanged();
        SortWeakestCommand.RaiseCanExecuteChanged();
    }

    private void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        target.Clear();

        foreach (T row in rows) target.Add(row);

        RefreshSaveCommands();
    }

    private static IEnumerable<string> HostHistory()
        => Settings.Current.WlcHosts
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string RememberedUser(string host)
        => Settings.Current.WlcUserNames.TryGetValue(
            HttpsHost.Normalize(host), out string? user) ? user : "";

    /// <summary>繋がった相手とユーザー名だけ覚える。<b>パスワードは覚えない。</b></summary>
    private void Remember(string host)
    {
        if (host.Length == 0) return;

        string[] hosts = [host, .. HostHistory().Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase))];

        Settings.Current.WlcHosts = string.Join('\n', hosts.Take(8));
        Settings.Current.WlcUserNames[host] = UserName.Trim();
        Settings.Save();

        if (!KnownHosts.Contains(host)) KnownHosts.Insert(0, host);
    }
}
