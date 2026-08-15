namespace PingWatcher.Core.Ftp;

/// <param name="Verb">動詞。常に大文字にそろえる（プロトコルは大小を区別しない）。</param>
/// <param name="Argument">引数。無ければ空文字。</param>
public readonly record struct FtpCommand(string Verb, string Argument)
{
    /// <summary>
    /// 制御接続の 1 行を動詞と引数に分ける。例外は投げない（壊れた行は空の動詞で返す）。
    ///
    /// FTP は「動詞 空白 引数」。引数に空白を含むファイル名もあるので、
    /// 最初の空白 1 つだけで割る。末尾の CR/LF や Telnet 由来の空白は落とす。
    /// </summary>
    public static FtpCommand Parse(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return new FtpCommand(string.Empty, string.Empty);

        string trimmed = line.Trim('\r', '\n', ' ', '\t');
        if (trimmed.Length == 0)
            return new FtpCommand(string.Empty, string.Empty);

        int space = trimmed.IndexOf(' ');

        string verb = space < 0 ? trimmed : trimmed[..space];
        string argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        return new FtpCommand(verb.ToUpperInvariant(), argument);
    }
}
