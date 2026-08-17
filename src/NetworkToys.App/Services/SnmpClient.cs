using System.Net;
using System.Net.Sockets;
using NetworkToys.Core.Snmp;

namespace NetworkToys.App.Services;

/// <param name="Success">応答が得られたか。</param>
/// <param name="Message">エラーや補足（タイムアウトなど）。</param>
/// <param name="VarBinds">取得できた varbind。</param>
internal sealed record SnmpResult(bool Success, string? Message, IReadOnlyList<VarBind> VarBinds);

/// <summary>
/// SNMP v1/v2c の GET / GETNEXT / ウォークを行う。<see cref="SnmpCodec"/>(Core)で
/// 組み立て・解析し、ここは UDP の送受信とタイムアウト・再送だけを見る。
/// </summary>
internal sealed class SnmpClient
{
    private const int Timeout = 2000;
    private const int Retries = 1;

    /// <summary>ウォークの暴走を防ぐ上限。</summary>
    private const int MaxWalk = 5000;

    private int _requestId = Environment.TickCount & 0x7FFFFFFF;

    public Task<SnmpResult> GetAsync(string host, string community, SnmpVersion version, IReadOnlyList<Oid> oids, CancellationToken token)
        => RequestAsync(host, community, version, oids, next: false, token);

    /// <summary>rootOid の配下を GETNEXT で順にたどる。</summary>
    public async Task<SnmpResult> WalkAsync(string host, string community, SnmpVersion version, Oid rootOid, CancellationToken token)
    {
        var collected = new List<VarBind>();
        Oid current = rootOid;

        while (collected.Count < MaxWalk)
        {
            token.ThrowIfCancellationRequested();

            SnmpResult step = await RequestAsync(host, community, version, [current], next: true, token).ConfigureAwait(false);
            if (!step.Success)
                return collected.Count > 0 ? new SnmpResult(true, step.Message, collected) : step;

            if (step.VarBinds.Count == 0) break;

            VarBind vb = step.VarBinds[0];

            // 範囲を出た・末尾に達したら終わり
            if (!vb.Oid.IsDescendantOf(rootOid)
                || vb.Value.Tag is BerTag.EndOfMibView or BerTag.NoSuchObject or BerTag.NoSuchInstance)
            {
                break;
            }

            collected.Add(vb);
            current = vb.Oid;
        }

        return new SnmpResult(true, null, collected);
    }

    private async Task<SnmpResult> RequestAsync(string host, string community, SnmpVersion version, IReadOnlyList<Oid> oids, bool next, CancellationToken token)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            // ホスト名なら解決を試みる（引けなければ失敗）
            try
            {
                IPAddress[] resolved = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                address = Array.Find(resolved, a => a.AddressFamily == AddressFamily.InterNetwork) ?? resolved.FirstOrDefault();
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return new SnmpResult(false, "宛先を解決できません。", []);
            }
        }

        if (address is null)
            return new SnmpResult(false, "宛先を解決できません。", []);

        int requestId = Interlocked.Increment(ref _requestId) & 0x7FFFFFFF;
        byte[] packet = SnmpCodec.BuildGet(version, community, requestId, oids, next);

        for (int attempt = 0; attempt <= Retries; attempt++)
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(address, 161);

            try
            {
                await udp.SendAsync(packet, token).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);

                UdpReceiveResult received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                SnmpMessage? message = SnmpCodec.Parse(received.Buffer);
                if (message is null)
                    return new SnmpResult(false, "応答を解釈できませんでした。", []);

                if (message.RequestId != requestId)
                    continue;   // 前の応答が遅れて来た。無視して待ち直す（再送で拾う）

                if (message.ErrorStatus != 0)
                    return new SnmpResult(false, message.ErrorText, message.VarBinds);

                return new SnmpResult(true, null, message.VarBinds);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // タイムアウト。再送
            }
            catch (SocketException ex)
            {
                return new SnmpResult(false, $"送受信に失敗しました（{ex.SocketErrorCode}）。", []);
            }
        }

        return new SnmpResult(false, "応答がありません（タイムアウト）。", []);
    }
}
