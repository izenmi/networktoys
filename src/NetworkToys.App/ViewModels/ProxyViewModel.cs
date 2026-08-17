using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Net;

namespace NetworkToys.App.ViewModels;

/// <summary>方式 ComboBox の項目。</summary>
public sealed record ProxyModeChoice(ProxyMode Mode, string Name);

/// <summary>
/// プロキシ設定(IP設定タブ内のセクション)。Windows のユーザー設定(WinINET)を
/// 読み書きするだけなので管理者権限は不要。UAC も出ない。
/// </summary>
public sealed class ProxyViewModel : ObservableObject
{
    private ProxyModeChoice _selectedMode;
    private string _pacUrl = "";
    private string _server = "";
    private string _bypass = "<local>";
    private string _currentSummary = "";
    private string _winHttpSummary = "";
    private ProxyModeChoice _selectedWinHttpMode;
    private string _winHttpServer = "";
    private string _winHttpBypass = "<local>";
    private string _resultText = "";
    private bool _isBusy;

    public ProxyViewModel()
    {
        Modes =
        [
            new ProxyModeChoice(ProxyMode.None, "使わない(直接接続)"),
            new ProxyModeChoice(ProxyMode.Pac, "PAC(自動構成スクリプト)"),
            new ProxyModeChoice(ProxyMode.Fixed, "固定のプロキシサーバ"),
        ];
        _selectedMode = Modes[0];

        // WinHTTP は PAC を持てない仕様。選ばせて「使えません」と言うより、
        // はじめから出さないほうが分かりやすい
        WinHttpModes =
        [
            new ProxyModeChoice(ProxyMode.None, "使わない(直接接続)"),
            new ProxyModeChoice(ProxyMode.Fixed, "固定のプロキシサーバ"),
        ];
        _selectedWinHttpMode = WinHttpModes[0];

        ApplyCommand = new RelayCommand(Apply);
        ApplyWinHttpCommand = new RelayCommand(() => _ = ApplyWinHttpAsync(), () => !_isBusy);
    }

    public ProxyModeChoice[] Modes { get; }

    /// <summary>WinHTTP 側の選択肢。PAC は無い。</summary>
    public ProxyModeChoice[] WinHttpModes { get; }

    public RelayCommand ApplyCommand { get; }

    /// <summary>
    /// <b>WinHTTP（PC 全体）</b>に適用する。
    /// 管理者権限が要るので、適用の瞬間だけ昇格する（IP設定の適用と同じ作り）。
    /// </summary>
    public RelayCommand ApplyWinHttpCommand { get; }

