namespace PingWatcher.Core.Ftp;

/// <summary>
/// FTP クライアントから見える仮想パスと、実ファイルパスの対応。
///
/// <b>ここが安全の要。</b>公開フォルダ 1 つに閉じ込め、<c>..</c> や絶対パス、
/// ドライブ文字でルート外へ出る要求は必ず弾く。UI にもネットワークにも
/// 触らない純粋ロジックなので、脱獄の試みをテストで網羅できる。
///
/// 仮想パスは常に <c>/</c> 区切りの絶対表記（クライアントへ返す PWD もこれ）。
/// </summary>
public sealed class FtpVirtualPath
{
    private readonly string _root;

    /// <param name="rootDirectory">公開する実フォルダ。この外は一切見せない。</param>
    public FtpVirtualPath(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);

        // 末尾の区切りを外して比較の基準をそろえる
        _root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootDirectory));
    }

    /// <summary>クライアントの現在位置（仮想パス）。ルート直下から始まる。</summary>
    public string CurrentDirectory { get; private set; } = "/";

    /// <summary>
    /// 引数（絶対 <c>/a/b</c> か相対 <c>../c</c>）を、現在位置を基点に解決する。
    /// ルート外へ出る場合は <see langword="null"/>。ディレクトリ移動には使わない
    /// （移動は <see cref="ChangeDirectory"/>）。
    /// </summary>
    public string? Resolve(string argument)
    {
        string? virtualPath = Normalize(CurrentDirectory, argument);
        if (virtualPath is null) return null;

        return ToLocal(virtualPath);
    }

    /// <summary>
    /// 現在位置を移す。成功したら true。ルート外や、仮想パスとしては正しいが
    /// 実体がフォルダでない場合は false（呼び出し側で 550 を返す）。
    /// </summary>
    public bool ChangeDirectory(string argument, out string? error)
    {
        error = null;

        string? virtualPath = Normalize(CurrentDirectory, argument);
        if (virtualPath is null)
        {
            error = "ルートより外へは移動できません。";
            return false;
        }

        if (!System.IO.Directory.Exists(ToLocal(virtualPath)))
        {
            error = "そのフォルダはありません。";
            return false;
        }

        CurrentDirectory = virtualPath;
        return true;
    }

    /// <summary>現在位置を親へ。ルートより上へは行かない（黙ってルートに留まる）。</summary>
    public void GoToParent()
    {
        string? parent = Normalize(CurrentDirectory, "..");
        if (parent is not null)
            CurrentDirectory = parent;
    }

    /// <summary>
    /// 仮想パスを正規化する。<c>.</c>/<c>..</c> を畳み、ルートより上へ出たら null。
    /// クライアントは <c>\</c> を送ってくることもあるので <c>/</c> と同一視する。
    /// </summary>
    private static string? Normalize(string current, string argument)
    {
        argument ??= string.Empty;

        // 絶対指定ならルートから、相対なら現在位置から
        string start = argument.StartsWith('/') || argument.StartsWith('\\') ? "/" : current;

        var stack = new List<string>();
        foreach (string part in $"{start}/{argument}".Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;

            if (part == "..")
            {
                if (stack.Count == 0)
                    return null;   // ルートより上へ出ようとした

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            // ドライブ文字やコロンを含む部品は拒否（C: など）
            if (part.Contains(':'))
                return null;

            stack.Add(part);
        }

        return "/" + string.Join('/', stack);
    }

    /// <summary>正規化済みの仮想パスを実パスへ。ルート内に収まることは保証済み。</summary>
    private string ToLocal(string virtualPath)
    {
        string relative = virtualPath.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar);
        return relative.Length == 0 ? _root : System.IO.Path.Combine(_root, relative);
    }
}
