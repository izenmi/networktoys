using System.Globalization;

namespace NetworkToys.Core.Net;

/// <summary>
/// Tera Term (ttermpro.exe) に渡す引数を組み立てる。
///
/// 実行ファイルを探して起動するのは App 側。ここは文字列を作るだけなので
/// そのまま検証できる（SSH と Telnet で書式が違い、間違えても静かに
/// 「接続先が空の Tera Term が開く」だけで気づきにくい）。
/// </summary>
public static class TeraTermCommand
{
    public const int DefaultSshPort = 22;
    public const int DefaultTelnetPort = 23;

    /// <summary>
    /// 例: <c>192.168.1.1:22 /ssh /2</c> / <c>192.168.1.1:23 /T=1</c>。
    /// </summary>
    /// <param name="host">接続先。空なら空文字を返す（起動しない）。</param>
    /// <param name="port">0 以下なら方式の既定ポートを使う。</param>
    /// <param name="ssh">true なら SSH2、false なら Telnet。</param>
    public static string Build(string host, int port, bool ssh)
    {
        string trimmed = host.Trim();
        if (trimmed.Length == 0) return "";

        // IPv6 は Tera Term の "host:port" 記法と衝突するので角括弧で囲む
        if (trimmed.Contains(':', StringComparison.Ordinal) && !trimmed.StartsWith('['))
            trimmed = "[" + trimmed + "]";

        int effective = port > 0 ? port : ssh ? DefaultSshPort : DefaultTelnetPort;
        string target = trimmed + ":" + effective.ToString(CultureInfo.InvariantCulture);

        // /2 は SSH2 固定。/T=1 は Telnet で開く指定（付けないと接続方法を聞かれる）
        return ssh ? target + " /ssh /2" : target + " /T=1";
    }

    /// <summary>
    /// よくある導入先。上から順に探す。
    /// バージョン 5 は teraterm5 に入るので、新しい方を先に見る。
    /// </summary>
    public static IReadOnlyList<string> WellKnownPaths(string programFiles, string programFilesX86) =>
    [
        Path.Combine(programFiles, "teraterm5", "ttermpro.exe"),
        Path.Combine(programFilesX86, "teraterm5", "ttermpro.exe"),
        Path.Combine(programFiles, "teraterm", "ttermpro.exe"),
        Path.Combine(programFilesX86, "teraterm", "ttermpro.exe"),
        Path.Combine(programFiles, "Tera Term", "ttermpro.exe"),
        Path.Combine(programFilesX86, "Tera Term", "ttermpro.exe"),
    ];
}
