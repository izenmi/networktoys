namespace PingWatcher.Core.Net;

/// <summary>
/// 接続表をプロセスごとにグループ化した表示行の列へ整形する。
/// 並びは決定的な全順序（差分同期の前提）: プロセス名 → PID、グループ内は
/// プロトコル → ローカルポート → リモート。
/// </summary>
public static class ConnectionTableView
{
    /// <summary>
    /// GetExtendedTcpTable/GetExtendedUdpTable のポート欄はネットワークバイトオーダーの
    /// 16bit が DWORD の下位に入っている。ここだけがテストできる変換なので Core に置く。
    /// </summary>
    public static ushort PortFromNetworkOrder(uint raw)
        => (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    /// <param name="processNames">PID → プロセス名。引けない PID は Core 側の既定名になる。</param>
    /// <param name="filter">部分一致の絞り込み。プロセス名に合えばグループ丸ごと、
    /// そうでなければ行単位で残す。空なら全件。</param>
    /// <param name="rates">通信量。null は通信量なし（非管理者）で、列は「—」になる。</param>
    public static IReadOnlyList<ConnectionListRow> BuildRows(
        IReadOnlyList<ConnectionRow> rows,
        IReadOnlyDictionary<int, string> processNames,
        string? filter,
        ConnectionRates? rates)
    {
        string trimmed = filter?.Trim() ?? "";

        var byPid = new Dictionary<int, List<ConnectionRow>>();
        foreach (ConnectionRow row in rows)
        {
            if (!byPid.TryGetValue(row.Pid, out List<ConnectionRow>? list))
                byPid[row.Pid] = list = [];
            list.Add(row);
        }

        var groups = byPid
            .Select(pair => (Pid: pair.Key, Name: ResolveProcessName(pair.Key, processNames), Rows: pair.Value))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Pid);

        var result = new List<ConnectionListRow>();
        var usedKeys = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach ((int pid, string name, List<ConnectionRow> groupRows) in groups)
        {
            bool wholeGroup = trimmed.Length == 0 || name.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
            groupRows.Sort(CompareRows);

            var details = new List<ConnectionDetailRow>();
            double sentTotal = 0;
            double receivedTotal = 0;

            foreach (ConnectionRow row in groupRows)
            {
                string protocol = ProtocolText(row.Protocol);
                string local = FormatEndpoint(row.LocalAddress, row.LocalPort);
                string remote = HasRemote(row) ? FormatEndpoint(row.RemoteAddress, row.RemotePort) : "—";
                (string stateText, ConnectionStateKind stateKind) = TcpStateText.Describe(row.State);

                if (!wholeGroup && !Matches(trimmed, protocol, local, remote, stateText))
                    continue;

                (double sent, double received) = rates?.Lookup(row) ?? (0, 0);
                sentTotal += sent;
                receivedTotal += received;

                string key = MakeUniqueKey($"d{pid}|{protocol}|{local}|{remote}", usedKeys);
                details.Add(new ConnectionDetailRow(
                    protocol, local, remote, stateText, stateKind,
                    RateText(rates, sent), RateText(rates, received), key));
            }

            if (details.Count == 0)
                continue;

            result.Add(new ConnectionGroupRow(
                name,
                pid > 0 ? $"PID {pid}" : "",
                $"{details.Count} 件",
                RateText(rates, sentTotal),
                RateText(rates, receivedTotal),
                $"g{pid}"));
            result.AddRange(details);
        }

        return result;
    }

    /// <summary>ステータス行用の集計（フィルタ前の全件）。</summary>
    public static (int Tcp, int Udp, int Processes) Count(IReadOnlyList<ConnectionRow> rows)
    {
        int tcp = 0;
        int udp = 0;
        var pids = new HashSet<int>();

        foreach (ConnectionRow row in rows)
        {
            if (row.Protocol is ConnectionProtocol.TcpV4 or ConnectionProtocol.TcpV6)
                tcp++;
            else
                udp++;
            pids.Add(row.Pid);
        }

        return (tcp, udp, pids.Count);
    }

    public static string ResolveProcessName(int pid, IReadOnlyDictionary<int, string> processNames)
    {
        if (processNames.TryGetValue(pid, out string? name) && name.Length > 0)
            return name;

        return pid switch
        {
            0 => "(所有プロセスなし)",   // TIME_WAIT の残骸で正常
            4 => "System",
            _ => $"PID {pid}",
        };
    }

    private static string ProtocolText(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.TcpV4 => "TCP",
        ConnectionProtocol.TcpV6 => "TCPv6",
        ConnectionProtocol.UdpV4 => "UDP",
        _ => "UDPv6",
    };

    private static string FormatEndpoint(string address, ushort port)
        => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";

    private static bool HasRemote(ConnectionRow row)
        => row.Protocol is ConnectionProtocol.TcpV4 or ConnectionProtocol.TcpV6
           && row.State != TcpConnectionState.Listen;

    private static bool Matches(string filter, string protocol, string local, string remote, string stateText)
        => protocol.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || local.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || remote.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || stateText.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string RateText(ConnectionRates? rates, double bytesPerSecond)
    {
        // 通信量なし（非管理者）もゼロも「—」。数字が出るのは実際に流れたときだけ
        if (rates is null || bytesPerSecond <= 0)
            return "—";
        return ByteRateFormat.Format(bytesPerSecond);
    }

    private static int CompareRows(ConnectionRow x, ConnectionRow y)
    {
        int c = x.Protocol.CompareTo(y.Protocol);
        if (c != 0) return c;
        c = x.LocalPort.CompareTo(y.LocalPort);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.LocalAddress, y.LocalAddress);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.RemoteAddress, y.RemoteAddress);
        if (c != 0) return c;
        c = x.RemotePort.CompareTo(y.RemotePort);
        if (c != 0) return c;
        return x.State.CompareTo(y.State);
    }

    private static string MakeUniqueKey(string key, Dictionary<string, int> usedKeys)
    {
        // UDP は SO_REUSEADDR で同一エンドポイントが重複しうるので、連番で一意にする
        if (usedKeys.TryGetValue(key, out int seen))
        {
            usedKeys[key] = seen + 1;
            return $"{key}#{seen + 1}";
        }

        usedKeys[key] = 1;
        return key;
    }
}
