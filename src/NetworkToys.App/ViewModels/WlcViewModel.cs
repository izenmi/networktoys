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
/// <b>通信は SSH だけ</b>（2026-08-18 ユーザー指示。RESTCONF を有効にできない現場があり、
/// 入口が 2 つあると「どちらを押せばよいか」で迷うため畳んだ）。
/// 会話は収集タブと同じ <see cref="DeviceCollector"/>、出力の解釈は Core の
/// <see cref="WlcShow"/>。ここは画面の状態と段取りだけを持つ。
///
/// <b>タブを選んだだけでは 1 バイトも出さない</b>（<c>OnActivated</c> を持たない）。
/// CI の Windows ランナーは外に出られるので、選んだだけで通信する作りにすると
/// 自己診断から本番の WLC へ要求が飛ぶ。
/// </summary>
public sealed class WlcViewModel : ObservableObject, IDisposable
{
    private const string Ready = "WLC のアドレスとユーザーを入れて「取得」を押すと、SSH で入って一通り取ってきます。";

    /// <summary>
    /// 1 回の「取得」で流す <c>show</c>。<b>この 1 本で全部のタブが埋まる</b>。
    ///
    /// 版で名前が割れるものは両方投げて失敗を無視する（ページャ無効化と同じ
    /// 「判定の誤りより無害な失敗」）。出力の解釈は <see cref="WlcShow"/>。
    /// </summary>
    private static readonly string[] ShowCommands =
    [
        "show ap summary",
        "show ap join stats summary",
        "show wireless client summary",
        "show wireless device-tracking database mac",
        "show wireless wlan summary",
        "show ap dot11 24ghz summary",
        "show ap dot11 5ghz summary",
        "show rogue ap summary",
        "show wireless wps rogue ap summary",
    ];

    private CancellationTokenSource? _cts;
    private IReadOnlyList<WlcClientRow> _allClients = [];
    private IReadOnlyList<WlcApRow> _allAps = [];

    private string _host = "";
    private string _userName = "";
    private string _password = "";
    private string _status = Ready;
    private string _notice = "";
    private string _clientQuery = "";
    private bool _onlyDisconnected;
    private bool _isBusy;
    private string _lastShow = "";
    private bool _showConnection = true;

    public WlcViewModel()
    {
        FetchCommand = new RelayCommand(() => _ = RunShowAsync(), CanFetch);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);

        SortWeakestCommand = new RelayCommand(SortWeakest, () => ClientRows.Count > 0);

        SaveCsvCommand = new RelayCommand<string>(key => Save(key, xlsx: false), key => RowCount(key) > 0);
        SaveXlsxCommand = new RelayCommand<string>(key => Save(key, xlsx: true), key => RowCount(key) > 0);

