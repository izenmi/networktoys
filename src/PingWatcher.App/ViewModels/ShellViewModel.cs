namespace PingWatcher.App.ViewModels;

/// <summary>
/// ウィンドウ全体の入れ物。タブごとに DataContext を切り替えて使う。
/// </summary>
public sealed class ShellViewModel
{
    public ShellViewModel()
    {
        Monitor = new MonitorViewModel();

        // TCP は独立した画面にする。ICMP で見る相手とポートまで見る相手は別物なので、
        // 宛先リストも保存先も分けている
        Tcp = new MonitorViewModel("tcp-targets.json", alwaysTcp: true);

        // 現在の DNS サーバを比較対象の既定値として渡す
        Dns = new DnsViewModel(Monitor.SystemDnsServers);
        Trace = new TraceViewModel();

        // 自分のサブネットをスキャン範囲の既定値にしておく（現場で最もよく使う操作）
        Scan = new ScanViewModel(Monitor.SubnetCidr, Monitor.AppendToTargetList);

        // 電卓も自分のいるサブネットから始める
        Subnet = new SubnetViewModel(Monitor.SubnetCidr);

        // ファイル配布サーバ。機器側コマンド例に自分の IP を渡す
        Ftp = new FtpViewModel(Monitor.LocalAddress);
        Tftp = new TftpViewModel(Monitor.LocalAddress);
        Sftp = new SftpViewModel(Monitor.LocalAddress);

        // 無線は画面が開かれるまで API に触れない（位置情報の同意を求める時機のため）
        Wifi = new WifiViewModel();

        // 記録には無線の情報も載せるので、無線画面が持っている内容を参照させる
        Report = new ReportViewModel(Monitor, Wifi);
        Work = new WorkViewModel(Monitor);
        DeviceCompare = new DeviceCompareViewModel();
    }

    public MonitorViewModel Monitor { get; }

    /// <summary>TCP 接続で測る画面。宛先も結果も Ping とは別に持つ。</summary>
    public MonitorViewModel Tcp { get; }

    public DnsViewModel Dns { get; }

    public TraceViewModel Trace { get; }

    public ScanViewModel Scan { get; }

    /// <summary>サブネット電卓。</summary>
    public SubnetViewModel Subnet { get; }

    /// <summary>使い捨て FTP サーバ。</summary>
    public FtpViewModel Ftp { get; }

    /// <summary>使い捨て TFTP サーバ。</summary>
    public TftpViewModel Tftp { get; }

    /// <summary>使い捨て SFTP サーバ。</summary>
    public SftpViewModel Sftp { get; }

    public WifiViewModel Wifi { get; }

    public ReportViewModel Report { get; }

    /// <summary>変更作業の前後確認。このツールの主な使い道。</summary>
    public WorkViewModel Work { get; }

    /// <summary>機器の出力（show ip route / show run）を作業前後で見比べる。</summary>
    public DeviceCompareViewModel DeviceCompare { get; }

    /// <summary>
    /// 測った結果をすべて捨てて、起動直後の状態へ戻す。
    ///
    /// <b>宛先リストだけは残す。</b>次の現場でも同じ宛先を測ることが多く、
    /// 打ち直す手間の方が大きい。逆に、前の現場の測定結果が残っていると
    /// 作業前後の比較が狂うので、それ以外は入力欄も含めて戻す。
    ///
    /// 保存済みのレポートや作業セッションのファイルには触れない。
    /// </summary>
    public async Task ClearAllAsync()
    {
        await Monitor.ResetAsync();
        await Tcp.ResetAsync();
        Work.Reset();
        DeviceCompare.Reset();
        Scan.Reset();
        Subnet.Reset();
        Ftp.Reset();
        Tftp.Reset();
        Sftp.Reset();
        Dns.Reset();
        Trace.Reset();
        Wifi.Reset();
        Report.Reset();
    }
}
