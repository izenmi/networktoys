namespace NetworkToys.Core.Files;

/// <summary>
/// 接続先の一覧の 1 行。<b>FTP と SFTP で同じ型を使う。</b>
///
/// 取り方は protocol ごとに違うが、画面に出すものは同じなので、
/// ここで揃えてから上へ渡す（画面が protocol を意識しなくて済む）。
/// </summary>
/// <param name="Name">ファイル / フォルダの名前。パスは含まない。</param>
/// <param name="IsDirectory">フォルダか。</param>
/// <param name="Size">バイト数。フォルダは 0。</param>
/// <param name="Modified">最終更新時刻。分からなければ <see cref="DateTime.MinValue"/>。</param>
public readonly record struct RemoteEntry(string Name, bool IsDirectory, long Size, DateTime Modified)
{
    /// <summary>親へ戻る行。一覧の先頭に置く。</summary>
    public static RemoteEntry Parent { get; } = new("..", IsDirectory: true, 0, DateTime.MinValue);

    /// <summary>自分自身か親を指す行か。一覧に出すときは自前で足すので、受け取った分は捨てる。</summary>
    public bool IsDots => Name is "." or "..";
}
