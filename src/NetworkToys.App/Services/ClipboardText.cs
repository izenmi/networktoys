using System.Windows;

namespace NetworkToys.App.Services;

/// <summary>
/// クリップボードへ写す。
///
/// <b>他のプロセスが掴んでいると失敗する。</b>クリップボードは OS に 1 つしかなく、
/// 開いている間は誰も触れない（見張りの常駐ソフトが定期的に開くので珍しくない）。
/// <b>数十ミリ秒おいて数回試す</b> — 1 度の失敗で諦めると「押しても写らない」になる
/// （2026-08-18 に報告された）。それでも駄目なら記録して黙って諦める。
/// </summary>
internal static class ClipboardText
{
    public static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        Exception? last = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // SetText と違い、掴まれていても例外の代わりに false を返す版がある。
                // copy:true は終了後もクリップボードに残す指定
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(30);
            }
        }

        if (last is not null) CrashLog.Write(last, "Clipboard.SetDataObject");
    }
}