        // 前に繋いだ相手を出しておく
        _host = HostHistory().FirstOrDefault() ?? "";
        _userName = RememberedUser(_host);
    }

    // ===== 一覧 =====

    public ObservableCollection<WlcClientRow> ClientRows { get; } = [];
    public ObservableCollection<WlcApRow> ApRows { get; } = [];
    public ObservableCollection<WlcJoinRow> JoinRows { get; } = [];
    public ObservableCollection<WlcRrmRow> RrmRows { get; } = [];
    public ObservableCollection<WlcRogueRow> RogueRows { get; } = [];
    public ObservableCollection<WlcSsidRow> SsidRows { get; } = [];

    // ===== コマンド =====

    /// <summary>SSH で入って show を一通り流し、全部のタブを埋める。</summary>
    public RelayCommand FetchCommand { get; }

    public RelayCommand CancelCommand { get; }
    public RelayCommand SortWeakestCommand { get; }

    public RelayCommand<string> SaveCsvCommand { get; }
    public RelayCommand<string> SaveXlsxCommand { get; }

    /// <summary>
    /// SSH で取ってきた生の出力（流したコマンドごと）。
    /// <b>表の元になった文字をいつでも読めるようにしておく</b> —
    /// 見出しの思い違いは実機でしか分からず、これが無いと「表が空」から先へ進めない。
    /// </summary>
    public string LastShowOutput => _lastShow;

    /// <summary>画面の <c>PasswordBox</c> を空にしてもらう合図。</summary>
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
    /// SSH で show を流す。既存の収集タブの仕組み（<see cref="DeviceCollector"/>）を
    /// そのまま使うので、ページャ無効化・プロンプトの学習・パスワード 2 回で中断は
    /// あちらが面倒を見てくれる。<b>出力は解釈せず、そのまま画面に出す。</b>
    /// </summary>
    private async Task RunShowAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Notice = "";
        _cts = new CancellationTokenSource();

        try
        {
            Status = "SSH で取得しています…（少し時間がかかります）";

            var request = new CollectRequest(
                Host: HttpsHost.Normalize(Host),
                Port: 22,
                UseSsh: true,
                Credentials: new DeviceCredentials(UserName.Trim(), Password, ""),
                Memo: "WLC");

            DeviceCollectionResult result = await DeviceCollector.CollectAsync(
                request, ShowCommands, new CiscoSessionOptions(),
                TimeSpan.FromSeconds(15), null, _cts.Token).ConfigureAwait(true);

            _lastShow = DeviceReport.Render(result);
            OnPropertyChanged(nameof(LastShowOutput));

            int filled = FillFromShow(result);

            Status = result.FailureMessage is { Length: > 0 } failure
                ? failure
                : $"SSH で {result.Commands.Count} 本ぶん取れました（表に {filled} 行）。生の出力は「SSH の出力」で開けます。";

            Notice = "⚠ SSH から作った表です。IP・電波の強さ・混み具合は show の要約に出ないので「—」になります。";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = "SSH で取得できませんでした。";
            CrashLog.Write(ex, "WlcViewModel.RunShowAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ===== 画面に出す =====

    /// <summary>
    /// SSH の出力から表を作る。<b>RESTCONF を有効にできない現場のための本道</b>
    /// （2026-08-18 ユーザー指示。それまでは生の出力を見せるだけだった）。
    ///
    /// 出ない項目は埋めない（IP・電波の強さ・混み具合は show に無い）。
    /// 読めなかったコマンドがあっても、読めたぶんの表は出す。
    /// </summary>
    private int FillFromShow(DeviceCollectionResult result)
    {
        string Output(string startsWith) => result.Commands
            .FirstOrDefault(c => c.Command.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase))?.Output ?? "";

        string aps = Output("show ap summary");
        string joins = Output("show ap join stats");
        string clients = Output("show wireless client summary");
        string wlans = Output("show wireless wlan summary");
        string radio24 = Output("show ap dot11 24ghz summary");
        string radio5 = Output("show ap dot11 5ghz summary");
        string rogues = Or(Output("show wireless wps rogue ap summary"), Output("show rogue ap summary"));

        IReadOnlyList<WlcSsidRow> ssids = WlcShow.ParseWlanSummary(wlans);

        // 端末の一覧に IP は出ない。追跡データベースから MAC で引いて埋める。
        // メーカーは MAC から引ける（OUI）ので、機器に聞かずに埋まる
        IReadOnlyDictionary<string, string> ips =
            WlcShow.ParseIpBindings(Output("show wireless device-tracking"));

        _allAps = WlcShow.ParseApSummary(aps, joins);
        _allClients = WlcShow.ParseClientSummary(clients, ssids, ips, mac => OuiCatalog.FindVendor(mac) ?? "");

        Replace(SsidRows, ssids);
        Replace(JoinRows, WlcShow.ParseJoinStats(joins));
        Replace(RogueRows, WlcShow.ParseRogueSummary(rogues));

        Replace(RrmRows,
        [
            .. WlcShow.ParseRadioSummary(radio24, "2.4GHz"),
            .. WlcShow.ParseRadioSummary(radio5, "5GHz"),
        ]);

        ShowAps();
        ShowClients();

        return ApRows.Count + ClientRows.Count + SsidRows.Count
               + JoinRows.Count + RrmRows.Count + RogueRows.Count;
    }

    private static string Or(string first, string second) => first.Length > 0 ? first : second;

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
        _lastShow = "";
        ClientQuery = "";
        OnlyDisconnected = false;
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

    private void RefreshFetchCommands() => FetchCommand.RaiseCanExecuteChanged();

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
    }
}
