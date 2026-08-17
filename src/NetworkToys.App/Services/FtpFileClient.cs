using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkToys.Core.Files;
using NetworkToys.Core.Ftp;

namespace NetworkToys.App.Services;

/// <summary>
/// FTP のクライアント。<b>自前で書いている。</b>
/// <c>FtpWebRequest</c> は廃止済み（SYSLIB0014）で、このリポジトリは
/// 警告をエラーにするのでビルドが通らない。
///
/// 決めごと:
/// <list type="bullet">
///   <item><b>PASV だけを使う。</b>PORT はこちらが待ち受ける形になり、
///         ファイアウォールと NAT で詰む（サーバ側が PORT を持っているのは受ける側だから）</item>
///   <item><b>転送は <c>TYPE I</c> 固定。</b>ASCII モードは持たない —
///         改行を勝手に変換され、設定ファイルが壊れる事故のもとになる</item>
///   <item><b>PASV が返すアドレスは使わない。</b>NAT の内側のサーバは自分の
///         プライベートアドレスを答えるので、そこへ繋ぐと届かない。
///         <b>ポートだけ受け取り、繋ぐ先は制御接続と同じ相手</b>にする</item>
///   <item><b><c>LingerOption(true, 0)</c> を使わない。</b>あれは測定用ソケットの
///         決まりで、ファイル転送では最後まで送り切る必要がある</item>
/// </list>
///
/// 一覧の解釈は <see cref="FtpListing"/>、応答の組み立ては
/// <see cref="FtpReplyReader"/> にあり、どちらも CI で固めてある。
/// ここに残るのはソケットの往復だけ。
/// </summary>
internal sealed class FtpFileClient : IRemoteFiles
{
    private TcpClient? _control;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private readonly FtpReplyReader _replies = new();

    private IPAddress? _serverAddress;
    private bool _hasMachineListing;

    /// <summary>FTP に鍵は無い。</summary>
    public string? Fingerprint => null;

    public async Task ConnectAsync(
        string host, int port, string userName, string password, CancellationToken token)
    {
        var control = new TcpClient();

        try
        {
            await control.ConnectAsync(host, port, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            control.Dispose();

            throw new InvalidOperationException($"{host}:{port} へ接続できません。", ex);
        }

        _control = control;
        _stream = control.GetStream();

        // 文字コードはサーバ任せ。いまどきのサーバは UTF-8 なのでそれで読み、
        // 化けても止まらないよう置換にする（例外にすると一覧が丸ごと出なくなる）
        _reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        _serverAddress = (control.Client.RemoteEndPoint as IPEndPoint)?.Address;

        try
        {
            await ReadReplyAsync(token).ConfigureAwait(false);   // 220 の挨拶

            FtpReply user = await SendAsync($"USER {userName}", token).ConfigureAwait(false);

            // 331 ならパスワードを待っている。230 なら anonymous で通った
            if (user.NeedsMore)
            {
                FtpReply pass = await SendAsync($"PASS {password}", token).ConfigureAwait(false);

                if (!pass.IsSuccess)
                    throw new InvalidOperationException("ユーザー名かパスワードが違います。");
            }
            else if (!user.IsSuccess)
            {
                throw new InvalidOperationException("ユーザー名かパスワードが違います。");
            }

            // FEAT は持たないサーバがある。失敗しても続ける
            FtpReply features = await SendAsync("FEAT", token).ConfigureAwait(false);

            _hasMachineListing = features.Text.Contains("MLSD", StringComparison.OrdinalIgnoreCase);

            if (features.Text.Contains("UTF8", StringComparison.OrdinalIgnoreCase))
                await SendAsync("OPTS UTF8 ON", token).ConfigureAwait(false);

            await Expect("TYPE I", token).ConfigureAwait(false);
        }
        catch
        {
            Dispose();

            throw;
        }
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken token)
    {
        // LIST は「いまいる場所」を返すサーバがある（引数を無視する）ので、先に移る
        await Expect($"CWD {path}", token).ConfigureAwait(false);

        // MLSD があるなら使う。機械可読で年まで入っており、書式で悩まなくて済む
        string verb = _hasMachineListing ? "MLSD" : "LIST";

        string text = Encoding.UTF8.GetString(
            await ReadDataAsync(verb, token).ConfigureAwait(false));

        DateTime now = DateTime.Now;
        var entries = new List<RemoteEntry>();

        foreach (string line in text.Split('\n'))
        {
            RemoteEntry? entry = _hasMachineListing
                ? FtpListing.ParseMachineLine(line)
                : FtpListing.ParseListLine(line, now);

            // 読めない行は捨てる。1 行のために一覧全体を失う方が困る
            if (entry is { } row) entries.Add(row);
        }

        return entries;
    }

    public async Task DownloadAsync(
        string remotePath, string localPath,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        long total = await SizeAsync(remotePath, token).ConfigureAwait(false);

        using TcpClient data = await OpenDataAsync($"RETR {remotePath}", token).ConfigureAwait(false);

        await using (var to = new FileStream(
            localPath, FileMode.Create, FileAccess.Write, FileShare.None,
            TransferBuffer.Size, useAsync: true))
        {
            await TransferBuffer.CopyAsync(
                data.GetStream(), to, RemotePath.Name(remotePath), total, progress, token).ConfigureAwait(false);
        }

        data.Close();

        await FinishTransferAsync(token).ConfigureAwait(false);
    }

    public async Task UploadAsync(
        string localPath, string remotePath,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        var info = new FileInfo(localPath);

        using TcpClient data = await OpenDataAsync($"STOR {remotePath}", token).ConfigureAwait(false);

        await using (var from = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            TransferBuffer.Size, useAsync: true))
        {
            await TransferBuffer.CopyAsync(
                from, data.GetStream(), info.Name, info.Length, progress, token).ConfigureAwait(false);
        }

        // 送り終わりは「データ接続を閉じたこと」で伝える。閉じる前に 226 を待つと止まる
        data.Client.Shutdown(SocketShutdown.Send);
        data.Close();

        await FinishTransferAsync(token).ConfigureAwait(false);
    }

