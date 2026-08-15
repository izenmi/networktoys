using System.Net;
using System.Net.Sockets;
using PingWatcher.Core.Snmp;

namespace PingWatcher.App.Services;

/// <summary>
/// SNMP Trap の受信サーバ（v1/v2c）。UDP/162 で待ち受け、届いた Trap を
/// 解析して一覧に出す。<see cref="IFileServer"/> に乗せて共通基盤で扱う。
/// </summary>
internal sealed class SnmpTrapReceiver : IFileServer
{
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;

    public event Action<FileServerEvent>? Event;

    public bool IsRunning => _listener is not null;

    public void Start(int port)
    {
        if (IsRunning) return;

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

            SnmpMessage? message = SnmpCodec.Parse(received.Buffer);
            if (message is null) continue;

            string remote = received.RemoteEndPoint.Address.ToString();
            Event?.Invoke(new FileServerEvent(DateTime.Now, remote, Describe(message)));
        }
    }

    private static string Describe(SnmpMessage trap)
    {
        // v2c Trap は snmpTrapOID、v1 Trap は enterprise OID を TrapOid に入れてある
        string what = trap.TrapOid is { } oid ? oid.DisplayName : "trap";

        // varbind を「名前=値」で数個だけ添える（先頭の sysUpTime/trapOID は除く）
        var parts = new List<string>();
        foreach (VarBind vb in trap.VarBinds.Take(6))
        {
            if (vb.Oid.DisplayName is "sysUpTime" or "snmpTrapOID") continue;
            parts.Add($"{vb.Oid.DisplayName}={vb.Value.Display}");
        }

        string detail = parts.Count > 0 ? "　" + string.Join("  ", parts) : string.Empty;
        return $"[{trap.Community}] {what}{detail}";
    }

    public void Dispose() => Stop();
}
