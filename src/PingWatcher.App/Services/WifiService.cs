using ManagedNativeWifi;

namespace PingWatcher.App.Services;

/// <param name="Ssid">接続中のネットワーク名。</param>
/// <param name="Bssid">接続先アクセスポイントの MAC。</param>
/// <param name="Vendor">BSSID から引いた機器ベンダー。</param>
/// <param name="SignalQuality">0〜100 の信号品質。</param>
/// <param name="Rssi">受信強度（dBm）。取れなければ null。</param>
/// <param name="Channel">チャンネル番号。BSS 一覧から引けなければ 0。</param>
/// <param name="Band">周波数帯（GHz）。</param>
/// <param name="PhyType">物理層の種別（11n / 11ac / 11ax など）。</param>
/// <param name="Authentication">認証方式。</param>
/// <param name="Encryption">暗号化方式。</param>
/// <param name="RxRateKbps">受信リンク速度。スループットではない。</param>
/// <param name="TxRateKbps">送信リンク速度。スループットではない。</param>
/// <param name="InterfaceName">無線アダプタ名。</param>
internal sealed record WifiConnection(
    string Ssid,
    string Bssid,
    string? Vendor,
    int SignalQuality,
    int? Rssi,
    int Channel,
    float Band,
    string PhyType,
    string Authentication,
    string Encryption,
    int RxRateKbps,
    int TxRateKbps,
    string InterfaceName);

/// <param name="Ssid">ネットワーク名。ステルスなら空。</param>
/// <param name="Bssid">アクセスポイントの MAC。</param>
/// <param name="Vendor">BSSID から引いた機器ベンダー。</param>
/// <param name="LinkQuality">0〜100。</param>
/// <param name="Rssi">受信強度（dBm）。</param>
/// <param name="Channel">チャンネル番号。</param>
/// <param name="Band">周波数帯（GHz）。</param>
/// <param name="IsConnected">自分が今つないでいる AP か。</param>
internal sealed record WifiAccessPoint(
    string Ssid,
    string Bssid,
    string? Vendor,
    int LinkQuality,
    int Rssi,
    int Channel,
    float Band,
    bool IsConnected);

/// <summary>無線 LAN 情報が取れなかった理由。UI の案内を出し分けるために区別する。</summary>
internal enum WifiFailure
{
    None,

    /// <summary>
    /// 位置情報の許可が無い。Windows 11 24H2 以降、許可なしでは
    /// WlanScan / WlanGetNetworkBssList / WlanQueryInterface(現在の接続) が
    /// ACCESS_DENIED になる。
    /// </summary>
    LocationDenied,

    /// <summary>無線アダプタが無い、または無効。</summary>
    NoAdapter,

    /// <summary>その他。</summary>
    Error,
}

internal sealed record WifiSnapshot(
    WifiConnection? Connection,
    IReadOnlyList<WifiAccessPoint> AccessPoints,
    WifiFailure Failure,
    string? Message);

