using System.Runtime.InteropServices;

namespace PingWatcher.App.Interop;

/// <summary>PAC を評価した結果。</summary>
/// <param name="Proxy">使うべきプロキシ（<c>http://host:port</c>）。直接出るなら空。</param>
/// <param name="Error">評価に失敗した理由。成功なら null。</param>
internal sealed record PacLookup(string Proxy, string? Error);

/// <summary>
/// WinHTTP を使って <b>指定した PAC を、指定した URL について評価する</b>。
///
/// <b>なぜ P/Invoke なのか。</b>.NET の <c>WebProxy</c> は固定のアドレスしか扱えず、
/// PAC の <c>FindProxyForURL</c> を実行する仕組みが標準に無い。
/// <c>HttpClientHandler</c> に任せるとシステムの設定（＝いま端末に入っている PAC）
/// しか使えないので、<b>PAC を切り替えて両方で試験する</b>ことができない。
///
/// <b>システムのプロキシ設定は一切書き換えない。</b>評価するだけなので、
/// ほかのアプリを巻き込まないし、戻し忘れの事故も起きない。
///
/// WFP の「構造体を宣言しない」という決まりはここには当てはまらない。
/// あちらはポインタの配列を受け取る API で、構造体サイズを誤ると隣を踏む形だった。
/// こちらは 3 フィールドの単純な構造体を<b>こちらが用意して渡す</b>だけで、
/// 書き込まれる大きさも決まっている。
/// </summary>
internal static class WinHttpNativeMethods
{
    private const uint AccessTypeNoProxy = 1;

    /// <summary>WINHTTP_AUTOPROXY_CONFIG_URL。指定した PAC を使う。</summary>
    private const uint AutoProxyConfigUrl = 0x00000002;

    /// <summary>WINHTTP_ACCESS_TYPE_NO_PROXY。PAC が DIRECT を返したとき。</summary>
    private const uint ProxyTypeDirect = 1;

    private const uint ErrorWinHttpLoginFailure = 12015;
    private const uint ErrorWinHttpUnableToDownloadScript = 12167;
    private const uint ErrorWinHttpBadAutoProxyScript = 12166;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AutoProxyOptions
    {
        public uint Flags;
        public uint AutoDetectFlags;
        public IntPtr AutoConfigUrl;
        public IntPtr Reserved;
        public uint ReservedDword;

        /// <summary>PAC の取得自体に認証が要るとき、ログオン中の資格情報で通す。</summary>
        public int AutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProxyInfo
    {
        public uint AccessType;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(
        string? agent, uint accessType, string? proxy, string? bypass, uint flags);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(
        IntPtr session, string url, ref AutoProxyOptions options, out ProxyInfo info);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetDefaultProxyConfiguration(ref ProxyInfo info);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>
    /// <paramref name="pacUrl"/> の PAC を、<paramref name="targetUrl"/> について評価する。
    ///
    /// PAC は宛先ごとに違う答えを返しうる（社内は直接・外はプロキシ、など）ので、
    /// <b>試験する URL ごとに引き直す</b>。
    /// </summary>
    public static PacLookup Resolve(string pacUrl, string targetUrl)
    {
        IntPtr session = IntPtr.Zero;
        IntPtr configUrl = IntPtr.Zero;

        try
        {
            // PAC を取りに行くこのセッション自体はプロキシを通さない
            session = WinHttpOpen("PingWatcher", AccessTypeNoProxy, null, null, 0);
            if (session == IntPtr.Zero)
                return new PacLookup("", $"PAC を評価する仕組みを開けません（{Marshal.GetLastWin32Error()}）。");

            configUrl = Marshal.StringToHGlobalUni(pacUrl);

            var options = new AutoProxyOptions
            {
                Flags = AutoProxyConfigUrl,
                AutoConfigUrl = configUrl,
                AutoLogonIfChallenged = 1,
            };

            if (!WinHttpGetProxyForUrl(session, targetUrl, ref options, out ProxyInfo info))
                return new PacLookup("", Describe((uint)Marshal.GetLastWin32Error(), pacUrl));

            try
            {
                // PAC が DIRECT を返した場合。プロキシ無しで出るのが正しい
                if (info.AccessType == ProxyTypeDirect || info.Proxy == IntPtr.Zero)
                    return new PacLookup("", null);

                string? list = Marshal.PtrToStringUni(info.Proxy);

                return new PacLookup(Core.Verify.PacProxy.FirstProxy(list), null);
            }
            finally
            {
                // 返ってきた文字列はこちらで解放する
                if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
                if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new PacLookup("", "この Windows では PAC を評価できません。");
        }
        finally
        {
            if (configUrl != IntPtr.Zero) Marshal.FreeHGlobal(configUrl);
            if (session != IntPtr.Zero) WinHttpCloseHandle(session);
        }
    }

    /// <summary>失敗の理由を、次に何をすればよいか分かる文言にする。</summary>
    private static string Describe(uint error, string pacUrl) => error switch
    {
        ErrorWinHttpUnableToDownloadScript => $"PAC を取得できません（{pacUrl}）。URL と経路をご確認ください。",
        ErrorWinHttpBadAutoProxyScript => $"PAC の中身を実行できません（{pacUrl}）。",
        ErrorWinHttpLoginFailure => $"PAC の取得で認証に失敗しました（{pacUrl}）。",
        _ => $"PAC を評価できません（{pacUrl} / エラー {error}）。",
    };

    /// <summary>
    /// <b>PC 全体の</b> WinHTTP プロキシ設定を読む（<c>netsh winhttp show proxy</c> と同じ中身）。
    ///
    /// netsh の出力は<b>表示言語で変わる</b>ので読まない。API から直に取る。
    /// 読むだけなら管理者権限は要らない。
    /// </summary>
    /// <returns>直接接続なら (true, "", "")。読めなければ null。</returns>
    internal static (bool Direct, string Server, string Bypass)? ReadDefaultProxy()
    {
        var info = new ProxyInfo();

        try
        {
            if (!WinHttpGetDefaultProxyConfiguration(ref info)) return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        try
        {
            // 1 = NO_PROXY（直接）。3 = NAMED_PROXY（固定）
            string server = info.Proxy != IntPtr.Zero ? Marshal.PtrToStringUni(info.Proxy) ?? "" : "";
            string bypass = info.ProxyBypass != IntPtr.Zero ? Marshal.PtrToStringUni(info.ProxyBypass) ?? "" : "";

            return (info.AccessType == 1 || server.Length == 0, server, bypass);
        }
        finally
        {
            // 文字列は WinHTTP が確保したもの。呼んだ側が返す
            if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
            if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
        }
    }
}
