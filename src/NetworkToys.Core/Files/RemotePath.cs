using System.Text;

namespace NetworkToys.Core.Files;

/// <summary>
/// 接続先のパスの扱い。<b>常に <c>/</c> 区切りの絶対パス</b>で持つ。
///
/// <b><see cref="Ftp.FtpVirtualPath"/> とは役目が違う。</b>あちらはサーバ側で
/// 「公開フォルダの外へ出さない」ための仕組みで、ローカルの実パスと
/// <c>Directory.Exists</c> に結び付いている。こちらは<b>相手側のパス</b>を
/// 組み立てるだけなので、ファイルシステムには一切触らない。
///
/// 閉じ込めもしない — 接続先のどこへでも行けるのがクライアントの役目で、
/// <c>/</c> より上は「そもそも存在しない」ので詰めるだけでよい。
/// </summary>
public static class RemotePath
{
    /// <summary>いちばん上。</summary>
    public const string Root = "/";

    /// <summary>
    /// いまの場所に名前を継ぎ足す。
    /// <paramref name="name"/> が <c>/</c> で始まっていれば、そちらを絶対パスとして使う。
    /// </summary>
    public static string Combine(string? current, string? name)
    {
        string tail = (name ?? "").Replace('\\', '/').Trim();

        if (tail.Length == 0) return Normalize(current);
        if (tail.StartsWith('/')) return Normalize(tail);

        return Normalize(Normalize(current).TrimEnd('/') + "/" + tail);
    }

    /// <summary>1 つ上へ。いちばん上ならそのまま。</summary>
    public static string Parent(string? path)
    {
        string here = Normalize(path);

        if (here == Root) return Root;

        int slash = here.LastIndexOf('/');

        return slash <= 0 ? Root : here[..slash];
    }

    /// <summary>いちばん後ろの名前。いちばん上なら空。</summary>
    public static string Name(string? path)
    {
        string here = Normalize(path);

        if (here == Root) return "";

        return here[(here.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// <c>.</c> と <c>..</c> を畳み、区切りを 1 つに詰めて絶対パスにする。
    /// <b><c>/</c> より上へは行かない</b>（詰めて捨てる）。
    /// </summary>
    public static string Normalize(string? path)
    {
        string text = (path ?? "").Replace('\\', '/').Trim();

        if (text.Length == 0) return Root;

        var parts = new List<string>();

        foreach (string part in text.Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    continue;

                case "..":
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    continue;

                default:
                    parts.Add(part);
                    continue;
            }
        }

        if (parts.Count == 0) return Root;

        var built = new StringBuilder(text.Length + 1);

        foreach (string part in parts)
            built.Append('/').Append(part);

        return built.ToString();
    }
}
