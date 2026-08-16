using PingWatcher.App.Mvvm;
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
        CopyCommandCommand = new RelayCommand(() => ClipboardText.Copy(CommandLine));
    }

    /// <summary>
    /// 画面に出す案内文はパスワードを伏せるので、実物はクリップボード経由で渡す。
    /// </summary>
    public RelayCommand CopyCommandCommand { get; }

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

    /// <summary>
    /// 機器へ打つコマンドの実物。<b>パスワードを含むので画面には出さない。</b>
    /// 「コマンドをコピー」からクリップボードへ渡すためだけに使う。
    /// </summary>
    private string CommandLine => $"copy running-config ftp://{Credential(masked: false)}{HostForHint}/running-config";

    /// <summary>
    /// 画面に出す案内。<b>パスワードは伏せる。</b>
    ///
    /// FTP は認証情報を URL に埋める書き方なので、そのまま出すと画面に平文で残り、
    /// F12 の画面保存にも焼き込まれる（Meraki の API キーや収集タブのパスワードを
    /// 伏せ字にしているのと同じ理由）。実物は「コマンドをコピー」で渡す。
    /// </summary>
    public override string CommandHint
        => $"copy running-config ftp://{Credential(masked: true)}{HostForHint}/running-config\n" +
           "（右の「コマンドをコピー」でパスワード入りの実物を写せます。"
         + "機器が繋がらないときは機器側で「ip ftp passive」を設定）";

    private string Credential(bool masked)
    {
        if (string.IsNullOrEmpty(User)) return string.Empty;
        if (string.IsNullOrEmpty(Password)) return $"{User}@";

        return masked ? $"{User}:********@" : $"{User}:{Password}@";
    }

    private protected override IFileServer CreateServer(int port)
        => new FtpServer(RootDirectory, User, Password);
}
