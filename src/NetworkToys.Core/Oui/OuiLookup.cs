using System.IO.Compression;

namespace NetworkToys.Core.Oui;

/// <summary>
/// MAC アドレスの先頭 3 バイト（OUI）からベンダー名を引く。
///
/// IEEE の MA-L 登録簿は約 3.8MB あるので、必要な 2 列だけを抜いて gzip したものを
/// 埋め込みリソースとして持つ（約 340KB）。更新は tools/oui/build_oui.py を手で実行する。
/// CI では IEEE を叩かない — ビルドをネットワークに依存させないため。
/// </summary>
public sealed class OuiLookup
{
    private readonly Dictionary<string, string> _map;

    private OuiLookup(Dictionary<string, string> map) => _map = map;

    public static OuiLookup Empty { get; } = new([]);

    public int Count => _map.Count;

    /// <summary>gzip 圧縮された「OUI(16進6桁)\tベンダー名」の行から読み込む。</summary>
    public static OuiLookup Load(Stream gzipped)
    {
        ArgumentNullException.ThrowIfNull(gzipped);

        var map = new Dictionary<string, string>(40_000, StringComparer.Ordinal);

        // 引数で受け取ったストリームまで閉じない（所有権は呼び出し元にある）
        using var decompressed = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new StreamReader(decompressed);

        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab != 6) continue;

            map[line[..6]] = line[(tab + 1)..];
        }

        return new OuiLookup(map);
    }

    /// <summary>
    /// ベンダー名を引く。区切り文字は問わない（AA-BB-CC / aa:bb:cc / AABBCC のいずれも可）。
    /// 未登録なら null。
    /// </summary>
    public string? Find(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;

        Span<char> prefix = stackalloc char[6];
        int written = 0;

        foreach (char c in mac)
        {
            if (c is '-' or ':' or '.' or ' ') continue;

            if (!char.IsAsciiHexDigit(c)) return null;

            prefix[written++] = char.ToUpperInvariant(c);
            if (written == 6) break;
        }

        if (written < 6) return null;

        return _map.GetValueOrDefault(new string(prefix));
    }
}
