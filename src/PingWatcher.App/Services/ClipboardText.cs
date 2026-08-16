using System.Windows;

namespace PingWatcher.App.Services;

/// <summary>
/// クリップボードへ写す。
///
/// <b>他のプロセスが掴んでいると失敗する。</b>写せなかったからといって
/// 落ちる操作ではないので、記録して黙って諦める。
/// </summary>
internal static class ClipboardText
{
    public static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Clipboard.SetText");
        }
    }
}
