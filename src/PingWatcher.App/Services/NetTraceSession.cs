using System.Runtime.InteropServices;
using System.Security.Principal;
using PingWatcher.App.Interop;
using PingWatcher.Core.Net;

namespace PingWatcher.App.Services;

/// <summary>
/// Microsoft-Windows-Kernel-Network の ETW リアルタイムセッション。
/// TCP/UDP の送受信イベントを <see cref="TrafficAggregator"/> へ積む。
///
/// カーネルの通信イベントを購読できるのは<b>管理者だけ</b>。非管理者では
/// <see cref="Start"/> が false を返し、接続一覧は通信量なしで動き続ける
/// （このアプリで唯一の管理者限定機能）。カーネルセッションはシステム全体の
/// コストなので、タブが見えている間だけ回す。
/// </summary>
internal sealed class NetTraceSession : IDisposable
{
    private const string SessionName = "PingWatcher-KernelNet";

    private static readonly Guid ProviderId = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly long ProviderIdLow;
    private static readonly long ProviderIdHigh;

    static NetTraceSession()
    {
        byte[] guidBytes = ProviderId.ToByteArray();
        ProviderIdLow = BitConverter.ToInt64(guidBytes, 0);
        ProviderIdHigh = BitConverter.ToInt64(guidBytes, 8);
        IsAdministrator = DetectAdministrator();
    }

    /// <summary>管理者として起動されているか。プロセスの生存中に変わらないので 1 回だけ調べる。</summary>
    public static bool IsAdministrator { get; }

    private readonly TrafficAggregator _sink;

    // ネイティブへ渡すデリゲート。フィールドで持たないと GC に回収されて
    // コールバックの瞬間に落ちる（古典的な罠）
    private readonly EtwNativeMethods.EventRecordCallback _callback;

    // ProcessTrace のコールバックは単一スレッドなので、アドレス読み出し用のバッファを使い回せる
    private readonly byte[] _sourceAddress = new byte[16];
    private readonly byte[] _destinationAddress = new byte[16];

    private ulong _sessionHandle;
    private ulong _traceHandle = EtwNativeMethods.InvalidTraceHandle;
    private Thread? _pumpThread;

    public NetTraceSession(TrafficAggregator sink)
    {
        _sink = sink;
        _callback = OnEventRecord;
    }

    public bool IsRunning { get; private set; }

