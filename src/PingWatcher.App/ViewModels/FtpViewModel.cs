using PingWatcher.App.Services;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// FTP サーバ画面。現場で機器の config バックアップを受けるための使い捨てサーバ。
/// 共通部分は <see cref="FileServerViewModel"/>。ここは FTP 固有の差だけ。
/// </summary>
public sealed class FtpViewModel : FileServerViewModel
{
    private string _user = string.Empty;
    private string _password = string.Empty;

    public FtpViewModel(string? localAddress) : base("ftp", 21, localAddress)
    {
    }

    public override string RootDirectory => AppData.PathOf("ftp");

    public string User
    {
        get => _user;
        set { if (SetProperty(ref _user, value)) RefreshCommandHint(); }
    }

    public string Password
    {
        get => _password;
        set { if (SetProperty(ref _password, value)) RefreshCommandHint(); }
    }

    public override string CommandHint
    {
        get
        {
            string credential = string.IsNullOrEmpty(User)
                ? string.Empty
                : string.IsNullOrEmpty(Password) ? $"{User}@" : $"{User}:{Password}@";

            return $"copy running-config ftp://{credential}{HostForHint}/running-config\n" +
                   "（機器が繋がらないときは機器側で「ip ftp passive」を設定）";
        }
    }

    protected override IFileServer CreateServer(int port)
        => new FtpServer(RootDirectory, User, Password);
}
