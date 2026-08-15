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

internal static class NetworkEnvironment
{
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
