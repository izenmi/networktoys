namespace PingWatcher.App.Services;

/// <param name="At">発生時刻。</param>
/// <param name="RemoteAddress">相手の IP。</param>
/// <param name="Text">出来事（転送の結果や接続など）。</param>
internal sealed record FileServerEvent(DateTime At, string RemoteAddress, string Text);

/// <summary>
/// ファイル配布サーバ（FTP / TFTP / SFTP）の共通口。
/// UI 側（<see cref="ViewModels.FileServerViewModel"/>）はこの契約だけを見る。
/// </summary>
internal interface IFileServer : IDisposable
{
    /// <summary>出来事の通知。サーバ側のスレッドから上がるので購読側で marshaling する。</summary>
    event Action<FileServerEvent>? Event;

    bool IsRunning { get; }

    /// <summary>待受を始める。ポートが使えなければ SocketException を投げる。</summary>
    void Start(int port);

    void Stop();
}
