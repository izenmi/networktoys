using System.Globalization;
using System.Text;

namespace NetworkToys.Core.Ftp;

/// <param name="Code">3 桁の応答コード。読めなければ 0。</param>
/// <param name="Text">本文（複数行なら改行で連結）。</param>
public readonly record struct FtpReply(int Code, string Text)
{
    /// <summary>1xx。まだ続きがある（転送の開始など）。</summary>
    public bool IsPreliminary => Code is >= 100 and < 200;

    /// <summary>2xx。うまくいった。</summary>
    public bool IsSuccess => Code is >= 200 and < 300;

    /// <summary>3xx。続きの入力を待っている（USER のあとの PASS など）。</summary>
    public bool NeedsMore => Code is >= 300 and < 400;

    /// <summary>4xx / 5xx。断られた。</summary>
    public bool IsFailure => Code >= 400 || Code == 0;
}

/// <summary>
/// FTP の応答を 1 行ずつ食べて組み立てる。
///
/// <b>応答は 1 行とは限らない。</b>RFC 959 の複数行応答は
/// <c>250-最初の行</c> … <c>250 最後の行</c> の形で、
/// <b>先頭のコードと同じ数字 + 半角空白</b>が来るまで続く。
/// 途中の行が数字で始まっていても、それは終わりではない。
///
/// ソケットを持たない純粋な状態機械なので、CI で固められる。
/// </summary>
public sealed class FtpReplyReader
{
    private readonly StringBuilder _text = new();
    private int _code;
    private bool _multiline;

    /// <summary>
    /// 1 行食べる。<b>応答が揃ったらそれを返す。</b>まだ続くなら null。
    /// </summary>
    public FtpReply? Feed(string? line)
    {
        string text = (line ?? "").TrimEnd('\r', '\n');

        if (!_multiline)
        {
            // 最初の行。「250 …」なら 1 行で終わり、「250-…」なら続きがある
            if (text.Length < 4 || !TryCode(text, out int code))
            {
                // コードが読めない行が最初に来ることはないが、
                // 来たら 1 行だけの応答として扱う（黙って詰まらせない）
                return new FtpReply(0, text);
            }

            _code = code;
            _text.Clear();
            _text.Append(text[4..]);

            if (text[3] != '-') return Take();

            _multiline = true;
            return null;
        }

        _text.Append('\n');

        // 終わりは「同じコード + 半角空白」で始まる行だけ
        if (text.Length >= 4 && text[3] == ' ' && TryCode(text, out int closing) && closing == _code)
        {
            _text.Append(text[4..]);
            return Take();
        }

        _text.Append(text);
        return null;
    }

    /// <summary>途中で切れたときのため。組みかけを捨てる。</summary>
    public void Reset()
    {
        _text.Clear();
        _code = 0;
        _multiline = false;
    }

    private FtpReply Take()
    {
        var reply = new FtpReply(_code, _text.ToString());

        Reset();

        return reply;
    }

    private static bool TryCode(string line, out int code)
        => int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out code);
}
