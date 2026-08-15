using System.Globalization;

namespace PingWatcher.Core.Tftp;

/// <param name="BlockSize">1 ブロックのバイト数。既定 512。</param>
/// <param name="TimeoutSeconds">再送までの秒。</param>
/// <param name="Accepted">OACK で返すオプション。1 つも無ければ OACK を送らない。</param>
public readonly record struct TftpTransferOptions(int BlockSize, int TimeoutSeconds, IReadOnlyDictionary<string, string> Accepted);

/// <summary>
/// blksize / timeout / tsize（RFC2347/2348/2349）の交渉。
/// 範囲外や読めない値は無視して既定に落とす（相手が困らないよう寛容に）。
/// </summary>
public static class TftpNegotiation
{
    public const int DefaultBlockSize = 512;
    public const int MinBlockSize = 8;
    public const int MaxBlockSize = 65464;   // RFC2348 の上限
    public const int DefaultTimeout = 3;

    /// <param name="requested">RRQ/WRQ に入っていたオプション。</param>
    /// <param name="transferSize">
    /// tsize に返す値。RRQ（送信）なら実ファイルサイズ、WRQ（受信）なら
    /// 相手が申告した値をそのまま返す。0 のときは返さない。
    /// </param>
    public static TftpTransferOptions Negotiate(IReadOnlyDictionary<string, string> requested, long transferSize)
    {
        int blockSize = DefaultBlockSize;
        int timeout = DefaultTimeout;
        var accepted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (requested.TryGetValue("blksize", out string? bs)
            && int.TryParse(bs, NumberStyles.None, CultureInfo.InvariantCulture, out int requestedBlock))
        {
            blockSize = Math.Clamp(requestedBlock, MinBlockSize, MaxBlockSize);
            accepted["blksize"] = blockSize.ToString(CultureInfo.InvariantCulture);
        }

        if (requested.TryGetValue("timeout", out string? to)
            && int.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out int requestedTimeout)
            && requestedTimeout is >= 1 and <= 255)
        {
            timeout = requestedTimeout;
            accepted["timeout"] = timeout.ToString(CultureInfo.InvariantCulture);
        }

        if (requested.ContainsKey("tsize") && transferSize >= 0)
            accepted["tsize"] = transferSize.ToString(CultureInfo.InvariantCulture);

        return new TftpTransferOptions(blockSize, timeout, accepted);
    }
}
