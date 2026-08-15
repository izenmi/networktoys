using System.Collections.ObjectModel;
using System.Net;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;

namespace PingWatcher.App.ViewModels;

/// <summary>1 台の DNS サーバへの問い合わせ結果。横並びで見比べるための 1 枚。</summary>
public sealed class DnsServerResultViewModel : ObservableObject
{
    private string _summary = "問い合わせ中…";
    private bool _hasError;
    private bool _isDone;

    public DnsServerResultViewModel(string serverLabel)
    {
        ServerLabel = serverLabel;
    }

    public string ServerLabel { get; }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool IsDone
    {
        get => _isDone;
        private set => SetProperty(ref _isDone, value);
    }

    public ObservableCollection<DnsRecordLine> Records { get; } = [];

    internal void Apply(DnsLookupResult result)
    {
        Records.Clear();
        foreach (DnsRecordLine record in result.Records)
            Records.Add(record);

        Summary = result.Summary;
        HasError = !result.Success;
        IsDone = true;
    }
}

/// <summary>
/// DNS テスト画面。
///
/// 同じ名前を複数のサーバへ<b>同時に</b>投げて横並びで比べられるようにしている。
/// 現場で知りたいのは「引けるか」ではなく「どのサーバでは引けてどこでは引けないか」なので、
/// 1 台ずつ試すより比較の形にした方が早い。
/// </summary>
public sealed class DnsViewModel : ObservableObject
{
    private const int TimeoutMs = 3000;

    private string _name = "www.google.com";
    private string _selectedType = "A";
    private string _serversText = string.Empty;
    private string _status = string.Empty;

    /// <summary>起動時に組み立てた比較先。クリアで戻すために覚えておく。</summary>
    private readonly string _defaultServersText;
    private bool _isBusy;

    public DnsViewModel(IReadOnlyList<IPAddress> systemDnsServers)
    {
        var servers = new List<string> { DnsProbe.SystemResolver };
        servers.AddRange(systemDnsServers.Select(a => a.ToString()));

        // 社内 DNS と外部 DNS の食い違いは現場で頻出するので、既定で比較対象に入れておく
        if (!servers.Contains("8.8.8.8", StringComparer.Ordinal))
            servers.Add("8.8.8.8");
        if (!servers.Contains("1.1.1.1", StringComparer.Ordinal))
            servers.Add("1.1.1.1");

        _serversText = _defaultServersText = string.Join('\n', servers);

        QueryCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Name));
    }

    public IReadOnlyList<string> RecordTypes => DnsProbe.SupportedTypes;

    public ObservableCollection<DnsServerResultViewModel> Results { get; } = [];

    public RelayCommand QueryCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                QueryCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedType
    {
        get => _selectedType;
        set => SetProperty(ref _selectedType, value);
    }

    /// <summary>1 行に 1 台。「システム既定」と書くと OS のリゾルバを使う。</summary>
    public string ServersText
    {
        get => _serversText;
        set => SetProperty(ref _serversText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                QueryCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunAsync()
    {
        string[] servers = [.. ServersText
            .Split('\n')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)];

        if (servers.Length == 0)
        {
            Status = "問い合わせ先を 1 台以上指定してください。";
            return;
        }

        IsBusy = true;
        Status = $"{Name} を {servers.Length} 台へ同時に問い合わせています…";

        Results.Clear();
        var pending = new List<DnsServerResultViewModel>(servers.Length);
        foreach (string server in servers)
        {
            var vm = new DnsServerResultViewModel(server);
            Results.Add(vm);
            pending.Add(vm);
        }

        string name = Name.Trim();
        string type = SelectedType;

        try
        {
            // 全サーバへ同時に投げる。遅い 1 台が他を待たせない
            await Task.WhenAll(pending.Select(async vm =>
            {
                DnsLookupResult result = await DnsProbe.QueryAsync(vm.ServerLabel, name, type, TimeoutMs, CancellationToken.None);
                vm.Apply(result);
            }));

            int ok = pending.Count(r => !r.HasError);
            Status = $"{ok} / {pending.Count} 台から回答がありました。";
        }
        catch (Exception ex)
        {
            Status = $"問い合わせに失敗しました: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>結果と入力を起動時の状態へ戻す。</summary>
    public void Reset()
    {
        Results.Clear();
        Name = "www.google.com";
        SelectedType = "A";
        ServersText = _defaultServersText;
        Status = string.Empty;
    }

}
