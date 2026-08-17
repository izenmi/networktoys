using System.Runtime.InteropServices;

namespace NetworkToys.App.Interop;

/// <summary>
/// ETW（Event Tracing for Windows）のリアルタイム購読に使う P/Invoke 一式。
/// 必要なのは 1 プロバイダ・8 イベント ID・固定レイアウトのペイロードだけなので、
/// TraceEvent のような汎用パーサは持ち込まない（依存は少ないほどよい。
/// ローカルでビルドを確認できない以上、NuGet のバージョン依存の方がリスクが大きい）。
///
/// 構造体レイアウトを誤ると AccessViolation でプロセスごと落ちる（catch 不能）。
/// 防波堤は自己診断の ETW 検査で、CI の Windows ランナーは管理者なので実経路が通る。
/// </summary>
internal static class EtwNativeMethods
{
    public const uint EventTraceControlStop = 1;          // EVENT_TRACE_CONTROL_STOP
    public const uint EventControlCodeEnableProvider = 1; // EVENT_CONTROL_CODE_ENABLE_PROVIDER
    public const byte TraceLevelInformation = 4;          // TRACE_LEVEL_INFORMATION
    public const uint EventTraceRealTimeMode = 0x00000100;
    public const uint ProcessTraceModeRealTime = 0x00000100;
    public const uint ProcessTraceModeEventRecord = 0x10000000;
    public const uint WnodeFlagTracedGuid = 0x00020000;
    public const ulong InvalidTraceHandle = ulong.MaxValue;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorAlreadyExists = 183;

    /// <summary>セッション名 2 本ぶんの余白。StartTrace/ControlTrace が名前を書き込む。</summary>
    private const int NameBufferBytes = 1024;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    public static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode,
        byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll")]
    public static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    public static extern uint CloseTrace(ulong traceHandle);

    // WNODE_HEADER（48 バイト）。union は使う側のメンバだけで表す
    [StructLayout(LayoutKind.Sequential)]
    public struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;   // 1 = QPC
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    /// <summary>
    /// EVENT_TRACE_LOGFILEW（x64 で 448 バイト）。読まないフィールドの
    /// EVENT_TRACE(88B) と TRACE_LOGFILE_HEADER(280B) は不透明な塊として写す。
    /// EVENT_TRACE は Vista で InstanceId / ParentInstanceId / ParentGuid が
    /// 増えて 88 バイトになっている（48+4+4+16+8+4+4）。ここを 64 と誤ると
    /// 後続フィールドが 24 バイトずれ、OpenTrace がバッファ外へ書いて
    /// ヒープ破壊（0xC0000409）で落ちる — CI で実際に踏んだ。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)]
        public byte[] CurrentEvent;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)]
        public byte[] LogfileHeader;

        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public EventRecordCallback? EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    /// <summary>
    /// StartTrace / ControlTrace 用の EVENT_TRACE_PROPERTIES をネイティブ側に組み立てる。
    /// 呼び出し側が必ず <see cref="Marshal.FreeHGlobal"/> すること。
    /// </summary>
    public static IntPtr AllocateProperties()
    {
        int structSize = Marshal.SizeOf<EventTraceProperties>();
        int totalSize = structSize + NameBufferBytes * 2;

        IntPtr buffer = Marshal.AllocHGlobal(totalSize);
        Marshal.Copy(new byte[totalSize], 0, buffer, totalSize);   // 名前領域までゼロ埋め

        var properties = new EventTraceProperties
        {
            Wnode = new WnodeHeader
            {
                BufferSize = (uint)totalSize,
                ClientContext = 1,
                Flags = WnodeFlagTracedGuid,
            },
            BufferSize = 64,                      // KB
            LogFileMode = EventTraceRealTimeMode,
            FlushTimer = 1,                       // 秒。表示の追随を優先する
            LoggerNameOffset = (uint)structSize,
            LogFileNameOffset = (uint)(structSize + NameBufferBytes),
        };
        Marshal.StructureToPtr(properties, buffer, false);

        return buffer;
    }
}
