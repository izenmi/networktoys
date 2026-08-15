using System.Net;
using System.Runtime.InteropServices;
using PingWatcher.Core.Net;

namespace PingWatcher.App.Interop;

/// <summary>
/// Win32 API の P/Invoke 置き場。ARP テーブルと TCP/UDP 接続表の読み取り、
/// アイコン数の確認、タイトルバーの明暗切り替え。
///
/// <c>arp -a</c> や <c>netstat</c> の出力を解析する手もあるが、<b>表示が OS の言語で
/// 変わる</b>ため日本語環境と英語環境で壊れる。iphlpapi を直接叩けばロケールに
/// 左右されない。どの読み取りにも管理者権限は要らない。
/// </summary>
internal static class NativeMethods
{
    private const int AfInet = 2;          // AF_INET
    private const int NoError = 0;

    // LibraryImport は unsafe コードを生成するため AllowUnsafeBlocks が要る。
    // 関数 2 つのために全体で unsafe を許可したくないので DllImport を使う。
    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    // MIB_IPNET_ROW2 の先頭部分だけを写している。
    // 必要なのは IP・MAC・状態の 3 つで、後続フィールドは読まない。
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow2
    {
        public SockAddrInet Address;
        public uint InterfaceIndex;
        public ulong InterfaceLuid;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PhysicalAddress;

        public uint PhysicalAddressLength;
        public uint State;
        public uint Flags;
        public uint ReachabilityTime;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockAddrInet
    {
        [FieldOffset(0)]
        public ushort Family;

        // sockaddr_in の sin_addr は先頭から 4 バイト目
        [FieldOffset(4)]
        public uint Ipv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetTable2Header
    {
        public uint NumEntries;
        public uint Reserved;
    }

    /// <summary>
    /// IPv4 の近隣キャッシュを読む。取得できなければ空を返す（機能を止めない）。
    /// </summary>
    public static Dictionary<string, string> GetArpTable()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        IntPtr table = IntPtr.Zero;

        try
        {
            if (GetIpNetTable2(AfInet, out table) != NoError || table == IntPtr.Zero)
                return result;

            var header = Marshal.PtrToStructure<MibIpNetTable2Header>(table);

            // 配列は 8 バイト境界に揃えられる
            int rowSize = Marshal.SizeOf<MibIpNetRow2>();
            IntPtr cursor = table + 8;

            for (uint i = 0; i < header.NumEntries; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow2>(cursor);
                cursor += rowSize;

                if (row.Address.Family != AfInet) continue;
                if (row.PhysicalAddressLength is 0 or > 32) continue;

                var address = new IPAddress(BitConverter.GetBytes(row.Address.Ipv4));
                string mac = FormatMac(row.PhysicalAddress, (int)row.PhysicalAddressLength);

                if (mac.Length > 0)
                    result[address.ToString()] = mac;
            }
        }
        // AccessViolationException は .NET Core 以降 catch できない（プロセスごと落ちる）ので並べない
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 取れない環境でもスキャン自体は続けられるようにする
        }
        finally
        {
            if (table != IntPtr.Zero)
                FreeMibTable(table);
        }

        return result;
    }

    private static string FormatMac(byte[] bytes, int length)
    {
        if (length <= 0) return string.Empty;

        // 全部 0 のエントリは未解決なので捨てる
        bool allZero = true;
        for (int i = 0; i < length; i++)
        {
            if (bytes[i] != 0) { allZero = false; break; }
        }
        if (allZero) return string.Empty;

        return string.Join('-', bytes.Take(length).Select(b => b.ToString("X2")));
    }

