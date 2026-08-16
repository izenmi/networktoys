using System.IO;
using System.Net.Sockets;
using System.Text;
using PingWatcher.Core.Terminal;

namespace PingWatcher.App.Services;

/// <summary>収集の進み具合。</summary>
public sealed record CollectProgress(string Host, string Message);

/// <summary>1 台ぶんの指示。<b>パスワードはここに置くだけで、どこにも保存しない。</b></summary>
public sealed record CollectRequest(
    string Host,
    int Port,
    bool UseSsh,
    DeviceCredentials Credentials,
    string Memo);

/// <summary>
/// 機器へ接続して収集し、1 台 1 ファイルに落とす。
///
/// 実ネットワークに触れるのはここだけで、会話そのものは Core の
/// <see cref="CiscoSession"/> が持つ。Telnet も SSH も
/// <see cref="Stream"/> にして同じ状態機械へ渡す。
/// </summary>
internal static class DeviceCollector
{
    /// <summary>収集した出力を置くフォルダ。<b>logs は使わない</b>（30 日で自動削除されるため）。</summary>
    public static string RootDirectory => AppData.PathOf("collect");

    public static async Task<DeviceCollectionResult> CollectAsync(
        CollectRequest request,
        IReadOnlyList<string> commands,
        CiscoSessionOptions options,
        TimeSpan connectTimeout,
        IProgress<CollectProgress>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTime started = DateTime.Now;
        Stream? io = null;
        TcpClient? tcp = null;
        SshConnection? ssh = null;
        string? fingerprint = null;

        try
        {
            progress?.Report(new CollectProgress(request.Host, "接続しています…"));

            if (request.UseSsh)
            {
                // SSH の接続は同期 API なので、UI スレッドを塞がないよう逃がす
                ssh = new SshConnection();
                (Stream? shell, string? sshError) = await Task.Run(() =>
                {
                    Stream? opened = ssh.Open(
                        request.Host, request.Port, request.Credentials.UserName,
                        request.Credentials.Password, connectTimeout, out string? error);

                    return (opened, error);
                }, token).ConfigureAwait(false);

                if (shell is null)
                    return Failed(request, started, sshError ?? "SSH で接続できませんでした。");

                io = shell;
                fingerprint = ssh.HostKeyFingerprint;
            }
            else
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(connectTimeout);

                // 周期プローブの LingerOption(true, 0) をここへ流用しない。
                // あちらはデータを流さない測定用の最適化で、セッションでは行儀よく閉じる
                tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(request.Host, request.Port, connect.Token).ConfigureAwait(false);

                io = new TelnetStream(tcp.GetStream());
            }

            var session = new CiscoSession(io, options)
            {
                Progress = new Progress<string>(m => progress?.Report(new CollectProgress(request.Host, m))),
            };

            return await session.RunAsync(
                request.Host, request.Port, request.UseSsh, fingerprint,
                request.Credentials, commands, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Failed(request, started, "接続がタイムアウトしました。");
        }
        catch (OperationCanceledException)
        {
            return Failed(request, started, "中断しました。");
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return Failed(request, started, $"接続できませんでした: {ex.Message}");
        }
        finally
        {
            // 中断のときは接続を閉じることでブロック中の読み取りを抜けさせる
            io?.Dispose();
            tcp?.Dispose();
            ssh?.Dispose();
        }
    }

    /// <summary>1 台 1 ファイルで書き出す。書けなかったらその旨を返す。</summary>
    public static string? Save(string directory, DeviceCollectionResult result)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, DeviceReport.FileName(result));

            // BOM が無いとメモ帳と Excel で文字化けする
            File.WriteAllText(path, DeviceReport.Render(result),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"保存できませんでした: {ex.Message}";
        }
    }

    private static DeviceCollectionResult Failed(CollectRequest request, DateTime started, string message) => new(
        Host: request.Host,
        Port: request.Port,
        LearnedHostname: null,
        UserName: request.Credentials.UserName,
        UsedSsh: request.UseSsh,
        HostKeyFingerprint: null,
        StartedAt: started,
        FinishedAt: DateTime.Now,
        ReachedEnable: false,
        PagerNote: "未実施",
        Commands: [],
        FailureMessage: message);
}
