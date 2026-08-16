using System.IO;
using System.Text;
using System.Windows;

namespace PingWatcher.App.Services;

/// <summary>
/// 放り込まれたファイルを文字にする。
///
/// 現場で扱うテキストは <b>UTF-8 とは限らない</b>。TeraTerm のログや機器から
/// 落とした設定は cp932（Shift_JIS）のことが多く、UTF-8 として読むと化ける。
/// 先に UTF-8 として厳密に読んでみて、通らなければ cp932 に落とす
/// （UTF-8 のバイト列は cp932 として妥当なことがあるが、その逆はまず無いので
/// この順で見るのが安全）。
/// </summary>
internal static class DroppedText
{
    /// <summary>これより大きいファイルは開かない。貼り付け欄に載せる前提の大きさ。</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>放り込まれたファイルの一覧。ファイルでなければ空。</summary>
    public static string[] FilesOf(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return e.Data.GetDataPresent(DataFormats.FileDrop)
               && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : [];
    }

    /// <summary>
    /// 読めたら中身、読めなければ null。
    /// <b>読めない理由は握りつぶさない</b>ので、呼ぶ側で画面に出すこと。
    /// </summary>
    public static string? TryRead(string path, out string problem)
    {
        problem = "";

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                problem = $"{Path.GetFileName(path)} が見つかりません。";
                return null;
            }

            if (info.Length > MaxBytes)
            {
                problem = $"{Path.GetFileName(path)} は大きすぎます（{info.Length / 1024 / 1024} MB）。";
                return null;
            }

            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or PathTooLongException)
        {
            problem = $"{Path.GetFileName(path)} を読めませんでした: {ex.Message}";
            return null;
        }
    }

    /// <summary>UTF-8 として通るならそれ。通らなければ cp932。</summary>
    internal static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            // throwOnInvalidBytes: これが無いと、化けたまま「読めた」ことになる
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Ansi().GetString(bytes);
        }
    }

    private static Encoding Ansi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // cp932 が引けない環境（Linux の CI など）では読めるところまでで妥協する
            return Encoding.Latin1;
        }
    }
}