    public ProxyModeChoice SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is not null && SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(IsPac));
                OnPropertyChanged(nameof(IsFixed));
            }
        }
    }

    public bool IsPac => _selectedMode.Mode == ProxyMode.Pac;
    public bool IsFixed => _selectedMode.Mode == ProxyMode.Fixed;

    public string PacUrl { get => _pacUrl; set => SetProperty(ref _pacUrl, value); }
    public string Server { get => _server; set => SetProperty(ref _server, value); }
    public string Bypass { get => _bypass; set => SetProperty(ref _bypass, value); }

    // ===== WinHTTP（PC 全体）=====

    public ProxyModeChoice SelectedWinHttpMode
    {
        get => _selectedWinHttpMode;
        set
        {
            if (value is not null && SetProperty(ref _selectedWinHttpMode, value))
                OnPropertyChanged(nameof(WinHttpIsFixed));
        }
    }

    public bool WinHttpIsFixed => _selectedWinHttpMode.Mode == ProxyMode.Fixed;

    public string WinHttpServer { get => _winHttpServer; set => SetProperty(ref _winHttpServer, value); }
    public string WinHttpBypass { get => _winHttpBypass; set => SetProperty(ref _winHttpBypass, value); }

    /// <summary>いまの Windows 側の設定。</summary>
    public string CurrentSummary
    {
        get => _currentSummary;
        private set => SetProperty(ref _currentSummary, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    /// <summary>いまの WinHTTP（PC 全体）の設定。読むだけなら管理者権限は要らない。</summary>
    public string WinHttpSummary
    {
        get => _winHttpSummary;
        private set => SetProperty(ref _winHttpSummary, value);
    }

    /// <summary>タブを開いたときに現在値を読み込み、入力欄へ取り込む。</summary>
    public void Refresh()
    {
        ProxyState state = ProxySettings.Read();

        CurrentSummary = state.Summary;
        SelectedMode = Modes.First(m => m.Mode == state.Mode);
        if (state.PacUrl.Length > 0)
            PacUrl = state.PacUrl;
        if (state.Server.Length > 0)
            Server = state.Server;
        if (state.Bypass.Length > 0)
            Bypass = state.Bypass;

        RefreshWinHttp();
    }

    /// <summary>
    /// いまの WinHTTP 設定を読み、入力欄にも取り込む。
    /// ユーザー側と同じ作法 — 開いたら現状が入っていて、そこから直せる。
    /// </summary>
    private void RefreshWinHttp()
    {
        if (Interop.WinHttpNativeMethods.ReadDefaultProxy() is not { } winHttp)
        {
            WinHttpSummary = "読み取れませんでした";
            return;
        }

        WinHttpSummary = WinHttpProxyScript.Describe(winHttp.Direct, winHttp.Server, winHttp.Bypass);

        SelectedWinHttpMode = WinHttpModes.First(
            m => m.Mode == (winHttp.Direct ? ProxyMode.None : ProxyMode.Fixed));

        if (winHttp.Server.Length > 0) WinHttpServer = winHttp.Server;
        if (winHttp.Bypass.Length > 0) WinHttpBypass = winHttp.Bypass;
    }

    /// <summary>
    /// WinHTTP へ適用する。<b>上段とは独立した設定</b>なので、こちらの欄から組み立てる。
    /// <b>PAC は選べない</b>（WinHTTP の既定設定は「直接」か「固定」しか持てない）ので、
    /// そもそも選択肢に出していない。
    /// </summary>
    private async Task ApplyWinHttpAsync()
    {
        ProxyPlan? plan = ProxyPlan.Parse(
            SelectedWinHttpMode.Mode, "", WinHttpServer, WinHttpBypass, out string? error);

        if (plan is null)
        {
            ResultText = error ?? "入力内容を確かめてください。";
            return;
        }

        await RunNetshAsync(WinHttpProxyScript.Build(plan),
                            plan.Mode == ProxyMode.Fixed
                                ? "✓ WinHTTP（PC 全体）に適用しました。"
                                : "✓ WinHTTP（PC 全体）のプロキシを解除しました。");
    }

    private async Task RunNetshAsync(IReadOnlyList<string> script, string done)
    {
        _isBusy = true;
        ApplyWinHttpCommand.RaiseCanExecuteChanged();
        ResultText = "管理者権限で適用しています…";

        try
        {
            ResultText = await ElevatedNetsh.ApplyAsync(script) ?? done;
        }
        finally
        {
            _isBusy = false;
            ApplyWinHttpCommand.RaiseCanExecuteChanged();
            RefreshWinHttp();
        }
    }

    public void Reset()
    {
        PacUrl = "";
        Server = "";
        Bypass = "<local>";
        ResultText = "";
    }

    private void Apply()
    {
        ProxyPlan? plan = ProxyPlan.Parse(SelectedMode.Mode, PacUrl, Server, Bypass, out string? error);
        if (plan is null)
        {
            ResultText = error ?? "入力内容を確かめてください。";
            return;
        }

        string? failure = ProxySettings.Apply(plan);
        if (failure is not null)
        {
            ResultText = failure;
            return;
        }

        Refresh();
        ResultText = "✓ 適用しました(動作中のアプリには再起動後に反映されることがあります)。";
    }
}