/// <summary>
/// ManagedNativeWifi の薄いラッパ。
///
/// <b>起動時に呼んではいけない。</b>スキャン系 API は位置情報の同意を要求するので、
/// ユーザーが無線画面を開いた（＝その情報を見たいと意思表示した）ときに初めて呼ぶ。
/// 同意のダイアログが出るタイミングとしても自然になる。
/// </summary>
internal static class WifiService
{
    /// <summary>
    /// 周辺の AP を洗い直してから一覧を返す。
    ///
    /// Windows の WLAN サービス自体が既定 60 秒間隔でしかスキャンしないため、
    /// これを短い周期で呼んでも新しい結果は返らない。呼び出し側で 10 秒以上あけること。
    /// </summary>
    public static async Task<WifiSnapshot> ScanAsync(CancellationToken token)
    {
        try
        {
            // スキャン完了の通知は 4 秒でタイムアウトするのが作法
            await NativeWifi.ScanNetworksAsync(TimeSpan.FromSeconds(4), token);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Denied(ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // スキャンに失敗しても直前のキャッシュは読めることがあるので続行する
            CrashLog.Write(ex, "WifiService.ScanAsync(scan)");
        }

        return Collect();
    }

    /// <summary>スキャンを走らせずに、いま OS が持っている一覧を読む。</summary>
    public static WifiSnapshot Collect()
    {
        try
        {
            InterfaceInfo[] interfaces = [.. NativeWifi.EnumerateInterfaces()];
            if (interfaces.Length == 0)
                return new WifiSnapshot(null, [], WifiFailure.NoAdapter, "無線 LAN アダプタが見つかりません。");

            var accessPoints = new List<WifiAccessPoint>();
            WifiConnection? connection = null;

            foreach (InterfaceInfo adapter in interfaces)
            {
                (ActionResult bssResult, IEnumerable<BssNetworkInfo> list) = NativeWifi.EnumerateBssNetworks(adapter.Id);
                BssNetworkInfo[] bssNetworks = bssResult == ActionResult.Success ? [.. list] : [];

                (ActionResult connectionResult, CurrentConnectionInfo? current) = NativeWifi.GetCurrentConnection(adapter.Id);

                string? connectedBssid = null;
                if (connectionResult == ActionResult.Success && current is not null)
                {
                    connectedBssid = FormatMac(current.Bssid);
                    connection ??= BuildConnection(adapter, current, bssNetworks, connectedBssid);
                }

                foreach (BssNetworkInfo bss in bssNetworks)
                {
                    string bssid = FormatMac(bss.Bssid);

                    accessPoints.Add(new WifiAccessPoint(
                        bss.Ssid?.ToString() ?? string.Empty,
                        bssid,
                        OuiCatalog.FindVendor(bssid),
                        bss.LinkQuality,
                        bss.Rssi,
                        bss.Channel,
                        bss.Band,
                        connectedBssid is not null && string.Equals(bssid, connectedBssid, StringComparison.OrdinalIgnoreCase)));
                }
            }

            // 強い順に並べる。近くにある AP から見たいので
            accessPoints.Sort((a, b) => b.Rssi.CompareTo(a.Rssi));

            string? message = connection is null && accessPoints.Count == 0
                ? "周辺のアクセスポイントが見つかりませんでした。"
                : null;

            return new WifiSnapshot(connection, accessPoints, WifiFailure.None, message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Denied(ex);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WifiService.Collect");
            return new WifiSnapshot(null, [], WifiFailure.Error, ex.Message);
        }
    }

    private static WifiConnection BuildConnection(
        InterfaceInfo adapter,
        CurrentConnectionInfo current,
        IReadOnlyList<BssNetworkInfo> bssNetworks,
        string connectedBssid)
    {
        // CurrentConnectionInfo にチャンネルは無いので、BSS 一覧から BSSID で引く
        BssNetworkInfo? bss = null;
        foreach (BssNetworkInfo candidate in bssNetworks)
        {
            if (string.Equals(FormatMac(candidate.Bssid), connectedBssid, StringComparison.OrdinalIgnoreCase))
            {
                bss = candidate;
                break;
            }
        }

        (ActionResult rssiResult, int rssi) = NativeWifi.GetRssi(adapter.Id);

        return new WifiConnection(
            current.Ssid?.ToString() ?? current.ProfileName ?? string.Empty,
            connectedBssid,
            OuiCatalog.FindVendor(connectedBssid),
            current.SignalQuality,
            rssiResult == ActionResult.Success ? rssi : bss?.Rssi,
            bss?.Channel ?? 0,
            bss?.Band ?? 0f,
            current.PhyType.ToString(),
            current.AuthenticationAlgorithm.ToString(),
            current.CipherAlgorithm.ToString(),
            current.RxRate,
            current.TxRate,
            adapter.Description);
    }

    /// <summary>
    /// 接続中 AP の受信強度だけを取る。時系列表示のために毎秒呼ぶので、
    /// スキャンを伴わない軽い API だけを使う。
    /// </summary>
    public static int? GetRssi()
    {
        try
        {
            // 接続していないアダプタでは Success にならないので、
            // 接続状態を別途調べる必要はない（InterfaceInfo.IsConnected は 3.0.2 には無い）
            foreach (InterfaceInfo adapter in NativeWifi.EnumerateInterfaces())
            {
                (ActionResult result, int rssi) = NativeWifi.GetRssi(adapter.Id);
                if (result == ActionResult.Success)
                    return rssi;
            }
        }
        catch (Exception ex)
        {
            // ManagedNativeWifi はアダプタの抜き差しや無効化のタイミングで
            // Win32Exception なども投げる。ここはタイマーから毎秒呼ばれるので、
            // 型を絞って取り漏らすとアプリごと落ちる。初回だけ記録して黙る
            if (!_rssiFailureLogged)
            {
                _rssiFailureLogged = true;
                CrashLog.Write(ex, "WifiService.GetRssi");
            }

            return null;
        }

        return null;
    }

    private static bool _rssiFailureLogged;

    private static WifiSnapshot Denied(Exception ex) => new(
        null,
        [],
        WifiFailure.LocationDenied,
        ex.Message);

    /// <summary>NetworkIdentifier は BSSID の場合 ToString() が空になりうるのでバイト列から組み立てる。</summary>
    private static string FormatMac(NetworkIdentifier? identifier)
    {
        byte[]? bytes = identifier?.ToBytes();
        return bytes is { Length: > 0 }
            ? string.Join('-', bytes.Select(b => b.ToString("X2")))
            : string.Empty;
    }
}
