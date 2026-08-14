using System.IO;
using PastelNet.Core.Oui;

namespace PastelNet.App.Services;

/// <summary>
/// 埋め込みの OUI テーブルへの入り口。
///
/// <b>遅延ロードにするのが肝。</b>起動時に 34 万件を展開すると起動時間の要件に響く。
/// 初めて MAC を引いたときにだけ読み込む。
/// </summary>
internal static class OuiCatalog
{
    private const string ResourceName = "PastelNet.App.Resources.oui.tsv.gz";

    private static readonly Lazy<OuiLookup> Lookup = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static OuiLookup Current => Lookup.Value;

    public static string? FindVendor(string? mac) => Current.Find(mac);

    private static OuiLookup Load()
    {
        try
        {
            using Stream? stream = typeof(OuiCatalog).Assembly.GetManifestResourceStream(ResourceName);
            return stream is null ? OuiLookup.Empty : OuiLookup.Load(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            CrashLog.Write(ex, "OuiCatalog.Load");
            return OuiLookup.Empty;
        }
    }
}
