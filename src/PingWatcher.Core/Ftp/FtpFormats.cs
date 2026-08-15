using System.Globalization;
using System.Net;

namespace PingWatcher.Core.Ftp;

/// <param name="Name">ファイル/フォルダ名。</param>
/// <param name="IsDirectory">フォルダか。</param>
/// <param name="Size">バイト数。フォルダは 0。</param>
/// <param name="Modified">最終更新時刻。</param>
public readonly record struct FtpListEntry(string Name, bool IsDirectory, long Size, DateTime Modified);

/// <summary>
/// FTP の応答文字列の整形。純粋関数なのでテストで固定できる。
/// <b>日時は必ず InvariantCulture の英語月名。</b>和暦や日本語カルチャで
/// LIST を出すと、多くの FTP クライアントが日付を読めなくなる。
/// </summary>
public static class FtpFormats
{
    /// <summary>PASV の応答。h1,h2,h3,h4,p1,p2（p1*256+p2 がポート）。</summary>
    public static string PassiveModeReply(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        byte[] ip = address.MapToIPv4().GetAddressBytes();
        int high = (port >> 8) & 0xFF;
        int low = port & 0xFF;

        return string.Create(CultureInfo.InvariantCulture,
            $"227 Entering Passive Mode ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{high},{low}).");
    }

    /// <summary>PORT の引数（h1,h2,h3,h4,p1,p2）を IP とポートに戻す。読めなければ null。</summary>
    public static (IPAddress Address, int Port)? ParsePort(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return null;

        string[] parts = argument.Split(',');
        if (parts.Length != 6) return null;

        Span<byte> octets = stackalloc byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]))
                return null;
        }

        var address = new IPAddress(octets[..4].ToArray());
        int port = (octets[4] << 8) | octets[5];
        return (address, port);
    }

    /// <summary>LIST の 1 行（UNIX 風）。クライアントが最も広く解釈できる形。</summary>
    public static string ListLine(FtpListEntry entry)
    {
        // 例: -rw-r--r-- 1 owner group      1234 Aug 15 09:30 name
        //     drwxr-xr-x 1 owner group         0 Aug 15  2025 dir
        char type = entry.IsDirectory ? 'd' : '-';
        string permissions = entry.IsDirectory ? "rwxr-xr-x" : "rw-r--r--";
        string size = entry.Size.ToString(CultureInfo.InvariantCulture).PadLeft(12);
        string when = entry.Modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"{type}{permissions} 1 owner group {size} {when} {entry.Name}";
    }

    /// <summary>MDTM/内部用の時刻表記（yyyyMMddHHmmss）。</summary>
    public static string Timestamp(DateTime value)
        => value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>257 応答のパス。二重引用符は "" にエスケープする。</summary>
    public static string QuotedPath(string path)
        => "\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
