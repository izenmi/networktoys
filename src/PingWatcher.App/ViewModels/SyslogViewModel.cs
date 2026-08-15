using PingWatcher.App.Services;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// syslog サーバ画面。機器からの syslog を受けて一覧に出す。
/// 共通部分は <see cref="FileServerViewModel"/>（開始/停止・履歴・ログ）。
/// </summary>
public sealed class SyslogViewModel : FileServerViewModel
{
    public SyslogViewModel(string? localAddress) : base("syslog", 514, localAddress)
    {
    }

    // 受け取るだけでフォルダは使わないが、基底がログ用に要求するので logs 直下を返す
    public override string RootDirectory => SessionLogService.LogsDirectory;

    public override string CommandHint
        => $"機器側で「logging {HostForHint}」を設定すると、ここに届きます。\n" +
           "（Cisco IOS の例。機器によって書き方は異なります）";

    private protected override IFileServer CreateServer(int port) => new SyslogReceiver();
}