    /// <summary>開始できなかった理由。UI の案内行にそのまま出す。</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>投げない。失敗したら false と <see cref="FailureMessage"/>。</summary>
    public bool Start()
    {
        if (IsRunning)
            return true;

        FailureMessage = null;

        try
        {
            // 前回クラッシュの残骸セッションを空振り覚悟で止めてから始める。
            // これがあるので再開始は常に安全
            StopSessionByName();

            uint startResult;
            IntPtr properties = EtwNativeMethods.AllocateProperties();
            try
            {
                startResult = EtwNativeMethods.StartTraceW(out _sessionHandle, SessionName, properties);
                if (startResult == EtwNativeMethods.ErrorAlreadyExists)
                {
                    StopSessionByName();
                    startResult = EtwNativeMethods.StartTraceW(out _sessionHandle, SessionName, properties);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(properties);
            }

            if (startResult != 0)
            {
                return Fail(startResult == EtwNativeMethods.ErrorAccessDenied
                    ? "通信量の取得を開始できませんでした（権限不足）。"
                    : $"ETW セッションを開始できませんでした（コード {startResult}）。");
            }

            Guid provider = ProviderId;
            uint enableResult = EtwNativeMethods.EnableTraceEx2(
                _sessionHandle, ref provider,
                EtwNativeMethods.EventControlCodeEnableProvider,
                EtwNativeMethods.TraceLevelInformation,
                ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (enableResult != 0)
            {
                StopSessionByName();
                return Fail($"通信イベントの購読を開始できませんでした（コード {enableResult}）。");
            }

            var logfile = new EtwNativeMethods.EventTraceLogfile
            {
                ProcessTraceMode = EtwNativeMethods.ProcessTraceModeRealTime | EtwNativeMethods.ProcessTraceModeEventRecord,
                EventRecordCallback = _callback,
                CurrentEvent = new byte[64],
                LogfileHeader = new byte[280],
            };

            IntPtr namePtr = Marshal.StringToHGlobalUni(SessionName);
            try
            {
                logfile.LoggerName = namePtr;
                _traceHandle = EtwNativeMethods.OpenTraceW(ref logfile);
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }

            if (_traceHandle == EtwNativeMethods.InvalidTraceHandle)
            {
                StopSessionByName();
                return Fail("ETW セッションに接続できませんでした。");
            }

            // ProcessTrace はセッションが止まるまで返らないので専用スレッドで回す
            ulong handle = _traceHandle;
            _pumpThread = new Thread(() => Pump(handle)) { IsBackground = true, Name = "PingWatcher-EtwPump" };
            _pumpThread.Start();

            IsRunning = true;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Fail("この環境では ETW を利用できません。");
        }
    }

    public void Stop()
    {
        try
        {
            if (_traceHandle != EtwNativeMethods.InvalidTraceHandle)
            {
                EtwNativeMethods.CloseTrace(_traceHandle);   // ProcessTrace が戻り始める
                _traceHandle = EtwNativeMethods.InvalidTraceHandle;
            }

            if (IsRunning || _sessionHandle != 0)
            {
                StopSessionByName();
                _sessionHandle = 0;
            }

            // バッファのフラッシュで 1 秒弱かかることがある。
            // 背景スレッドなので待ちきれなければ見捨ててよい
            _pumpThread?.Join(TimeSpan.FromSeconds(1));
            _pumpThread = null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Start が同じ理由で失敗しているはずなので、後始末も諦めてよい
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Dispose() => Stop();

    private static void StopSessionByName()
    {
        IntPtr properties = EtwNativeMethods.AllocateProperties();
        try
        {
            // 無ければ ERROR_WMI_INSTANCE_NOT_FOUND が返るだけ。戻り値は見ない
            EtwNativeMethods.ControlTraceW(0, SessionName, properties, EtwNativeMethods.EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    private static void Pump(ulong traceHandle)
    {
        try
        {
            EtwNativeMethods.ProcessTrace([traceHandle], 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "NetTraceSession.Pump");
        }
    }

    /// <summary>
    /// EVENT_RECORD ごとに呼ばれる。毎秒数千回になりうるので、ここでは
    /// 固定オフセットの読み出しと集計への加算だけを行い、何も確保しない。
    ///
    /// ペイロード（v4）: uint PID; uint size; daddr; saddr; ushort dport; ushort sport;
    /// v6 はアドレスが 16 バイト × 2。アドレスとポートはネットワークバイトオーダー。
    /// saddr/daddr のどちらがローカルかは方向で揺れるため意味を持たせず、
    /// 照合側（ConnectionRates）が両向きを引く。
    /// </summary>
    private void OnEventRecord(IntPtr record)
    {
        try
        {
            // EVENT_DESCRIPTOR.Id は EVENT_HEADER 先頭から 40 バイト目
            int eventId = Marshal.ReadInt16(record, 40) & 0xFFFF;

            bool tcp;
            bool v6;
            bool sent;
            switch (eventId)
            {
                case 10: tcp = true; v6 = false; sent = true; break;    // TCPv4 送信
                case 11: tcp = true; v6 = false; sent = false; break;   // TCPv4 受信
                case 26: tcp = true; v6 = true; sent = true; break;     // TCPv6 送信
                case 27: tcp = true; v6 = true; sent = false; break;    // TCPv6 受信
                case 42: tcp = false; v6 = false; sent = true; break;   // UDPv4 送信
                case 43: tcp = false; v6 = false; sent = false; break;  // UDPv4 受信
                case 58: tcp = false; v6 = true; sent = true; break;    // UDPv6 送信
                case 59: tcp = false; v6 = true; sent = false; break;   // UDPv6 受信
                default: return;
            }

            // ProviderId は EVENT_HEADER 先頭から 24 バイト目。セッションには
            // このプロバイダしか繋いでいないが、ランダウンイベント等と ID が
            // 衝突しても拾わないよう念のため確かめる
            if (Marshal.ReadInt64(record, 24) != ProviderIdLow ||
                Marshal.ReadInt64(record, 32) != ProviderIdHigh)
                return;

            int userDataLength = Marshal.ReadInt16(record, 86) & 0xFFFF;
            IntPtr userData = Marshal.ReadIntPtr(record, 96);
            if (userData == IntPtr.Zero)
                return;

            int addressLength = v6 ? 16 : 4;
            int required = 8 + addressLength * 2 + 4;
            if (userDataLength < required)
                return;

            uint pid = (uint)Marshal.ReadInt32(userData, 0);
            uint bytes = (uint)Marshal.ReadInt32(userData, 4);

            int destinationOffset = 8;
            int sourceOffset = 8 + addressLength;
            int destinationPortOffset = 8 + addressLength * 2;
            int sourcePortOffset = destinationPortOffset + 2;

            Marshal.Copy(userData + destinationOffset, _destinationAddress, 0, addressLength);
            Marshal.Copy(userData + sourceOffset, _sourceAddress, 0, addressLength);
            ushort destinationPort = ReadNetworkOrderPort(userData, destinationPortOffset);
            ushort sourcePort = ReadNetworkOrderPort(userData, sourcePortOffset);

            if (tcp)
            {
                _sink.Add(FlowKey.ForTcp(v6, pid,
                    _sourceAddress.AsSpan(0, addressLength), sourcePort,
                    _destinationAddress.AsSpan(0, addressLength), destinationPort), sent, bytes);
            }
            else
            {
                // UDP は接続表に相手側が無いので、両端点の畳みキーに同じ量を積む。
                // 行側はローカルの端点だけで引くため二重計上にはならない
                _sink.Add(FlowKey.ForUdp(v6, pid, _sourceAddress.AsSpan(0, addressLength), sourcePort), sent, bytes);
                _sink.Add(FlowKey.ForUdp(v6, pid, _destinationAddress.AsSpan(0, addressLength), destinationPort), sent, bytes);
            }
        }
        catch (Exception ex)
        {
            // ネイティブ境界を例外で突き抜けさせない。1 イベント落とすだけにする
            CrashLog.Write(ex, "NetTraceSession.OnEventRecord");
        }
    }

    private static ushort ReadNetworkOrderPort(IntPtr userData, int offset)
        => (ushort)((Marshal.ReadByte(userData, offset) << 8) | Marshal.ReadByte(userData, offset + 1));

    private bool Fail(string message)
    {
        FailureMessage = message;
        return false;
    }

    private static bool DetectAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
