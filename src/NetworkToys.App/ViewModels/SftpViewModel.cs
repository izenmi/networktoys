using NetworkToys.App.Services;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// SFTP サーバ画面。SSH 越しの安全なファイル転送。SSH 層は FxSsh に任せる。
/// 共通部分は <see cref="FileServerViewModel"/>。
/// </summary>
public sealed class SftpViewModel : FileServerViewModel
{
    private string _user = string.Empty;
    private string _password = string.Empty;

    public SftpViewModel(string? localAddress) : base("sftp", 22, localAddress)
    {
    }

    public override string RootDirectory => AppData.PathOf("sftp");

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
            string user = string.IsNullOrEmpty(User) ? "任意のユーザー" : User;

            return $"WinSCP などで sftp://{user}@{HostForHint}:{Port} に接続します。\n" +
                   "初回はホスト鍵の確認が出ます（2 回目以降は出ません）。";
        }
    }

    private protected override IFileServer CreateServer(int port)
        => new SftpServer(RootDirectory, AppData.PathOf("sftp-hostkey.txt"), User, Password);
}
