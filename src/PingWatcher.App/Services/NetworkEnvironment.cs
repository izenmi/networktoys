using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingWatcher.App.Services;

/// <summary>
/// いま自分がどのネットワークに繋がっているか。
/// ステータスバーの表示に使うほか、初回起動時の宛先の既定値にも使う。
/// Phase 4 でここに SSID が加わる。
/// </summary>
/// <param name="InterfaceName">使用中のインターフェース名。</param>
/// <param name="LocalAddress">自分の IPv4 アドレス。</param>
/// <param name="PrefixLength">サブネットのプレフィックス長。</param>
/// <param name="Gateway">既定ゲートウェイ。</param>
/// <param name="DnsServers">DNS サーバ。</param>
internal sealed record NetworkSnapshot(
    string? InterfaceName,
    IPAddress? LocalAddress,
    int PrefixLength,
    IPAddress? Gateway,
    IReadOnlyList<IPAddress> DnsServers)
{
    public static readonly NetworkSnapshot Empty = new(null, null, 0, null, []);

    /// <summary>自分のサブネットを CIDR 表記で返す。スキャン範囲の既定値に使う。</summary>
    public string? SubnetCidr
    {
        get
        {
            if (LocalAddress is null || PrefixLength <= 0) return null;

            IPAddress network = Core.Addressing.IpMath.NetworkAddress(LocalAddress, PrefixLength);
            return $"{network}/{PrefixLength}";
        }
    }
}

/// <summary>
/// IP 設定タブに出すアダプタ 1 枚ぶん。<see cref="Name"/> は netsh の
/// <c>name=</c> にそのまま使う接続名。
/// </summary>
internal sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    bool IsUp,
    bool IsDhcp,
    IPAddress? Address,
    int PrefixLength,
    IPAddress? Gateway,
    IReadOnlyList<IPAddress> DnsServers)
{
    /// <summary>現在値の 1 行表示。</summary>
    public string Summary
    {
        get
        {
            if (Address is null)
                return IsUp ? "IPv4 アドレスなし" : "未接続";

            string source = IsDhcp ? "DHCP" : "固定";
            string address = PrefixLength > 0 ? $"{Address}/{PrefixLength}" : Address.ToString();
            string gateway = Gateway is null ? "" : $" GW {Gateway}";
            return $"{source}: {address}{gateway}";
        }
    }
}

internal static class NetworkEnvironment
{
    /// <summary>
    /// IP 設定タブ用の全アダプタ列挙。除外は Loopback だけ — ダウン中のアダプタも出す
    /// (「機器に繋ぐ前に、抜けている NIC へ固定 IP を仕込む」のが現場の典型操作)。
    /// 稼働中を先頭に並べる。
    /// </summary>
    public static IReadOnlyList<NetworkAdapterInfo> ListAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties properties = nic.GetIPProperties();

                UnicastIPAddressInformation? unicast = properties.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                int prefix = 0;
                if (unicast is not null)
                {
                    try
                    {
                        prefix = unicast.PrefixLength;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // 取れない環境では 0 のままにする
                    }
                }

                bool isDhcp = false;
                try
                {
                    isDhcp = properties.GetIPv4Properties().IsDhcpEnabled;
                }
                catch (NetworkInformationException)
                {
                    // IPv4 が無効なアダプタでは取れない。固定扱いにしておく
                }

                IPAddress? gateway = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

                IPAddress[] dns = [.. properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)];

                adapters.Add(new NetworkAdapterInfo(
                    nic.Name,
                    nic.Description,
                    nic.OperationalStatus == OperationalStatus.Up,
                    isDhcp,
                    unicast?.Address,
                    prefix,
                    gateway,
                    dns));
            }
        }
        catch (NetworkInformationException)
        {
            // 取得できない環境でも空の一覧で動かす
        }

        return [.. adapters.OrderByDescending(a => a.IsUp).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 既定ゲートウェイを持つ、稼働中のインターフェースを 1 つ選んで情報を返す。
    /// 複数ある場合は最初に見つかったものを使う（用途上それで足りる）。
    /// </summary>
    public static NetworkSnapshot Current()
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties properties = nic.GetIPProperties();

                IPAddress? gateway = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

                if (gateway is null) continue;

                UnicastIPAddressInformation? unicast = properties.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                if (unicast is null) continue;

                int prefix = 0;
                try
                {
                    prefix = unicast.PrefixLength;
                }
                catch (PlatformNotSupportedException)
                {
                    // 取れない環境では 0 のままにする
                }

                IPAddress[] dns = [.. properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)];

                return new NetworkSnapshot(nic.Name, unicast.Address, prefix, gateway, dns);
            }
        }
        catch (NetworkInformationException)
        {
            // 取得できない環境でもアプリは動かす
        }

        return NetworkSnapshot.Empty;
    }
}
