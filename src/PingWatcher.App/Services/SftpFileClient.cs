using System.IO;
using PingWatcher.Core.Files;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace PingWatcher.App.Services;

/// <summary>
/// SFTP のクライアント。SSH.NET の <see cref="SftpClient"/> を包むだけ。
///
/// <b>ホスト鍵は受け入れて指紋を控える。</b><see cref="SshConnection"/> と同じ方針で、
/// 理由もあちらのコメントに書いてある（初回確認のプロンプトを出す作りにすると、
/// 誰も見ていない最中に止まる）。<b>指紋は画面に出して、人が見比べられるようにする。</b>
///
/// SSH.NET の API は同期なので、呼ぶ側で <see cref="Task.Run"/> に逃がす
/// （<see cref="DeviceCollector"/> が SSH の接続でやっているのと同じ）。
/// </summary>
internal sealed class SftpFileClient : IRemoteFiles
{
    private SftpClient? _client;

    public string? Fingerprint { get; private set; }

    public async Task ConnectAsync(
        string host, int port, string userName, string password, CancellationToken token)
    {
        var info = new ConnectionInfo(host, port, userName,
            new PasswordAuthenticationMethod(userName, password))
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        var client = new SftpClient(info);

        client.HostKeyReceived += (_, e) =>
        {
            Fingerprint = e.FingerPrintSHA256 is { Length: > 0 } print ? "SHA256:" + print : null;
            e.CanTrust = true;
        };

        try
        {
            await Task.Run(client.Connect, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();

            throw Translate(ex);
        }

        _client = client;
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken token)
    {
        SftpClient client = Ready();

        try
        {
            IEnumerable<ISftpFile> files =
                await Task.Run(() => client.ListDirectory(path), token).ConfigureAwait(false);

            var entries = new List<RemoteEntry>();

            foreach (ISftpFile file in files)
            {
                // 「.」「..」は画面側で足すので捨てる
                if (file.Name is "." or "..") continue;

                entries.Add(new RemoteEntry(
                    file.Name,
                    file.IsDirectory,
                    file.IsDirectory ? 0 : file.Length,
                    file.LastWriteTime));
            }

            return entries;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public async Task DownloadAsync(
        string remotePath, string localPath,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        SftpClient client = Ready();

        try
        {
            long total = await Task.Run(() => client.Get(remotePath).Length, token).ConfigureAwait(false);

            await using Stream from = await Task.Run(
                () => client.OpenRead(remotePath), token).ConfigureAwait(false);

            await using var to = new FileStream(
                localPath, FileMode.Create, FileAccess.Write, FileShare.None,
                TransferBuffer.Size, useAsync: true);

            await TransferBuffer.CopyAsync(
                from, to, RemotePath.Name(remotePath), total, progress, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    public async Task UploadAsync(
        string localPath, string remotePath,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        SftpClient client = Ready();

        try
        {
            var info = new FileInfo(localPath);

            await using var from = new FileStream(
                localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                TransferBuffer.Size, useAsync: true);

            await using Stream to = await Task.Run(
                () => client.Create(remotePath), token).ConfigureAwait(false);

            await TransferBuffer.CopyAsync(
                from, to, info.Name, info.Length, progress, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    public async Task MakeDirectoryAsync(string path, CancellationToken token)
    {
        SftpClient client = Ready();

        try
        {
            await Task.Run(() => client.CreateDirectory(path), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken token)
    {
        SftpClient client = Ready();

        try
        {
            await Task.Run(
                () =>
                {
                    if (isDirectory) client.DeleteDirectory(path);
                    else client.DeleteFile(path);
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public async Task RenameAsync(string fromPath, string toPath, CancellationToken token)
    {
        SftpClient client = Ready();

        try
        {
            await Task.Run(() => client.RenameFile(fromPath, toPath), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public void Dispose()
    {
        try
        {
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SftpFileClient.Dispose");
        }

        _client = null;
    }

    private SftpClient Ready()
        => _client is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException("接続していません。");

    /// <summary>
    /// SSH.NET の例外を画面に出せる日本語にする。
    /// <see cref="SshConnection"/> の言い換えと文言を揃えてある。
    /// </summary>
    private static Exception Translate(Exception ex) => ex switch
    {
        SshAuthenticationException => new InvalidOperationException("ユーザー名かパスワードが違います。", ex),

        SftpPermissionDeniedException => new InvalidOperationException("権限がありません。", ex),

        SftpPathNotFoundException => new InvalidOperationException("その場所が見つかりません。", ex),

        // 古い機器では鍵交換や暗号方式が合わずに弾かれる。原文の方が手掛かりになる
        SshConnectionException => new InvalidOperationException($"SSH で接続できません: {ex.Message}", ex),

        SshOperationTimeoutException or System.Net.Sockets.SocketException
            => new InvalidOperationException("接続できません（応答がありません）。", ex),

        _ => ex,
    };
}
