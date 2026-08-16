using System.Net;
using System.Net.Sockets;
using System.Text;
using PingWatcher.Core.Logging;

namespace PingWatcher.App.Services;

/// <summary>
/// syslog サーバ。UDP/514 で機器からの syslog を受けて一覧に出す。
/// 切替作業のとき、機器に <c>logging &lt;このPCのIP&gt;</c> を一行入れるだけで、
/// リンクダウンや OSPF 断が ping の不通記録と同じ時系列で手元に残る。
///
/// <see cref="IFileServer"/> に乗せて、開始/停止・履歴・ログを共通基盤で扱う。
/// </summary>
internal sealed class SyslogReceiver : IFileServer
{
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;

    public event Action<FileServerEvent>? Event;

    public bool IsRunning => _listener is not null;

    public void Start(int port)
    {
        if (IsRunning) return;

        // SocketException（ポート使用中など）はそのまま呼び出し側へ
        var listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));

        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // syslog は 1 データグラム = 1 メッセージ。改行区切りで複数来ることもある
            string payload = Encoding.UTF8.GetString(received.Buffer);
            string remote = received.RemoteEndPoint.Address.ToString();

            foreach (string line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                SyslogMessage message = SyslogParser.Parse(line);

                // 重大度は文字列に混ぜず、そのまま数値で渡す。
                // PRI が無い機器は -1 にして「重大度なし」と区別する
                Event?.Invoke(new FileServerEvent(
                    DateTime.Now, remote, message.Message,
                    message.HasPriority ? message.Severity : -1));
            }
        }
    }

    public void Dispose() => Stop();
}
