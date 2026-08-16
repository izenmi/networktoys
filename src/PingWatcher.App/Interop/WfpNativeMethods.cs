using System.Net;
using System.Runtime.InteropServices;
using PingWatcher.Core.Net;

namespace PingWatcher.App.Interop;

/// <summary>読み取りの結果。エラーはそのまま画面に出せる日本語。</summary>
/// <param name="Events">遮断イベント。</param>
/// <param name="TotalSeen">列挙できたイベントの総数（種類を問わない）。</param>
/// <param name="CollectOption">FWPM_ENGINE_COLLECT_NET_EVENTS の値。0 なら記録していない。</param>
/// <param name="Error">失敗した理由。成功なら null。</param>
/// <param name="WithAppId">
/// アプリ情報（appId）が付いていた遮断イベントの数。
/// <b>切り分けのための数字。</b>プロセス欄が全部「—」になるとき、
/// Windows が付けていないのか、こちらの読み違いなのかはこれでしか分からない。
/// </param>
/// <param name="FlagsSeen">
/// 見たフラグの論理和。ここに 0x20（APP_ID_SET）が一度も立たなければ、
/// Windows がアプリ情報を付けていない。立つのに <paramref name="WithAppId"/> が
/// 0 なら、こちらの読み方が悪い。
/// </param>
/// <param name="SkippedByFlags">未知のフラグが立っていて捨てた数（レイアウト不一致の疑い）。</param>
internal sealed record WfpReadResult(
    IReadOnlyList<WfpBlockedEvent> Events,
    int TotalSeen,
    uint CollectOption,
    string? Error,
    int WithAppId = 0,
    uint FlagsSeen = 0,
    int SkippedByFlags = 0);

/// <summary>
/// Windows Filtering Platform（fwpuclnt.dll）から「遮断されたイベント」を読む P/Invoke 一式。
///
/// <b>構造体を 1 つも宣言しない。</b>FwpmNetEventEnum2 が返すのは FWPM_NET_EVENT2***
/// ＝<b>ポインタの配列</b>なので、配列のストライドは構造体サイズではなく常に 8。
/// Marshal.SizeOf を必要とする箇所が設計上どこにも無く、ETW で踏んだ
/// 「構造体サイズを 24 バイト誤って隣を踏む」事故が起こりようがない。
/// 必要な 10 フィールドだけを Marshal.Read* でオフセット直読みする
/// （PtrToStructure は構造体全体を読むので、末尾フィールドの誤りだけで
/// バッファ外へ出てしまう）。
///
/// 危険なのは残る 3 回の逆参照（union ポインタ・appId.data・イベントポインタ）だけで、
/// それぞれの手前に値域検査の門を置いてある。とくに <c>type</c>（オフセット 104）の
/// 検査は、ヘッダ長を誤ったときに catch 不能の AccessViolation になる唯一の経路を
/// きれいなエラー文字列へ変換する要。<b>union を触る前に必ず通すこと。</b>
/// </summary>
internal static class WfpNativeMethods
{
    // ===== オフセット表（x64。検算は下のコメントのとおり） =====
    //
    // FWPM_NET_EVENT_HEADER2（104 バイト）
    //    0  FILETIME timeStamp        8 バイト（align 4。8 境界を要求しないのが罠）
    //    8  UINT32   flags
    //   12  FWP_IP_VERSION ipVersion  C の enum は 4 バイト
    //   16  UINT8    ipProtocol       …17〜19 はパディング
    //   20  union    localAddr        FWP_BYTE_ARRAY16(align 1) と UINT32(align 4) の
    //                                 union なので align 4・サイズ 16。「16 バイト配列だから
    //                                 8 か 16 境界」と早合点すると 4 バイトずれる
    //   36  union    remoteAddr
    //   52  UINT16   localPort
    //   54  UINT16   remotePort
    //   56  UINT32   scopeId          …60〜63 はパディング（次が align 8）
    //   64  FWP_BYTE_BLOB appId       UINT32 size + 4 パディング + UINT8* data ＝ 16 バイト
    //   80  SID*     userId           読まない（逆参照を 1 つ減らす）
    //   88  FWP_AF   addressFamily    …92〜95 はパディング
    //   96  SID*     packageSid       → 終端 104
    //
    // FWPM_NET_EVENT2（120 バイト）
    //  104  FWPM_NET_EVENT_TYPE type  …108〜111 はパディング（union が全部ポインタ＝align 8）
    //  112  union                     10 メンバすべてポインタなので 8 バイトに畳める
    //
    // FWPM_NET_EVENT_CLASSIFY_DROP2（56 バイト）
    //    0  UINT64 filterId / 8 UINT16 layerId /（10〜11 パディング）/ 12 reauthReason
    //   16  originalProfile / 20 currentProfile / 24 msFwpDirection / 28 BOOL isLoopback
    //   32  FWP_BYTE_BLOB vSwitchId / 48・52 vSwitch ポート → 終端 56
    private const int OffTimeStamp = 0;
    private const int OffFlags = 8;
    private const int OffIpVersion = 12;
    private const int OffIpProtocol = 16;
    private const int OffLocalAddr = 20;
    private const int OffRemoteAddr = 36;
    private const int OffLocalPort = 52;
    private const int OffRemotePort = 54;
    private const int OffScopeId = 56;
    private const int OffAppIdSize = 64;
    private const int OffAppIdData = 72;
    private const int OffType = 104;
    private const int OffUnion = 112;

