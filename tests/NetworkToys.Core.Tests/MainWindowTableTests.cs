using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// 一覧の「見出しと行がズレる」を、XAML の文字として突き合わせて捕まえる検査。
///
/// 一覧は<b>見出しの Grid と行の DataTemplate で列を二重に書く</b>作りなので、
/// 片方だけ直すと列が丸ごとずれる。<b>見た目のずれは起動するだけの検査を素通りする</b>ため、
/// ここで機械的に見比べる。同じ指摘が何度も出ているので、規約そのものを検査にした
/// （2026-08-19: ACI のポートで行に列が 1 つ多く、可変幅より右が全部ずれていた）。
///
/// App は net10.0-windows で Core のテストからは参照できないので、
/// <c>MainWindow.xaml</c> をテストに埋め込んで読む。
/// </summary>
public class MainWindowTableTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Ping / TCP 一覧だけは古い <c>ColumnLayout</c> を使う（キーに表名が付かない）。</summary>
    private static readonly string[] PingColumns = ["State", "Target", "Rtt", "Loss", "Spark"];

    /// <summary>縦スクロールバーの幅（Controls.xaml で固定）。行はこのぶん狭くなる。</summary>
    private const int ScrollBarWidth = 14;

    [Fact]
    public void 見出しと行で列の定義がそろっている()
    {
        var problems = new List<string>();

        foreach ((string table, List<string> shapes) in ColumnShapes())
        {
            // 見出しと行で 2 箇所以上に出るはず。1 箇所なら片方が消えている
            if (shapes.Count < 2)
            {
                problems.Add($"{table}: 列の定義が {shapes.Count} 箇所しかない（見出しか行が欠けている）");
                continue;
            }

            string[] variants = [.. shapes.Distinct(StringComparer.Ordinal)];

            if (variants.Length > 1)
                problems.Add($"{table}:\n    " + string.Join("\n    ", variants));
        }

        Assert.True(problems.Count == 0, "見出しと行で列の定義が違う:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void 列を持つ一覧は行の余白とスクロールバーの決まりを守っている()
    {
        // 「行だけ 14px 狭くなる」「既定の ListBoxItem の余白で描かれる」は、
        // どちらも見出しとのズレとして報告された原因。
        bool styleReserves = Regex.IsMatch(
            Xaml(),
            "TargetListBox.*?ScrollViewer\\.VerticalScrollBarVisibility\" Value=\"Visible\"",
            RegexOptions.Singleline);

        var problems = new List<string>();

        foreach (XElement list in XDocument.Parse(Xaml()).Descendants(Presentation + "ListBox"))
        {
            if (list.Attribute("ItemsSource") is null) continue;

            bool sharedStyle = list.Attribute("Style")?.Value.Contains("TargetListBox", StringComparison.Ordinal) == true;

            // 列を持つ一覧だけが対象。差分の左右や札を並べただけの一覧は関係ない
            bool hasColumns = sharedStyle || list.Descendants(Presentation + "ColumnDefinition")
                .Any(c => c.Attribute("Width")?.Value.Contains("TableColumns.Instance", StringComparison.Ordinal) == true);

            if (!hasColumns) continue;

            string name = list.Attribute("Name")?.Value
                          ?? list.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                          ?? list.Attribute("ItemsSource")?.Value
                          ?? "(名前なし)";

            if (list.Attribute("ItemContainerStyle") is null && !sharedStyle)
                problems.Add($"{name}: 行コンテナ（ItemContainerStyle）を指定していない");

            bool reserves = list.Attribute(Presentation + "ScrollViewer.VerticalScrollBarVisibility")?.Value == "Visible"
                            || list.Attributes().Any(a => a.Name.LocalName == "ScrollViewer.VerticalScrollBarVisibility"
                                                          && a.Value == "Visible")
                            || (sharedStyle && styleReserves);

            if (!reserves)
                problems.Add($"{name}: 縦スクロールバーの場所を確保していない（出た瞬間に行だけ {ScrollBarWidth}px 狭くなる）");
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    [Fact]
    public void Ping_の見出しはスクロールバーのぶんを空けている()
    {
        // ほかの表は「4,0,18,3」（行の 4 ＋ バーの 14）。Ping はカードの中なので
        // 左が 11（枠 1 ＋ カード余白 6 ＋ 行余白 4）で、右はそこに 14 を足した 25。
        XElement header = XDocument.Parse(Xaml())
            .Descendants(Presentation + "Grid")
            .Single(g => g.Elements(Presentation + "Thumb").Any(t => t.Attribute("Tag")?.Value == "State"));

        Assert.Equal($"11,0,{11 + ScrollBarWidth},3", header.Attribute("Margin")?.Value);
    }

    [Fact]
    public void 進行の帯はすべて共通スタイルで描く()
    {
        // 高さや余白を直書きすると、画面ごとに帯の見た目がずれていく
        // （2026-08-20 の UI 改善で BusyBar に統一した）
        foreach (Match bar in Regex.Matches(Xaml(), @"<ProgressBar[^>]*>", RegexOptions.Singleline))
        {
            Assert.True(bar.Value.Contains("Style=\"{StaticResource BusyBar}\"", StringComparison.Ordinal),
                        $"BusyBar でない ProgressBar: {Regex.Replace(bar.Value, @"\s+", " ")}");
        }
    }

    /// <summary>表ごとに、見つかった列定義の「形」を並べる。</summary>
    private static IEnumerable<(string Table, List<string> Shapes)> ColumnShapes()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (XElement block in XDocument.Parse(Xaml()).Descendants(Presentation + "Grid.ColumnDefinitions"))
        {
            List<string> columns = [.. block.Elements(Presentation + "ColumnDefinition").Select(Shape)];

            if (TableOf(columns) is not { } table) continue;

            if (!found.TryGetValue(table, out List<string>? shapes))
                found[table] = shapes = [];

            shapes.Add(string.Join(" | ", columns));
        }

        return found.Select(pair => (pair.Key, pair.Value));
    }

    /// <summary>
    /// 列 1 つの形。<b>幅の出どころと下限だけ</b>を見る
    /// （見出し側の <c>Mode=TwoWay</c> のような、ずれに関係しない違いは落とす）。
    /// </summary>
    private static string Shape(XElement column)
    {
        string width = column.Attribute("Width")?.Value ?? "Auto";

        if (Regex.Match(width, @"Path=\[?([\w.]+)\]?") is { Success: true } path)
            width = path.Groups[1].Value;

        string? min = column.Attribute("MinWidth")?.Value;

        return min is null ? width : $"{width}+min{min}";
    }

    /// <summary>その Grid がどの表のものか。表の列を 1 つも持たないもの（画面の割り付け）は null。</summary>
    private static string? TableOf(IEnumerable<string> columns)
    {
        foreach (string column in columns)
        {
            string name = column.Split('+')[0];

            if (name.Contains('.', StringComparison.Ordinal)) return name.Split('.')[0];

            if (PingColumns.Contains(name)) return "ping/tcp";
        }

        return null;
    }

    private static string Xaml()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")
            ?? throw new InvalidOperationException("MainWindow.xaml を埋め込めていない");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
