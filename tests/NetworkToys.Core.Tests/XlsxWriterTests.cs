using System.IO;
using System.IO.Compression;
using System.Text;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// xlsx は zip + XML なので、生成物は開いて中身を確かめられる。
/// <b>Excel で開いた見た目までは確かめられない</b>（この環境にも CI にも Excel は無い）。
/// ここで守れるのは「パーツが揃っている」「絞り込みが範囲どおりに入っている」
/// 「値が壊れずに入っている」の 3 点まで。
/// </summary>
public class XlsxWriterTests
{
    private static readonly CsvTable Sample = new(
        ["Port", "Vlan", "Status"],
        [
            ["Gi0/1", "10", "connected"],
            ["Gi0/2", "007", "notconnect"],
        ]);

    private static Dictionary<string, string> Parts(CsvTable table)
    {
        using var buffer = new MemoryStream();
        XlsxWriter.Write(buffer, table);

        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var parts = new Dictionary<string, string>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            parts[entry.FullName] = reader.ReadToEnd();
        }

        return parts;
    }

    private static string Sheet(CsvTable table) => Parts(table)["xl/worksheets/sheet1.xml"];

    [Fact]
    public void Workbook_has_the_five_parts_excel_needs()
    {
        string[] expected =
        [
            "[Content_Types].xml",
            "_rels/.rels",
            "xl/_rels/workbook.xml.rels",
            "xl/workbook.xml",
            "xl/worksheets/sheet1.xml",
        ];

        // 並べ替えは序数で。既定の比較は文化圏によって '[' と '_' の前後が入れ替わる
        Assert.Equal(expected, Parts(Sample).Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Filter_covers_the_header_and_every_row()
    {
        Dictionary<string, string> parts = Parts(Sample);

        Assert.Contains("""<autoFilter ref="A1:C3"/>""", parts["xl/worksheets/sheet1.xml"], StringComparison.Ordinal);
        Assert.Contains("""<dimension ref="A1:C3"/>""", parts["xl/worksheets/sheet1.xml"], StringComparison.Ordinal);

        // Excel 自身が保存時に書く定義名も同じ範囲で置く
        Assert.Contains("Sheet1!$A$1:$C$3", parts["xl/workbook.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void Header_row_is_frozen_and_elements_stay_in_schema_order()
    {
        string sheet = Sheet(Sample);

        Assert.Contains("""<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/>""",
                        sheet, StringComparison.Ordinal);

        // 順番を崩すと Excel は「読めない内容」と言って開かない
        Assert.True(sheet.IndexOf("<sheetViews", StringComparison.Ordinal)
                    < sheet.IndexOf("<sheetData>", StringComparison.Ordinal));
        Assert.True(sheet.IndexOf("</sheetData>", StringComparison.Ordinal)
                    < sheet.IndexOf("<autoFilter", StringComparison.Ordinal));
    }

    [Fact]
    public void Numeric_looking_fields_become_numbers_but_identifiers_do_not()
    {
        string sheet = Sheet(Sample);

        // Vlan 10 は数値。でないと並べ替えも数値の絞り込みも文字列として振る舞う
        Assert.Contains("""<c r="B2"><v>10</v></c>""", sheet, StringComparison.Ordinal);

        // 007 を数値にすると 7 になって戻せない
        Assert.Contains("""<c r="B3" t="inlineStr"><is><t xml:space="preserve">007</t></is></c>""",
                        sheet, StringComparison.Ordinal);

        // 見出しは数字に見えても文字列のまま
        Assert.Contains("""<c r="A1" t="inlineStr">""", sheet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10", true)]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("0.5", true)]
    [InlineData("1.5", true)]
    [InlineData("007", false)]              // 先頭の 0 は意味がある
    [InlineData("10.0.0.1", false)]         // IP
    [InlineData("0011.2233.4455", false)]   // MAC
    [InlineData("Gi0/1", false)]
    [InlineData("1234567890123456", false)] // 16 桁。double では丸まる
    [InlineData("1e5", false)]
    [InlineData("5.", false)]
    [InlineData("-", false)]
    [InlineData("", false)]
    public void Only_plain_decimal_notation_counts_as_a_number(string value, bool expected)
        => Assert.Equal(expected, XlsxWriter.IsPlainNumber(value));

    [Fact]
    public void Markup_is_escaped_and_control_characters_are_dropped()
    {
        var table = new CsvTable(["Note"], [["a<b>c&d\"e"], ["制御\u0001文字"]]);
        string sheet = Sheet(table);

        Assert.Contains("a&lt;b&gt;c&amp;d\"e", sheet, StringComparison.Ordinal);

        // 1 文字でも通すと Excel はファイルごと開けなくなる
        Assert.Contains("制御文字", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_field_becomes_an_empty_cell()
    {
        string sheet = Sheet(new CsvTable(["A", "B"], [["", "x"]]));

        Assert.Contains("""<c r="A2"/>""", sheet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void Column_names_carry_over_after_twenty_six(int index, string expected)
        => Assert.Equal(expected, XlsxWriter.ColumnName(index));

    [Fact]
    public void Header_only_table_still_writes_a_filter()
        => Assert.Contains("""<autoFilter ref="A1:A1"/>""", Sheet(new CsvTable(["A"], [])), StringComparison.Ordinal);

    [Fact]
    public void Table_without_headers_is_rejected()
        => Assert.Throws<ArgumentException>(() => XlsxWriter.Write(new MemoryStream(), new CsvTable([], [])));
}
