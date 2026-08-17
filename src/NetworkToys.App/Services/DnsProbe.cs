using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;

namespace NetworkToys.App.Services;

/// <param name="Type">レコード種別。</param>
/// <param name="Name">名前。</param>
/// <param name="Value">値（種別ごとに読みやすく整形したもの）。</param>
/// <param name="Ttl">残り有効秒数。</param>
public sealed record DnsRecordLine(string Type, string Name, string Value, int Ttl)
{
    /// <summary>TTL は 0 のとき（システムリゾルバ経由）表示しない。</summary>
    public string TtlText => Ttl > 0 ? $"{Ttl} 秒" : string.Empty;
}

/// <param name="ServerLabel">問い合わせ先の表示名。</param>
/// <param name="Success">応答が得られたか。</param>
/// <param name="Message">エラーや補足（NXDOMAIN など）。</param>
/// <param name="ElapsedMs">応答までの時間。</param>
/// <param name="Records">回答セクションの内容。</param>
internal sealed record DnsLookupResult(
    string ServerLabel,
    bool Success,
    string? Message,
    double ElapsedMs,
    IReadOnlyList<DnsRecordLine> Records)
{
    public string Summary => Success
        ? (Records.Count > 0 ? $"{Records.Count} 件 / {ElapsedMs:0} ms" : $"回答なし / {ElapsedMs:0} ms")
        : (Message ?? "失敗");
}

internal static class DnsProbe
{
    /// <summary>「システム既定」を指すときの擬似サーバ名。</summary>
    public const string SystemResolver = "システム既定";

    /// <summary>
    /// サーバごとに使い回すクライアント。LookupClient は内部に UDP ソケットの
    /// プールを持つ長寿命前提の作りで、問い合わせのたびに作り直すと
    /// プールが働かず一時ポートを無駄に消費する。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LookupClient> Clients = new();

    public static readonly string[] SupportedTypes =
        ["A", "AAAA", "CNAME", "MX", "NS", "TXT", "SOA", "SRV", "PTR"];

    /// <summary>
    /// 指定サーバへ問い合わせる。
    /// キャッシュは使わない（応答時間と実際の返答を見たいので）。
    /// </summary>
    public static async Task<DnsLookupResult> QueryAsync(
        string server, string name, string typeName, int timeoutMs, CancellationToken token)
    {
        if (string.Equals(server, SystemResolver, StringComparison.Ordinal))
            return await QuerySystemAsync(name, timeoutMs, token);

        if (!IPAddress.TryParse(server, out IPAddress? serverAddress))
            return new DnsLookupResult(server, false, "サーバの IP アドレスとして読み取れません", 0, []);

        if (!Enum.TryParse(typeName, ignoreCase: true, out QueryType queryType))
            return new DnsLookupResult(server, false, $"未対応のレコード種別です: {typeName}", 0, []);

        LookupClient client = Clients.GetOrAdd($"{serverAddress}|{timeoutMs}", _ =>
            new LookupClient(new LookupClientOptions(serverAddress)
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
                UseCache = false,
                Retries = 0,
                UseTcpFallback = true,
                ThrowDnsErrors = false,
            }));

        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            IDnsQueryResponse response = await client.QueryAsync(name, queryType, cancellationToken: token);
            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (response.HasError)
                return new DnsLookupResult(server, false, response.ErrorMessage, elapsedMs, []);

            List<DnsRecordLine> records = [.. response.Answers.Select(Describe)];
            return new DnsLookupResult(server, true, null, elapsedMs, records);
        }
        catch (DnsResponseException ex)
        {
            return new DnsLookupResult(server, false, ex.DnsError.ToString(), Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, []);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new DnsLookupResult(server, false, "タイムアウト", timeoutMs, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 型を絞ると、想定外の 1 例外（不正なドメイン名の ArgumentException など）が
            // 呼び出し元の Task.WhenAll ごと巻き込み、他のサーバの結果まで空欄で残る。
            // この画面の目的は「どのサーバで引けてどこで引けないか」の比較なので、
            // 1 台の異常は 1 枠の失敗として閉じ込める
            return new DnsLookupResult(server, false, ex.Message, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, []);
        }
    }

    /// <summary>
    /// OS のリゾルバで引く。hosts ファイル・DNS キャッシュ・DoH 設定など、
    /// 実際にこの PC のアプリが体験する解決結果を反映する。種別は指定できない。
    /// </summary>
    private static async Task<DnsLookupResult> QuerySystemAsync(string name, int timeoutMs, CancellationToken token)
    {
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeoutMs);

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(name, timeoutCts.Token);
            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            List<DnsRecordLine> records =
            [
                .. addresses.Select(a => new DnsRecordLine(
                    a.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A",
                    name,
                    a.ToString(),
                    0)),
            ];

            return new DnsLookupResult(SystemResolver, true, null, elapsedMs, records);
        }
        catch (SocketException ex)
        {
            return new DnsLookupResult(SystemResolver, false, ex.SocketErrorCode.ToString(), Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, []);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new DnsLookupResult(SystemResolver, false, "タイムアウト", timeoutMs, []);
        }
    }

    /// <summary>レコードを種別ごとに読みやすい 1 行にする。</summary>
    private static DnsRecordLine Describe(DnsResourceRecord record)
    {
        string value = record switch
        {
            ARecord a => a.Address.ToString(),
            AaaaRecord aaaa => aaaa.Address.ToString(),
            CNameRecord cname => cname.CanonicalName.Value,
            MxRecord mx => $"{mx.Preference} {mx.Exchange.Value}",
            NsRecord ns => ns.NSDName.Value,
            TxtRecord txt => string.Join(" ", txt.Text),
            SoaRecord soa => $"{soa.MName.Value} {soa.RName.Value} serial={soa.Serial}",
            SrvRecord srv => $"{srv.Priority} {srv.Weight} {srv.Port} {srv.Target.Value}",
            PtrRecord ptr => ptr.PtrDomainName.Value,
            CaaRecord caa => $"{caa.Flags} {caa.Tag} {caa.Value}",
            _ => record.ToString() ?? string.Empty,
        };

        return new DnsRecordLine(
            record.RecordType.ToString(),
            record.DomainName.Value,
            value,
            record.TimeToLive);
    }
}