    private const int AfInet6 = 23;               // AF_INET6
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;    // TCP_TABLE_OWNER_PID_ALL
    private const int UdpTableOwnerPid = 1;       // UDP_TABLE_OWNER_PID

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);

    // 全フィールド DWORD。ポートはネットワークオーダーの 16bit が下位に入っている
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    // IPv6 版だけ State が末尾側に来る。フィールド順を v4 版から写さないこと
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;

        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    /// <summary>
    /// TCP/UDP の接続表（v4/v6 の 4 枚）を読む。非管理者でも全プロセスの PID 込みで
    /// 取得できる。取得できなければ空を返す（機能を止めない）。
    /// </summary>
    public static List<ConnectionRow> GetConnectionTable()
    {
        var rows = new List<ConnectionRow>();

        try
        {
            ReadConnectionTable(tcp: true, v6: false, rows);
            ReadConnectionTable(tcp: true, v6: true, rows);
            ReadConnectionTable(tcp: false, v6: false, rows);
            ReadConnectionTable(tcp: false, v6: true, rows);
        }
        // AccessViolationException は .NET Core 以降 catch できない（プロセスごと落ちる）ので並べない
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 取れない環境でも一覧が空になるだけで、アプリは動き続ける
        }

        return rows;
    }

    private static void ReadConnectionTable(bool tcp, bool v6, List<ConnectionRow> sink)
    {
        uint family = (uint)(v6 ? AfInet6 : AfInet);
        int size = 0;

        uint result = tcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpTableOwnerPid, 0);

        // 必要サイズを訊いてから確保するが、呼び出しの合間にテーブルが伸びることが
        // あるので、足りないと言われる間は確保し直す（無限には付き合わない）
        for (int attempt = 0; result == ErrorInsufficientBuffer && attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = tcp
                    ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
                    : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);

                if (result == NoError)
                {
                    ParseConnectionTable(buffer, tcp, v6, sink);
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static void ParseConnectionTable(IntPtr table, bool tcp, bool v6, List<ConnectionRow> sink)
    {
        int count = Marshal.ReadInt32(table);

        // 先頭は DWORD dwNumEntries。この 4 種の行はアラインメント 4 なので配列は +4 から
        // 始まる（GetIpNetTable2 は行に 8 バイト境界のフィールドがあるため +8。写経しないこと）
        IntPtr cursor = table + 4;

        for (int i = 0; i < count; i++)
        {
            if (tcp && !v6)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                cursor += Marshal.SizeOf<MibTcpRowOwnerPid>();
                sink.Add(new ConnectionRow(
                    ConnectionProtocol.TcpV4,
                    FormatIpv4(row.LocalAddr),
                    ConnectionTableView.PortFromNetworkOrder(row.LocalPort),
                    FormatIpv4(row.RemoteAddr),
                    ConnectionTableView.PortFromNetworkOrder(row.RemotePort),
                    (TcpConnectionState)row.State,
                    (int)row.OwningPid));
            }
            else if (tcp)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(cursor);
                cursor += Marshal.SizeOf<MibTcp6RowOwnerPid>();
                sink.Add(new ConnectionRow(
                    ConnectionProtocol.TcpV6,
                    FormatIpv6(row.LocalAddr, row.LocalScopeId),
                    ConnectionTableView.PortFromNetworkOrder(row.LocalPort),
                    FormatIpv6(row.RemoteAddr, row.RemoteScopeId),
                    ConnectionTableView.PortFromNetworkOrder(row.RemotePort),
                    (TcpConnectionState)row.State,
                    (int)row.OwningPid));
            }
            else if (!v6)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(cursor);
                cursor += Marshal.SizeOf<MibUdpRowOwnerPid>();
                sink.Add(new ConnectionRow(
                    ConnectionProtocol.UdpV4,
                    FormatIpv4(row.LocalAddr),
                    ConnectionTableView.PortFromNetworkOrder(row.LocalPort),
                    "0.0.0.0", 0, TcpConnectionState.None,
                    (int)row.OwningPid));
            }
            else
            {
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(cursor);
                cursor += Marshal.SizeOf<MibUdp6RowOwnerPid>();
                sink.Add(new ConnectionRow(
                    ConnectionProtocol.UdpV6,
                    FormatIpv6(row.LocalAddr, row.LocalScopeId),
                    ConnectionTableView.PortFromNetworkOrder(row.LocalPort),
                    "::", 0, TcpConnectionState.None,
                    (int)row.OwningPid));
            }
        }
    }

    private static string FormatIpv4(uint networkOrder)
        => new IPAddress(BitConverter.GetBytes(networkOrder)).ToString();

    private static string FormatIpv6(byte[] address, uint scopeId)
        => (scopeId == 0 ? new IPAddress(address) : new IPAddress(address, scopeId)).ToString();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    /// <summary>
    /// 実行ファイルに入っているアイコンの数。
    ///
    /// <c>ApplicationIcon</c> の指定漏れやパスの打ち間違いはビルドを通ってしまうので、
    /// 実際に埋め込まれたかを実行ファイル側から確かめるために使う。
    /// index に -1 を渡すと、取り出さずに個数だけ返る。
    /// </summary>
    public static int CountIcons(string executablePath)
    {
        try
        {
            return ExtractIconExW(executablePath, -1, null, null, 0);
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    // タイトルバーを暗くする属性。Windows 10 1809〜1903 では 19、それ以降は 20。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// タイトルバーの明暗を窓の中身に合わせる。
    ///
    /// これをやらないと、暗い画面の上に白いタイトルバーが残って台無しになる。
    /// 自前のタイトルバー（WindowChrome）を組むより遥かに安く、挙動も OS 標準のまま。
    /// 古い Windows には属性が無いだけなので、失敗は無視してよい。
    /// </summary>
    public static void SetTitleBarDark(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;

        int value = dark ? 1 : 0;

        try
        {
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // dwmapi が無い環境。見た目が揃わないだけで動作に支障はない
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
