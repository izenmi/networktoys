using System.Collections.ObjectModel;
using System.Windows.Threading;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Addressing;
using NetworkToys.Core.Storage;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// IP設定タブ。アダプタを選び、DHCP か固定 IP を「適用」で反映する。
///
/// 表示・編集・検証は非管理者のまま。適用の瞬間だけ netsh を UAC 昇格で
/// 起動する(<see cref="ElevatedNetsh"/>)。プリセットは settings.json に持ち、
/// アダプタ名は含めない(適用先は毎回選ぶ)。
/// </summary>
public sealed class IpConfigViewModel : ObservableObject
{
    private static readonly TimeSpan ValidateDebounce = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _debounce;

    private NetworkAdapterInfo? _selectedAdapter;
    private bool _useDhcp;
    private string _address = "";
    private string _mask = "";
    private string _gateway = "";
    private string _dns1 = "";
    private string _dns2 = "";
    private string _validationText = "";
    private bool _hasError;
    private IpPreset? _selectedPreset;
    private string _presetName = "";
    private bool _isApplying;
    private string _resultText = "";

    public IpConfigViewModel()
    {
        ApplyPresetToFields = true;

        SavePresetCommand = new RelayCommand(SavePreset, () => PresetName.Trim().Length > 0);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPreset is not null);
        LoadPresetCommand = new RelayCommand(
            () => Expand(SelectedPreset), () => SelectedPreset is not null);
        RefreshCommand = new RelayCommand(() => RefreshAdapters());

