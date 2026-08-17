using NetworkToys.App.Services;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// SNMP Trap の受信画面。共通部分は <see cref="FileServerViewModel"/>
/// （開始/停止・履歴・ログ）。UDP/162 で待ち受ける。
/// </summary>
public sealed class SnmpTrapViewModel : FileServerViewModel
{
    public SnmpTrapViewModel(string? localAddress) : base("snmptrap", 162, localAddress)
    {
    }

    // 受け取るだけでフォルダは使わないが、基底がログ用に要求するので logs 直下を返す
    public override string RootDirectory => SessionLogService.LogsDirectory;

    public override string CommandHint
        => $"機器側で trap の送信先を {HostForHint} に設定すると、ここに届きます。\n" +
           "（例: Cisco IOS の「snmp-server host <このPCのIP> version 2c <community>」）";

    private protected override IFileServer CreateServer(int port) => new SnmpTrapReceiver();
}
