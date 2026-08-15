namespace PingWatcher.Core.Net;

/// <summary>
/// 接続の種別。表示のグループ内はこの並び（TCP → TCPv6 → UDP → UDPv6）で揃える。
/// </summary>
public enum ConnectionProtocol
{
    TcpV4,
    TcpV6,
    UdpV4,
    UdpV6,
}

/// <summary>
/// TCP 接続の状態。値は iphlpapi の MIB_TCP_STATE と一致させてあり、
/// P/Invoke 側は数値をそのままキャストする。UDP は状態を持たないので None。
/// </summary>
public enum TcpConnectionState
{
    None = 0,
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
}

/// <summary>
/// 状態表示の色分け。色そのものは App 側がパレットのキーに対応付ける
/// （Ok → Brush.Ok.Fg / Info → Brush.Info.Fg / Muted → Brush.TextMuted）。
/// </summary>
public enum ConnectionStateKind
{
    Muted,
    Ok,
    Info,
}

/// <summary>
/// 接続表の 1 行。アドレスは表示文字列に正規化済み
/// （IPv6 のスコープ付きは "fe80::1%12" の形）。UDP はリモート側を持たない。
/// </summary>
public sealed record ConnectionRow(
    ConnectionProtocol Protocol,
    string LocalAddress,
    ushort LocalPort,
    string RemoteAddress,
    ushort RemotePort,
    TcpConnectionState State,
    int Pid);

/// <summary>
/// TCP 状態の表示文字列。状態は色だけで表さない決まりなので、
/// 定常状態には記号を併記する。過渡状態は異常ではないため ✕ は使わない。
/// </summary>
public static class TcpStateText
{
    public static (string Text, ConnectionStateKind Kind) Describe(TcpConnectionState state) => state switch
    {
        TcpConnectionState.None => ("—", ConnectionStateKind.Muted),
        TcpConnectionState.Established => ("● ESTABLISHED", ConnectionStateKind.Ok),
        TcpConnectionState.Listen => ("⊘ LISTEN", ConnectionStateKind.Info),
        TcpConnectionState.Closed => ("CLOSED", ConnectionStateKind.Muted),
        TcpConnectionState.SynSent => ("SYN_SENT", ConnectionStateKind.Muted),
        TcpConnectionState.SynReceived => ("SYN_RCVD", ConnectionStateKind.Muted),
        TcpConnectionState.FinWait1 => ("FIN_WAIT_1", ConnectionStateKind.Muted),
        TcpConnectionState.FinWait2 => ("FIN_WAIT_2", ConnectionStateKind.Muted),
        TcpConnectionState.CloseWait => ("CLOSE_WAIT", ConnectionStateKind.Muted),
        TcpConnectionState.Closing => ("CLOSING", ConnectionStateKind.Muted),
        TcpConnectionState.LastAck => ("LAST_ACK", ConnectionStateKind.Muted),
        TcpConnectionState.TimeWait => ("TIME_WAIT", ConnectionStateKind.Muted),
        TcpConnectionState.DeleteTcb => ("DELETE_TCB", ConnectionStateKind.Muted),
        _ => (((int)state).ToString(), ConnectionStateKind.Muted),
    };
}