    private const int DropOffFilterId = 0;
    private const int DropOffLayerId = 8;
    private const int DropOffDirection = 24;
    private const int DropOffIsLoopback = 28;

    // FWPM_NET_EVENT_FLAG_*。立っているビットのフィールドだけが有効
    private const uint FlagIpProtocolSet = 0x0001;
    private const uint FlagLocalAddrSet = 0x0002;
    private const uint FlagRemoteAddrSet = 0x0004;
    private const uint FlagLocalPortSet = 0x0008;
    private const uint FlagRemotePortSet = 0x0010;
    private const uint FlagAppIdSet = 0x0020;
    private const uint FlagScopeIdSet = 0x0080;
    private const uint FlagIpVersionSet = 0x0100;
    private const uint KnownFlags = 0x3FFF;

    private const int TypeClassifyDrop = 3;      // FWPM_NET_EVENT_TYPE_CLASSIFY_DROP
    private const int MaxKnownType = 15;         // 値域の門。実際は 10 前後だが余裕を見る

    private const uint RpcCAuthnWinNt = 10;      // RPC_C_AUTHN_WINNT
    private const uint ErrorAccessDenied = 5;

    /// <summary>FWPM_ENGINE_COLLECT_NET_EVENTS。0 なら Windows が記録していない。</summary>
    public const uint OptionCollectNetEvents = 0;

    private const int FwpUint32 = 3;             // FWP_DATA_TYPE.FWP_UINT32
    private const int ValueTypeOffset = 0;       // FWP_VALUE0（16 バイト）: type@0
    private const int ValueUint32Offset = 8;     //                          値@8

    /// <summary>1 回の列挙で取り出す最大件数。</summary>
    private const int BatchSize = 1000;

