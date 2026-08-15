using System.Collections.ObjectModel;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Snmp;

namespace PingWatcher.App.ViewModels;

/// <summary>取得結果の 1 行。</summary>
public sealed record SnmpRow(string Oid, string Name, string Type, string Value);

/// <summary>OID プリセットの 1 つ。</summary>
public sealed record OidPreset(string Label, string Oid)
{
    public override string ToString() => Label;
}

/// <summary>
/// SNMP GET / ウォーク画面。宛先とコミュニティと OID を指定して機器から値を取る。
/// </summary>
public sealed class SnmpGetViewModel : ObservableObject
{
    private readonly SnmpClient _client = new();

    private string _host = string.Empty;
    private string _community = "public";
    private SnmpVersion _version = SnmpVersion.V2c;
    private string _oidText = "1.3.6.1.2.1.1.1.0";
    private string _status = string.Empty;
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public SnmpGetViewModel(string? defaultHost)
    {
        _host = defaultHost ?? string.Empty;

        GetCommand = new RelayCommand(() => _ = RunAsync(walk: false), () => CanRun);
        WalkCommand = new RelayCommand(() => _ = RunAsync(walk: true), () => CanRun);
    }

    public IReadOnlyList<OidPreset> Presets { get; } =
    [
        new("sysDescr（機器の説明）", "1.3.6.1.2.1.1.1.0"),
        new("sysName（ホスト名）", "1.3.6.1.2.1.1.5.0"),
        new("sysUpTime（稼働時間）", "1.3.6.1.2.1.1.3.0"),
        new("sysContact（連絡先）", "1.3.6.1.2.1.1.4.0"),
        new("sysLocation（設置場所）", "1.3.6.1.2.1.1.6.0"),
        new("ifNumber（IF 数）", "1.3.6.1.2.1.2.1.0"),
        new("ifDescr（IF 名・要ウォーク）", "1.3.6.1.2.1.2.2.1.2"),
        new("ifOperStatus（IF 状態・要ウォーク）", "1.3.6.1.2.1.2.2.1.8"),
    ];

    public ObservableCollection<SnmpRow> Rows { get; } = [];

    public RelayCommand GetCommand { get; }
    public RelayCommand WalkCommand { get; }

    public string Host
    {
        get => _host;
        set { if (SetProperty(ref _host, value)) RaiseCanRun(); }
    }

    public string Community { get => _community; set => SetProperty(ref _community, value); }

    /// <summary>true=v2c, false=v1。トグル 1 つで済むよう bool にしている。</summary>
    public bool IsV2c
    {
        get => _version == SnmpVersion.V2c;
        set => SetProperty(ref _version, value ? SnmpVersion.V2c : SnmpVersion.V1, nameof(IsV2c));
    }

    public string OidText
    {
        get => _oidText;
        set { if (SetProperty(ref _oidText, value)) RaiseCanRun(); }
    }

    /// <summary>プリセットを選ぶと OID 欄へ入る。</summary>
    public OidPreset? SelectedPreset
    {
        get => null;
        set { if (value is not null) OidText = value.Oid; }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RaiseCanRun(); }
    }

    private bool CanRun => !IsBusy && Host.Trim().Length > 0 && Oid.Parse(OidText) is not null;

    private void RaiseCanRun()
    {
        GetCommand.RaiseCanExecuteChanged();
        WalkCommand.RaiseCanExecuteChanged();
    }

    private async Task RunAsync(bool walk)
    {
        if (!CanRun) return;

        Oid? oid = Oid.Parse(OidText);
        if (oid is null)
        {
            Status = "OID を正しく入力してください（例 1.3.6.1.2.1.1.1.0）。";
            return;
        }

        IsBusy = true;
        Rows.Clear();
        Status = walk ? "ウォークしています…" : "取得しています…";

        _cts = new CancellationTokenSource();

        try
        {
            SnmpResult result = walk
                ? await _client.WalkAsync(Host.Trim(), Community, _version, oid, _cts.Token)
                : await _client.GetAsync(Host.Trim(), Community, _version, [oid], _cts.Token);

            foreach (VarBind vb in result.VarBinds)
                Rows.Add(new SnmpRow(vb.Oid.Text, vb.Oid.DisplayName, vb.Value.TypeName, vb.Value.Display));

            Status = result.Success
                ? (result.Message ?? $"{Rows.Count} 件を取得しました。")
                : result.Message ?? "取得できませんでした。";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Reset()
    {
        _cts?.Cancel();
        Rows.Clear();
        Community = "public";
        OidText = "1.3.6.1.2.1.1.1.0";
        Status = string.Empty;
    }
}
