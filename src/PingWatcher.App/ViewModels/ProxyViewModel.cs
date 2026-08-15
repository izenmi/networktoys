using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Net;

namespace PingWatcher.App.ViewModels;

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
    private string _resultText = "";

    public ProxyViewModel()
    {
        Modes =
        [
            new ProxyModeChoice(ProxyMode.None, "使わない(直接接続)"),
            new ProxyModeChoice(ProxyMode.Pac, "PAC(自動構成スクリプト)"),
            new ProxyModeChoice(ProxyMode.Fixed, "固定のプロキシサーバ"),
        ];
        _selectedMode = Modes[0];

        ApplyCommand = new RelayCommand(Apply);
    }

    public ProxyModeChoice[] Modes { get; }

    public RelayCommand ApplyCommand { get; }

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