    // WFP の API は Unicode 専用で A/W の対が無く、W サフィックスの実名も存在しない
    // （既存の advapi32 まわりと書き方が違う理由）。唯一文字列を取る
    // FwpmEngineOpen0 の serverName は常に NULL なので IntPtr で宣言してあり、
    // このファイルには文字列マーシャリングが 1 つも無い。
    // LibraryImport は unsafe コードを生成するため使わない（AllowUnsafeBlocks は無効）。

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineOpen0(
        IntPtr serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineGetOption0(IntPtr engineHandle, uint option, out IntPtr value);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineSetOption0(IntPtr engineHandle, uint option, IntPtr newValue);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmNetEventCreateEnumHandle0(
        IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmNetEventEnum2(
        IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested,
        out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmNetEventDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr p);

    /// <summary>
    /// 遮断イベントを読む。エンジンは呼ばれるたびに開いて閉じる。
    ///
    /// 生のポインタをこのメソッドの外へ出さない。遅延列挙（yield return）にすると
    /// 呼び出し側が列挙するころにはメモリが解放済みになるので、必ず配列に写し切る。
    /// </summary>
    public static WfpReadResult Read(int maxEvents)
    {
        IntPtr engine = IntPtr.Zero;
        IntPtr enumHandle = IntPtr.Zero;
        var events = new List<WfpBlockedEvent>();
        int totalSeen = 0;
        uint collectOption = 0;

        // 切り分け用。プロセス欄が全部「—」になる原因が
        // 「Windows が付けていない」のか「こちらの読み違い」なのかを分ける
        int withAppId = 0;
        uint flagsSeen = 0;
        int skippedByFlags = 0;

        // オフセットは x64 前提。32 ビットで動かすと全部ずれるので手前で止める
        if (IntPtr.Size != 8)
            return Failure("64 ビット版でのみ利用できます。");

        try
        {
            uint status = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out engine);
            if (status != 0)
                return Failure(DescribeStatus(status));

            collectOption = GetOption(engine, OptionCollectNetEvents);

            status = FwpmNetEventCreateEnumHandle0(engine, IntPtr.Zero, out enumHandle);
            if (status != 0)
                return Failure(DescribeStatus(status));

            while (events.Count < maxEvents)
            {
                uint request = (uint)Math.Min(BatchSize, maxEvents - events.Count);

                status = FwpmNetEventEnum2(engine, enumHandle, request, out IntPtr entries, out uint returned);
                if (status != 0)
                    return Failure(DescribeStatus(status));

                if (entries == IntPtr.Zero || returned == 0)
                    break;

                try
                {
                    if (returned > request)
                        return Failure("応答の件数がおかしいため中止しました。");

                    for (int i = 0; i < returned; i++)
                    {
                        IntPtr item = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        if (!LooksLikePointer(item)) continue;

                        totalSeen++;

                        // ここだけが「レイアウト誤り＝プロセス即死」の分かれ目。
                        // type が値域外なら、これ以上どこも触らずに畳む
                        int type = Marshal.ReadInt32(item, OffType);
                        if (type < 0 || type > MaxKnownType)
                            return Failure("この環境のイベント形式に対応していません（レイアウト不一致）。");

                        if (type != TypeClassifyDrop) continue;

                        // フラグは値域の門を通す前に見ておく。捨てた分も数えたい
                        uint itemFlags = (uint)Marshal.ReadInt32(item, OffFlags);
                        flagsSeen |= itemFlags;

                        if ((itemFlags & ~KnownFlags) != 0) skippedByFlags++;
                        if ((itemFlags & FlagAppIdSet) != 0) withAppId++;

                        WfpBlockedEvent? parsed = ParseDropEvent(item);
                        if (parsed is not null)
                            events.Add(parsed);
                    }
                }
                finally
                {
                    // 配列とイベント本体・DROP2・appId の実体は 1 つの割り当て。
                    // 個々のイベントポインタに対して呼ぶと二重解放になる
                    FwpmFreeMemory0(ref entries);
                }

                if (returned < request) break;
            }

            return new WfpReadResult(events, totalSeen, collectOption, null,
                                     withAppId, flagsSeen, skippedByFlags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Failure("この Windows では遮断イベントを取得できません。");
        }
        finally
        {
            // 列挙ハンドルの破棄はエンジンハンドルを要求する。閉じる順を逆にしない
            if (enumHandle != IntPtr.Zero && engine != IntPtr.Zero)
                FwpmNetEventDestroyEnumHandle0(engine, enumHandle);

            if (engine != IntPtr.Zero)
                FwpmEngineClose0(engine);
        }

        WfpReadResult Failure(string message)
            => new([], totalSeen, collectOption, message, withAppId, flagsSeen, skippedByFlags);
    }

    /// <summary>
    /// 1 件を行に写す。読めない項目は空にして行ごと落とさない
    /// （1 項目の欠落で遮断が一覧から消えると、調べている当人がいちばん困る）。
    /// selftest から合成バッファを食わせるので internal のまま公開しておく。
    /// </summary>
    internal static WfpBlockedEvent? ParseDropEvent(IntPtr item)
    {
        uint flags = (uint)Marshal.ReadInt32(item, OffFlags);

        // オフセット 8 の早期地雷探知。未知のビットだらけならレイアウトが合っていない
        if ((flags & ~KnownFlags) != 0) return null;

        long fileTime = Marshal.ReadInt64(item, OffTimeStamp);
        if (!TryReadTime(fileTime, out DateTime timeUtc)) return null;

        int ipVersion = Marshal.ReadInt32(item, OffIpVersion);
        if ((flags & FlagIpVersionSet) != 0 && ipVersion is not (0 or 1)) return null;

        IntPtr drop = Marshal.ReadIntPtr(item, OffUnion);
        if (!LooksLikePointer(drop)) return null;

        byte protocol = (flags & FlagIpProtocolSet) != 0
            ? Marshal.ReadByte(item, OffIpProtocol)
            : (byte)0;

        uint scopeId = (flags & FlagScopeIdSet) != 0 ? (uint)Marshal.ReadInt32(item, OffScopeId) : 0;

        IPAddress? local = (flags & FlagLocalAddrSet) != 0 ? ReadAddress(item, OffLocalAddr, ipVersion) : null;
        IPAddress? remote = (flags & FlagRemoteAddrSet) != 0 ? ReadAddress(item, OffRemoteAddr, ipVersion) : null;

        ushort localPort = (flags & FlagLocalPortSet) != 0 ? (ushort)Marshal.ReadInt16(item, OffLocalPort) : (ushort)0;
        ushort remotePort = (flags & FlagRemotePortSet) != 0 ? (ushort)Marshal.ReadInt16(item, OffRemotePort) : (ushort)0;

        uint directionRaw = (uint)Marshal.ReadInt32(drop, DropOffDirection);

        return new WfpBlockedEvent(
            TimeUtc: timeUtc,
            Direction: WfpFormat.DirectionOf(directionRaw),
            Protocol: protocol,
            Local: local,
            LocalPort: localPort,
            Remote: remote,
            RemotePort: remotePort,
            ScopeId: scopeId,
            AppIdRaw: ReadAppId(item, flags),
            FilterId: (ulong)Marshal.ReadInt64(drop, DropOffFilterId),
            LayerId: (ushort)Marshal.ReadInt16(drop, DropOffLayerId),
            IsLoopback: Marshal.ReadInt32(drop, DropOffIsLoopback) != 0,

            // 対応表に無い値だったときに何が来ているか画面で分かるよう、生のまま持たせる
            DirectionRaw: directionRaw);
    }

    /// <summary>FILETIME が現実的な範囲にあるか。レイアウト誤りはまずここに出る。</summary>
    private static bool TryReadTime(long fileTime, out DateTime timeUtc)
    {
        timeUtc = default;
        if (fileTime <= 0) return false;

        try
        {
            DateTime candidate = DateTime.FromFileTimeUtc(fileTime);
            if (candidate.Year < 2000 || candidate > DateTime.UtcNow.AddDays(1)) return false;

            timeUtc = candidate;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static IPAddress? ReadAddress(IntPtr item, int offset, int ipVersion)
    {
        if (ipVersion == 0)
        {
            // WFP はホストバイトオーダーで入れてくる（iphlpapi のテーブルとは逆）
            return WfpFormat.Ipv4FromHostOrder((uint)Marshal.ReadInt32(item, offset));
        }

        if (ipVersion != 1) return null;

        byte[] bytes = new byte[16];
        Marshal.Copy(item + offset, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// プロセスのパス。取れなかったときは<b>理由が分かる印</b>を返す。
    ///
    /// 空文字で潰すと「そもそも機器が出していない」のか「こちらの読み方が違う」のか
    /// 切り分けられない（実機で「プロセスが何も出ない」と報告を受けた）。
    /// </summary>
    private static string ReadAppId(IntPtr item, uint flags)
    {
        // カーネル由来のトラフィックや受信の遮断では、そもそもプロセスが決まらない
        if ((flags & FlagAppIdSet) == 0) return "";

        uint size = (uint)Marshal.ReadInt32(item, OffAppIdSize);
        IntPtr data = Marshal.ReadIntPtr(item, OffAppIdData);

        // 長さが異常なら触らない。ここを信じて Copy すると一気にバッファ外へ出る
        if (size < 2 || size > 65534 || !LooksLikePointer(data))
            return NtPathText.Unreadable;

        byte[] bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);

        string path = NtPathText.FromBlobBytes(bytes);
        return path.Length > 0 ? path : NtPathText.Unreadable;
    }

    /// <summary>ユーザーモードで現実的なアドレス範囲か。null や小さな整数を弾く。</summary>
    private static bool LooksLikePointer(IntPtr p)
        => (ulong)p.ToInt64() >= 0x10000 && (ulong)p.ToInt64() < 0x7FFF_FFFF_FFFF;

    /// <summary>設定値を読む。読めなければ 0（記録していない）扱い。</summary>
    public static uint GetOption(IntPtr engine, uint option)
    {
        IntPtr value = IntPtr.Zero;
        try
        {
            if (FwpmEngineGetOption0(engine, option, out value) != 0 || !LooksLikePointer(value))
                return 0;

            // FWP_VALUE0 の先頭は FWP_DATA_TYPE。UINT32 でなければ解釈しない
            if (Marshal.ReadInt32(value, ValueTypeOffset) != FwpUint32) return 0;

            return (uint)Marshal.ReadInt32(value, ValueUint32Offset);
        }
        finally
        {
            if (value != IntPtr.Zero)
                FwpmFreeMemory0(ref value);
        }
    }

    /// <summary>selftest 用。エンジンを開いて設定を読むところまでを確かめる。</summary>
    internal static (bool Opened, int ValueType, uint Collect) InspectOption()
    {
        IntPtr engine = IntPtr.Zero;
        IntPtr value = IntPtr.Zero;

        try
        {
            if (FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out engine) != 0)
                return (false, -1, 0);

            if (FwpmEngineGetOption0(engine, OptionCollectNetEvents, out value) != 0 || !LooksLikePointer(value))
                return (true, -1, 0);

            return (true, Marshal.ReadInt32(value, ValueTypeOffset), (uint)Marshal.ReadInt32(value, ValueUint32Offset));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, -1, 0);
        }
        finally
        {
            if (value != IntPtr.Zero) FwpmFreeMemory0(ref value);
            if (engine != IntPtr.Zero) FwpmEngineClose0(engine);
        }
    }

    /// <summary>
    /// 記録の有無を切り替える。<b>システム全体に効く設定</b>なので、
    /// 呼ぶのはユーザーが明示的にボタンを押したときだけ。
    ///
    /// <b>自己診断からは絶対に呼ばない。</b>CI のランナーは管理者なので UAC も無しに
    /// 本当にランナーの設定が変わり、途中で落ちれば戻らない
    /// （ElevatedNetsh を selftest から呼ばないのとまったく同じ理由）。
    /// </summary>
    public static string? SetCollectNetEvents(bool enabled)
    {
        if (IntPtr.Size != 8) return "64 ビット版でのみ利用できます。";

        IntPtr engine = IntPtr.Zero;
        IntPtr value = Marshal.AllocHGlobal(16);

        try
        {
            // FWP_VALUE0（16 バイト）を手で組む。構造体は宣言しない
            Marshal.WriteInt64(value, 0, 0);
            Marshal.WriteInt64(value, 8, 0);
            Marshal.WriteInt32(value, ValueTypeOffset, FwpUint32);
            Marshal.WriteInt32(value, ValueUint32Offset, enabled ? 1 : 0);

            uint status = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out engine);
            if (status != 0) return DescribeStatus(status);

            status = FwpmEngineSetOption0(engine, OptionCollectNetEvents, value);
            return status == 0 ? null : DescribeStatus(status);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "この Windows では設定を変更できません。";
        }
        finally
        {
            if (engine != IntPtr.Zero) FwpmEngineClose0(engine);
            Marshal.FreeHGlobal(value);
        }
    }

    private static string DescribeStatus(uint status) => status switch
    {
        ErrorAccessDenied => "管理者権限が必要です。",
        _ => $"取得できませんでした（0x{status:X8}）。",
    };
}
