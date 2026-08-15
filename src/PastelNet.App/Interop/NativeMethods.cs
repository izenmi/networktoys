using System.Net;
using System.Runtime.InteropServices;

namespace PastelNet.App.Interop;

/// <summary>
/// ARP テーブル（近隣キャッシュ）の読み取り。
///
/// <c>arp -a</c> の出力を解析する手もあるが、<b>表示が OS の言語で変わる</b>ため
/// 日本語環境と英語環境で壊れる。iphlpapi を直接叩けばロケールに左右されない。
/// 読み取りに管理者権限は要らない。
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
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
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