    public Task MakeDirectoryAsync(string path, CancellationToken token)
        => Expect($"MKD {path}", token);

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken token)
        => Expect(isDirectory ? $"RMD {path}" : $"DELE {path}", token);

    public async Task RenameAsync(string fromPath, string toPath, CancellationToken token)
    {
        // 2 段階。RNFR が 350 を返してから RNTO
        FtpReply from = await SendAsync($"RNFR {fromPath}", token).ConfigureAwait(false);

        if (!from.NeedsMore && !from.IsSuccess)
            throw Failed($"RNFR {fromPath}", from);

        await Expect($"RNTO {toPath}", token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            // QUIT は投げるだけ。応答を待たない（相手が黙って切ることがある）
            if (_stream is { } stream && _control is { Connected: true })
                stream.Write("QUIT\r\n"u8);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // もう切れている。片付けは続ける
        }

        _reader?.Dispose();
        _stream?.Dispose();
        _control?.Dispose();

        _reader = null;
        _stream = null;
        _control = null;
    }

    // ===== 制御接続 =====

    /// <summary>1 コマンド投げて応答を受ける。</summary>
    private async Task<FtpReply> SendAsync(string command, CancellationToken token)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("接続していません。");

        byte[] bytes = Encoding.UTF8.GetBytes(command + "\r\n");

        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        return await ReadReplyAsync(token).ConfigureAwait(false);
    }

    /// <summary>投げて、成功しなければ例外。</summary>
    private async Task Expect(string command, CancellationToken token)
    {
        FtpReply reply = await SendAsync(command, token).ConfigureAwait(false);

        if (!reply.IsSuccess) throw Failed(command, reply);
    }

    /// <summary>応答が揃うまで行を読む。複数行応答の組み立ては Core に任せる。</summary>
    private async Task<FtpReply> ReadReplyAsync(CancellationToken token)
    {
        StreamReader reader = _reader ?? throw new InvalidOperationException("接続していません。");

        _replies.Reset();

        while (true)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);

            if (line is null) throw new InvalidOperationException("接続が切れました。");

            if (_replies.Feed(line) is { } reply) return reply;
        }
    }

    private static InvalidOperationException Failed(string command, FtpReply reply)
    {
        // 応答文はサーバの言葉そのまま。こちらで訳すより手掛かりになる
        string verb = command.Split(' ')[0];

        return new InvalidOperationException($"{verb} が断られました（{reply.Code}）: {reply.Text.Trim()}");
    }

    // ===== データ接続 =====

    /// <summary>
    /// PASV でデータ接続を開き、転送のコマンドを投げたところまで進める。
    /// <b>閉じるのは呼んだ側。</b>
    /// </summary>
    private async Task<TcpClient> OpenDataAsync(string command, CancellationToken token)
    {
        FtpReply passive = await SendAsync("PASV", token).ConfigureAwait(false);

        if (FtpListing.ParsePassiveReply(passive.Text) is not { } endpoint)
            throw Failed("PASV", passive);

        // 相手が答えたアドレスは使わない（NAT の内側だと自分の私設アドレスを答える）。
        // ポートだけもらって、繋ぐ先は制御接続と同じ相手にする
        IPAddress address = _serverAddress ?? endpoint.Address;

        var data = new TcpClient();

        try
        {
            await data.ConnectAsync(address, endpoint.Port, token).ConfigureAwait(false);

            FtpReply started = await SendAsync(command, token).ConfigureAwait(false);

            // 150/125 で開始、まれに 226 で即終わり（0 バイト）
            if (!started.IsPreliminary && !started.IsSuccess) throw Failed(command, started);
        }
        catch
        {
            data.Dispose();

            throw;
        }

        return data;
    }

    /// <summary>一覧のように短いものを丸ごと受ける。</summary>
    private async Task<byte[]> ReadDataAsync(string command, CancellationToken token)
    {
        using TcpClient data = await OpenDataAsync(command, token).ConfigureAwait(false);

        using var buffer = new MemoryStream();

        await data.GetStream().CopyToAsync(buffer, token).ConfigureAwait(false);

        data.Close();

        await FinishTransferAsync(token).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>転送の締め。150 を受けていれば 226 が続く。</summary>
    private async Task FinishTransferAsync(CancellationToken token)
    {
        FtpReply done = await ReadReplyAsync(token).ConfigureAwait(false);

        if (done.IsFailure) throw Failed("転送", done);
    }

    /// <summary>SIZE。持たないサーバがあるので、取れなければ 0（進捗が出ないだけ）。</summary>
    private async Task<long> SizeAsync(string path, CancellationToken token)
    {
        FtpReply reply = await SendAsync($"SIZE {path}", token).ConfigureAwait(false);

        return reply.IsSuccess
               && long.TryParse(reply.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size)
            ? size
            : 0;
    }
}
