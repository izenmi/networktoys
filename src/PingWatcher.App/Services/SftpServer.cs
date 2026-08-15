using System.IO;
using System.Net;
using FxSsh;
using FxSsh.Services;
using PingWatcher.App.Vendor;

namespace PingWatcher.App.Services;

/// <summary>
/// 使い捨ての SFTP サーバ。SSH の上で動くので、SSH 層は FxSsh に任せ、
/// SFTP サブシステムのファイル操作を <c>sftp</c> フォルダに閉じ込めて橋渡しする。
///
/// ホスト鍵は初回に生成して保存する（毎回変わると WinSCP 等が
/// 「鍵が変わった」と警告するため）。認証は既定 anonymous、任意でパス固定。
/// </summary>
internal sealed class SftpServer : IFileServer
{
    private readonly string _rootDirectory;
    private readonly string _hostKeyPath;
    private readonly string? _user;
    private readonly string? _password;

    private SshServer? _ssh;

    public SftpServer(string rootDirectory, string hostKeyPath, string? user = null, string? password = null)
    {
        _rootDirectory = rootDirectory;
        _hostKeyPath = hostKeyPath;
        _user = string.IsNullOrEmpty(user) ? null : user;
        _password = password;
    }

    public event Action<FileServerEvent>? Event;

    public bool IsRunning => _ssh is not null;

    public void Start(int port)
    {
        if (IsRunning) return;

        Directory.CreateDirectory(_rootDirectory);

        var server = new SshServer(new StartingInfo(IPAddress.Any, port, "SSH-2.0-PingWatcher"));

        foreach ((string algorithm, string pem) in LoadOrCreateHostKeys())
            server.AddHostKey(algorithm, pem);

        server.ConnectionAccepted += OnConnectionAccepted;
        server.Start();

        _ssh = server;
    }

    public void Stop()
    {
        if (_ssh is null) return;

        _ssh.ConnectionAccepted -= OnConnectionAccepted;
        _ssh.Stop();
        _ssh = null;
    }

    private void OnConnectionAccepted(object? sender, Session session)
    {
        string remote = "?";
        Raise(remote, "接続しました");

        session.ServiceRegistered += (_, service) => OnServiceRegistered(session, service, remote);
    }

    private void OnServiceRegistered(Session session, SshService service, string remote)
    {
        switch (service)
        {
            case UserAuthService auth:
                // パス未設定なら誰でも（none 認証）。設定してあればパスワードで照合する
                auth.EnableNoneAuth = _user is null;
                auth.UserAuth += (_, e) => e.Result = Authenticate(e);
                break;

            case ConnectionService connection:
                connection.CommandOpened += (_, e) => OnCommandOpened(e, remote);
                break;
        }
    }

    private bool Authenticate(UserAuthArgs e)
    {
        if (_user is null) return true;

        // FxSsh のパスワード認証は Username/Password を持つ
        return string.Equals(e.Username, _user, StringComparison.Ordinal)
               && (_password is null || string.Equals(e.Password, _password, StringComparison.Ordinal));
    }

    private void OnCommandOpened(CommandRequestedArgs e, string remote)
    {
        // SFTP サブシステムだけ受ける（shell/exec は開かない）
        bool isSftp = e.ShellType == "subsystem" && e.CommandText == "sftp";
        e.Agreed = isSftp;
        if (!isSftp) return;

        var sftp = new SftpService(_rootDirectory);
        e.Channel.DataReceived += (_, data) => sftp.OnData(data);
        e.Channel.CloseReceived += (_, _) => sftp.OnClose();
        sftp.DataReceived += (_, data) => e.Channel.SendData(data);
        sftp.CloseReceived += (_, code) => e.Channel.SendClose(code);

        Raise(remote, "SFTP を開始しました");
    }

    /// <summary>
    /// ホスト鍵を読み込む。無ければ RSA と ECDSA を生成して保存する。
    /// ファイルは「アルゴリズム名 TAB PEM(改行は \n に退避)」の 1 行 1 鍵。
    /// </summary>
    private IReadOnlyList<(string Algorithm, string Pem)> LoadOrCreateHostKeys()
    {
        if (File.Exists(_hostKeyPath))
        {
            try
            {
                var loaded = new List<(string, string)>();
                foreach (string line in File.ReadAllLines(_hostKeyPath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0)
                        loaded.Add((line[..tab], line[(tab + 1)..].Replace("\\n", "\n")));
                }

                if (loaded.Count > 0) return loaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めなければ作り直す
            }
        }

        string rsa = KeyGenerator.GenerateRsaKeyPem(2048);
        string ecdsa = KeyGenerator.GenerateECDsaKeyPem("nistp256");

        var keys = new List<(string Algorithm, string Pem)>
        {
            ("rsa-sha2-256", rsa),
            ("rsa-sha2-512", rsa),
            ("ecdsa-sha2-nistp256", ecdsa),
        };

        try
        {
            File.WriteAllLines(_hostKeyPath, keys.Select(k => $"{k.Algorithm}\t{k.Pem.Replace("\n", "\\n")}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても起動はできる（次回また生成される＝警告は出る）
        }

        return keys;
    }

    private void Raise(string remote, string text)
        => Event?.Invoke(new FileServerEvent(DateTime.Now, remote, text));

    public void Dispose() => Stop();
}
