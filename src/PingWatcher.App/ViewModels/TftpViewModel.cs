using PingWatcher.App.Services;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// TFTP サーバ画面。機器の config/イメージ転送用の使い捨てサーバ。
/// 共通部分は <see cref="FileServerViewModel"/>。TFTP は認証が無い。
/// </summary>
public sealed class TftpViewModel : FileServerViewModel
{
    public TftpViewModel(string? localAddress) : base("tftp", 69, localAddress)
    {
    }

    public override string RootDirectory => AppData.PathOf("tftp");

    public override string CommandHint
        => $"copy running-config tftp://{HostForHint}/running-config\n" +
           "（受信して保存するには、機器側でファイル名を指定します）";

    protected override IFileServer CreateServer(int port) => new TftpServer(RootDirectory);
}
