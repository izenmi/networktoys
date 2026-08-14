namespace PastelNet.App.ViewModels;

/// <summary>
/// ウィンドウ全体の入れ物。タブごとに DataContext を切り替えて使う。
/// </summary>
public sealed class ShellViewModel
{
    public ShellViewModel()
    {
        Monitor = new MonitorViewModel();

        // 現在の DNS サーバを比較対象の既定値として渡す
        Dns = new DnsViewModel(Monitor.SystemDnsServers);
        Trace = new TraceViewModel();

        // 自分のサブネットをスキャン範囲の既定値にしておく（現場で最もよく使う操作）
        Scan = new ScanViewModel(Monitor.SubnetCidr, Monitor.AppendToTargetList);
    }

    public MonitorViewModel Monitor { get; }

    public DnsViewModel Dns { get; }

    public TraceViewModel Trace { get; }

    public ScanViewModel Scan { get; }
}
