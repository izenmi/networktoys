using Renci.SshNet;
using Renci.SshNet.Common;

namespace PingWatcher.App.Services;

/// <summary>SSH のシェルを 1 本開く。開けたら <see cref="Stream"/> として渡す。</summary>
internal sealed class SshConnection : IDisposable
{
    private SshClient? _client;
    private ShellStream? _shell;

    /// <summary>受け入れたホスト鍵の指紋。証跡として保存ファイルの見出しに載せる。</summary>
    public string? HostKeyFingerprint { get; private set; }

    /// <summary>
    /// つないでシェルを開く。失敗したら画面に出せる日本語を <paramref name="error"/> へ。
    ///
    /// <b>認証は 1 回だけ。失敗しても試し直さない</b>
    /// （TACACS+/AD 環境でアカウントを固めないため）。
    /// </summary>
    public Stream? Open(
        string host, int port, string userName, string password,
        TimeSpan timeout, out string? error)
    {
        error = null;

        try
        {
            var info = new ConnectionInfo(host, port, userName,
                new PasswordAuthenticationMethod(userName, password))
            {
                Timeout = timeout,
            };

            _client = new SshClient(info);

            // ホスト鍵は受け入れる。初回確認のプロンプトを出す作りにすると、
            // 誰も見ていない収集の最中に止まってしまう。代わりに指紋を記録に残す
            _client.HostKeyReceived += (_, e) =>
            {
                HostKeyFingerprint = "SHA256:" + Convert.ToBase64String(e.FingerPrintSHA256 ?? []).TrimEnd('=');
                e.CanTrust = true;
            };

            _client.Connect();

            // Cisco は exec チャネルを実装していないことが多く、pty 付きの
            // 対話シェルでないと enable にもページャ無効化にも届かない。
            // 行数を大きく取るのはページャ対策の保険(これを当てにはしない)
            _shell = _client.CreateShellStream("vt100", 200, 1000, 800, 600, 64 * 1024);
            return _shell;
        }
        catch (SshAuthenticationException)
        {
            error = "ユーザー名かパスワードが違います。";
        }
        catch (SshConnectionException ex)
        {
            // 古い機器では鍵交換や暗号方式が合わずに弾かれることがある
            // (no matching key exchange method found など)。原因の判断に要るので
            // 言い換えずにそのまま見せる
            error = $"SSH で接続できませんでした: {ex.Message}";
        }
        catch (Exception ex) when (ex is SshOperationTimeoutException or System.Net.Sockets.SocketException)
        {
            error = $"接続できませんでした: {ex.Message}";
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            error = $"SSH を開始できませんでした: {ex.Message}";
        }

        Dispose();
        return null;
    }

    public void Dispose()
    {
        try
        {
            _shell?.Dispose();
            _client?.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshConnectionException)
        {
            // 閉じるときの失敗は握る
        }
        finally
        {
            _shell = null;
            _client = null;
        }
    }
}
