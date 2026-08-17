using System.IO;
using NetworkToys.Core.Files;

namespace NetworkToys.App.Services;

/// <summary>転送の進み具合。</summary>
/// <param name="Name">いま運んでいるファイル名。</param>
/// <param name="Done">運んだバイト数。</param>
/// <param name="Total">全体のバイト数。分からなければ 0。</param>
public readonly record struct TransferProgress(string Name, long Done, long Total)
{
    /// <summary>0〜100。全体が分からないときは 0 のまま。</summary>
    public double Percent => Total > 0 ? Math.Min(100, Done * 100.0 / Total) : 0;
}

/// <summary>
/// 接続先のファイルを触る口。<b>FTP と SFTP で同じ形</b>にして、
/// 画面が protocol を意識しないで済むようにする。
///
/// 実装が 2 つあるので抽象化する（1 つしか無いなら作らない）。
///
/// <b>どのメソッドも、失敗は例外で伝える。</b>protocol ごとに理由の出方が
/// 違うので、日本語にするのは実装側の仕事
/// （<see cref="SshConnection"/> が例外を日本語へ言い換えているのと同じ流儀）。
/// </summary>
internal interface IRemoteFiles : IDisposable
{
    /// <summary>受け入れた鍵の指紋。SFTP だけ。証跡として画面に出す。</summary>
    string? Fingerprint { get; }

    /// <summary>つなぐ。</summary>
    Task ConnectAsync(string host, int port, string userName, string password, CancellationToken token);

    /// <summary>一覧を取る。<c>.</c> と <c>..</c> は含めない（画面側で足す）。</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken token);

    /// <summary>接続先 → この PC。</summary>
    Task DownloadAsync(string remotePath, string localPath,
                       IProgress<TransferProgress>? progress, CancellationToken token);

    /// <summary>この PC → 接続先。</summary>
    Task UploadAsync(string localPath, string remotePath,
                     IProgress<TransferProgress>? progress, CancellationToken token);

    /// <summary>フォルダを作る。</summary>
    Task MakeDirectoryAsync(string path, CancellationToken token);

    /// <summary>消す。フォルダかどうかで送るものが変わるので受け取る。</summary>
    Task DeleteAsync(string path, bool isDirectory, CancellationToken token);

    /// <summary>名前を変える。</summary>
    Task RenameAsync(string fromPath, string toPath, CancellationToken token);
}

/// <summary>接続先の種類。画面のコンボに出す。</summary>
internal enum RemoteKind
{
    Sftp,
    Ftp,
}

/// <summary>転送のときの読み書きの単位。大きすぎると中断の効きが鈍る。</summary>
internal static class TransferBuffer
{
    public const int Size = 64 * 1024;

    /// <summary>
    /// 進み具合を知らせながら丸ごと流す。
    ///
    /// <b>知らせるのは 100 ミリ秒に 1 回まで。</b>64KB ごとに通知すると、
    /// 速い回線では毎秒何百回も画面を触ることになる
    /// （測定結果を 10Hz のポンプでまとめているのと同じ理由）。
    /// </summary>
    public static async Task CopyAsync(
        Stream from, Stream to, string name, long total,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        var buffer = new byte[Size];
        long done = 0;
        long lastReport = -1;
        int read;

        while ((read = await from.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

            done += read;

            long now = Environment.TickCount64;

            if (lastReport < 0 || now - lastReport >= 100)
            {
                progress?.Report(new TransferProgress(name, done, total));
                lastReport = now;
            }
        }

        // 最後の 1 回は必ず出す。100% にならないまま終わって見えるのを避ける
        progress?.Report(new TransferProgress(name, done, total));
    }
}