        foreach (IpPreset preset in Settings.Current.IpPresets)
            Presets.Add(preset);

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = ValidateDebounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Validate();
        };
    }

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];

    public ObservableCollection<IpPreset> Presets { get; } = [];

    /// <summary>同じタブに置くプロキシ設定のセクション。</summary>
    public ProxyViewModel Proxy { get; } = new();

    public RelayCommand SavePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }

    /// <summary>
    /// 選んでいるプリセットを入力欄へ入れ直す。
    ///
    /// <b>コンボボックスは同じ項目を選び直しても何も起きない</b>（選択が変わらないので
    /// 通知が飛ばない）。入力欄をいじったあとに「プリセットの値に戻したい」が
    /// できなかったので、押せば何度でも戻せる口を分けてある（2026-08-17 指摘）。
    /// </summary>
    public RelayCommand LoadPresetCommand { get; }
    public RelayCommand RefreshCommand { get; }

    /// <summary>プリセット選択で入力欄へ展開するか。読み込み中の一時抑止に使う。</summary>
    private bool ApplyPresetToFields { get; set; }

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (!SetProperty(ref _selectedAdapter, value))
                return;

            // 選んだアダプタの現在値を入力欄へ取り込む(そこから書き換えるのが最短)
            if (value is not null)
            {
                ApplyPresetToFields = false;
                UseDhcp = value.IsDhcp;
                Address = value.Address?.ToString() ?? "";
                Mask = value.PrefixLength > 0 ? value.PrefixLength.ToString() : "";
                Gateway = value.Gateway?.ToString() ?? "";
                Dns1 = value.DnsServers.Count > 0 ? value.DnsServers[0].ToString() : "";
                Dns2 = value.DnsServers.Count > 1 ? value.DnsServers[1].ToString() : "";
                ApplyPresetToFields = true;
            }

            Validate();
        }
    }

    public bool UseDhcp
    {
        get => _useDhcp;
        set
        {
            if (SetProperty(ref _useDhcp, value))
            {
                OnPropertyChanged(nameof(UseStatic));
                Validate();
            }
        }
    }

    /// <summary>ラジオの相方。コンバータを新設するより素直に 2 本持つ。</summary>
    public bool UseStatic
    {
        get => !_useDhcp;
        set => UseDhcp = !value;
    }

    public string Address { get => _address; set { if (SetProperty(ref _address, value)) Revalidate(); } }
    public string Mask { get => _mask; set { if (SetProperty(ref _mask, value)) Revalidate(); } }
    public string Gateway { get => _gateway; set { if (SetProperty(ref _gateway, value)) Revalidate(); } }
    public string Dns1 { get => _dns1; set { if (SetProperty(ref _dns1, value)) Revalidate(); } }
    public string Dns2 { get => _dns2; set { if (SetProperty(ref _dns2, value)) Revalidate(); } }

    public string ValidationText
    {
        get => _validationText;
        private set => SetProperty(ref _validationText, value);
    }

    public bool CanApply => SelectedAdapter is not null && !_hasError && !IsApplying;

    public IpPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value))
                return;

            DeletePresetCommand.RaiseCanExecuteChanged();
            LoadPresetCommand.RaiseCanExecuteChanged();

            if (!ApplyPresetToFields)
                return;

            Expand(value);
        }
    }

    /// <summary>
    /// プリセットの中身を入力欄へ展開する。<b>展開するだけで適用はしない</b>
    /// （適用は必ず「適用」ボタンから）。
    /// </summary>
    private void Expand(IpPreset? preset)
    {
        if (preset is null) return;

        PresetName = preset.Name;
        UseDhcp = preset.Dhcp;
        Address = preset.Address;
        Mask = preset.Mask;
        Gateway = preset.Gateway;
        Dns1 = preset.Dns1;
        Dns2 = preset.Dns2;
        Validate();
    }

    public string PresetName
    {
        get => _presetName;
        set
        {
            if (SetProperty(ref _presetName, value))
                SavePresetCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
                OnPropertyChanged(nameof(CanApply));
        }
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    /// <summary>タブが表示されたときに呼ばれる。列挙は選択時だけで、タイマーは持たない。</summary>
    public void OnActivated()
    {
        RefreshAdapters();
        Proxy.Refresh();
    }

    public void RefreshAdapters()
    {
        string? keep = SelectedAdapter?.Name;

        Adapters.Clear();
        foreach (NetworkAdapterInfo adapter in NetworkEnvironment.ListAdapters())
            Adapters.Add(adapter);

        SelectedAdapter = Adapters.FirstOrDefault(a => a.Name == keep) ?? Adapters.FirstOrDefault();
    }

    /// <summary>確認ダイアログに出す要約。</summary>
    public string ConfirmationSummary()
    {
        if (SelectedAdapter is not { } adapter)
            return "";

        if (UseDhcp)
            return $"アダプタ「{adapter.Name}」を DHCP(自動取得)に切り替えます。";

        string gateway = Gateway.Trim().Length > 0 ? $"\nゲートウェイ: {Gateway.Trim()}" : "\nゲートウェイ: (なし)";
        string dns = Dns1.Trim().Length > 0
            ? $"\nDNS: {Dns1.Trim()}{(Dns2.Trim().Length > 0 ? " / " + Dns2.Trim() : "")}"
            : "\nDNS: (クリア)";

        return $"アダプタ「{adapter.Name}」に次の固定 IP を設定します。\n\nIP: {Address.Trim()} / {Mask.Trim()}{gateway}{dns}";
    }

    /// <summary>適用する。確認ダイアログを通ってから呼ばれる前提。</summary>
    public async Task ApplyAsync()
    {
        if (SelectedAdapter is not { } adapter || IsApplying)
            return;

        IpPlan? plan = IpPlan.Parse(adapter.Name, UseDhcp, Address, Mask, Gateway, Dns1, Dns2,
            out string? error, out _);
        if (plan is null)
        {
            ResultText = error ?? "入力内容を確かめてください。";
            return;
        }

        IsApplying = true;
        ResultText = "適用しています…(UAC の確認が出ます)";

        try
        {
            string? failure = await ElevatedNetsh.ApplyAsync(plan.ToNetshScript());
            if (failure is not null)
            {
                ResultText = failure;
                return;
            }

            // 反映まで少し間があるので、待ってから現在値を読み直して突き合わせる
            await Task.Delay(1500);
            RefreshAdapters();

            NetworkAdapterInfo? current = Adapters.FirstOrDefault(a => a.Name == adapter.Name);
            bool confirmed = plan.Dhcp
                ? current?.IsDhcp == true
                : current?.Address?.Equals(plan.Address) == true;

            ResultText = confirmed
                ? "✓ 適用しました。"
                : "netsh は完了しましたが、まだ反映を確認できません(DHCP の取得待ちの可能性)。「再読み込み」で確かめてください。";
        }
        catch (Exception ex)
        {
            ResultText = "適用に失敗しました。";
            CrashLog.Write(ex, "IpConfigViewModel.ApplyAsync");
        }
        finally
        {
            IsApplying = false;
        }
    }

    public void Reset()
    {
        // プリセットは宛先リストと同じ扱いで残す
        UseDhcp = false;
        Address = Mask = Gateway = Dns1 = Dns2 = "";
        PresetName = "";
        SelectedPreset = null;
        ValidationText = "";
        ResultText = "";
        _hasError = false;
        OnPropertyChanged(nameof(CanApply));
        Proxy.Reset();
    }

    private void SavePreset()
    {
        string name = PresetName.Trim();
        if (name.Length == 0)
            return;

        // 同名は上書き(upsert)。重複を作らない
        IpPreset preset = Settings.Current.IpPresets.FirstOrDefault(p => p.Name == name) ?? new IpPreset { Name = name };
        preset.Dhcp = UseDhcp;
        preset.Address = Address.Trim();
        preset.Mask = Mask.Trim();
        preset.Gateway = Gateway.Trim();
        preset.Dns1 = Dns1.Trim();
        preset.Dns2 = Dns2.Trim();

        if (!Settings.Current.IpPresets.Contains(preset))
            Settings.Current.IpPresets.Add(preset);
        if (!Presets.Contains(preset))
            Presets.Add(preset);

        try
        {
            Settings.Save();
            ResultText = $"プリセット「{name}」を保存しました。";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ResultText = $"プリセットを保存できませんでした: {ex.Message}";
        }
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { } preset)
            return;

        Settings.Current.IpPresets.Remove(preset);
        Presets.Remove(preset);
        SelectedPreset = null;

        try
        {
            Settings.Save();
            ResultText = $"プリセット「{preset.Name}」を削除しました。";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ResultText = $"プリセットを削除できませんでした: {ex.Message}";
        }
    }

    private void Revalidate()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Validate()
    {
        _debounce.Stop();

        if (SelectedAdapter is null)
        {
            _hasError = true;
            ValidationText = "";
            OnPropertyChanged(nameof(CanApply));
            return;
        }

        IpPlan? plan = IpPlan.Parse(SelectedAdapter.Name, UseDhcp, Address, Mask, Gateway, Dns1, Dns2,
            out string? error, out string? warning);

        _hasError = plan is null;
        ValidationText = error ?? (warning is null ? "" : $"⚠ {warning}");
        OnPropertyChanged(nameof(CanApply));
    }
}
