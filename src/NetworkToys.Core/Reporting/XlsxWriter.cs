using System.IO;
using System.IO.Compression;
using System.Text;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Reporting;

/// <summary>
/// 表を .xlsx で書き出す。<b>オートフィルタと見出し行の固定を付けた状態で開ける</b>のが
/// CSV との違い（CSV はただのテキストなので書式を持てない）。
///
/// <b>ライブラリを足していない。</b>xlsx は zip に XML を数枚入れただけのもので、
/// 必要なのは 5 パーツだけ。標準の <see cref="ZipArchive"/> で足りる。
/// 単一ファイル発行との相性を実機で確かめられないこの環境では、
/// 依存を増やさない方が安全という判断（EPPlus はライセンスの都合もある）。
///
/// <b>Excel で開いた見た目はこの環境では確かめられない。</b>xUnit で守れるのは
/// zip の中身と XML の形まで。書き換えるときは実機で 1 度開いて確認すること。
/// </summary>
public static class XlsxWriter
{
    /// <summary>
    /// シート名は固定。ブック内に 1 枚しか作らないので選ぶ意味が無く、
    /// 名前を可変にすると禁止文字(<c>: \ / ? * [ ]</c>)と 31 文字制限、
    /// さらに定義名の中での引用符付けまで面倒を見ることになる。
    /// </summary>
    private const string SheetName = "Sheet1";

    private const string Declaration = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""";

    /// <summary>
    /// 表を 1 冊のブックとして <paramref name="destination"/> へ書く。
    /// 見出しが無い表は表として成立しない（オートフィルタの範囲が決まらない）ので拒む。
    /// </summary>
    public static void Write(Stream destination, CsvTable table)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(table);

        if (table.Headers.Count == 0)
            throw new ArgumentException("見出しの無い表は xlsx にできない", nameof(table));

        // A1 から「見出し 1 行 + 中身」まで。オートフィルタも定義名もこの範囲を指す
        string lastColumn = ColumnName(table.Headers.Count - 1);
        int lastRow = table.Rows.Count + 1;

        string range = $"A1:{lastColumn}{lastRow}";
        string absoluteRange = $"$A$1:${lastColumn}${lastRow}";

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        AddEntry(zip, "[Content_Types].xml", ContentTypes());
        AddEntry(zip, "_rels/.rels", RootRelationships());
        AddEntry(zip, "xl/workbook.xml", Workbook(absoluteRange));
        AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
        AddEntry(zip, "xl/worksheets/sheet1.xml", Sheet(table, range));
    }

    private static void AddEntry(ZipArchive zip, string path, string xml)
    {
        using Stream entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.Write(xml);
    }

    private static string ContentTypes() =>
        $"""
        {Declaration}
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
        <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
        <Default Extension="xml" ContentType="application/xml"/>
        <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
        <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private static string RootRelationships() =>
        $"""
        {Declaration}
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    /// <summary>
    /// <c>_xlnm._FilterDatabase</c> は Excel 自身が保存時に書く定義名。
    /// シートの <c>autoFilter</c> だけでも今の Excel は絞り込みを出すが、
    /// 実機で確かめられない以上、Excel が書くのと同じ形に寄せておく。
    /// </summary>
    private static string Workbook(string absoluteRange) =>
        $"""
        {Declaration}
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheets><sheet name="{SheetName}" sheetId="1" r:id="rId1"/></sheets>
        <definedNames><definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">{SheetName}!{absoluteRange}</definedName></definedNames>
        </workbook>
        """;

    private static string WorkbookRelationships() =>
        $"""
        {Declaration}
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;

    /// <summary>
    /// シート本体。要素の順番は決まっており（dimension → sheetViews → sheetData → autoFilter）、
    /// 入れ替えると Excel が「読めない内容」と言って開かない。
    /// </summary>
    private static string Sheet(CsvTable table, string range)
    {
        var xml = new StringBuilder();

        xml.Append(Declaration).Append('\n');
        xml.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.Append($"""<dimension ref="{range}"/>""");

        // 見出し行を固定する。絞り込みながら下へ辿るとき、これが無いと何の列か見失う
        xml.Append("""<sheetViews><sheetView tabSelected="1" workbookViewId="0">""");
        xml.Append("""<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/>""");
        xml.Append("""<selection pane="bottomLeft" activeCell="A2" sqref="A2"/>""");
        xml.Append("</sheetView></sheetViews>");

        xml.Append("<sheetData>");
        AppendRow(xml, 1, table.Headers);
        for (int i = 0; i < table.Rows.Count; i++)
            AppendRow(xml, i + 2, table.Rows[i]);
        xml.Append("</sheetData>");

        xml.Append($"""<autoFilter ref="{range}"/>""");
        xml.Append("</worksheet>");

        return xml.ToString();
    }

    private static void AppendRow(StringBuilder xml, int rowNumber, IReadOnlyList<string> fields)
    {
        xml.Append($"""<row r="{rowNumber}">""");

        for (int i = 0; i < fields.Count; i++)
        {
            string cell = $"{ColumnName(i)}{rowNumber}";
            string value = fields[i];

            if (value.Length == 0)
            {
                // 空欄は器だけ置く。空の inlineStr を書くと絞り込みで「空白セル」に見えない
                xml.Append($"""<c r="{cell}"/>""");
            }
            else if (rowNumber > 1 && IsPlainNumber(value))
            {
                // 数値として入れておかないと、並べ替えも数値の絞り込みも文字列として振る舞う
                xml.Append($"""<c r="{cell}"><v>{value}</v></c>""");
            }
            else
            {
                // inlineStr は数式として評価されないので、CSV のような無害化は要らない
                xml.Append($"""<c r="{cell}" t="inlineStr"><is><t xml:space="preserve">{Escape(value)}</t></is></c>""");
            }
        }

        xml.Append("</row>");
    }

    /// <summary>
    /// 数値として入れてよい書き方か。<b>疑わしいものは文字列のままにする</b> —
    /// 数値に倒して困るのは元に戻せない側（<c>007</c> が <c>7</c>、
    /// 16 桁の製造番号が丸められる）なので、判定は狭く取る。
    /// IP や MAC は区切りが 2 つ以上あるのでここを通らない。
    /// </summary>
    internal static bool IsPlainNumber(string value)
    {
        // double が正確に持てる桁数まで。これを超える数字列は製造番号などの識別子とみなす
        if (value.Length is 0 or > 15) return false;

        int i = value[0] == '-' ? 1 : 0;
        if (i == value.Length) return false;

        // 先頭の 0 は「0」と「0.」以外は意味のある文字（VLAN 007、電話番号など）
        if (value[i] == '0' && i + 1 < value.Length && value[i + 1] != '.') return false;

        bool seenDot = false;
        bool seenDigit = false;

        for (; i < value.Length; i++)
        {
            char c = value[i];

            if (c == '.')
            {
                if (seenDot) return false;
                seenDot = true;
                continue;
            }

            if (c is < '0' or > '9') return false;
            seenDigit = true;
        }

        return seenDigit && value[^1] != '.';
    }

    /// <summary>
    /// XML に置ける文字だけにする。機器の出力には制御文字が混じることがあり、
    /// 1 文字でも通すと Excel はファイルごと開けなくなる（黙って壊れるのが最悪）。
    /// </summary>
    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length + 8);

        foreach (char c in value)
        {
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '\t' or '\n' or '\r': escaped.Append(c); break;
                default:
                    if (c >= ' ') escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>0 起点の列番号を A, B, … Z, AA の列名にする。</summary>
    internal static string ColumnName(int index)
    {
        var name = new StringBuilder(3);

        for (int i = index; ; i = i / 26 - 1)
        {
            name.Insert(0, (char)('A' + i % 26));
            if (i < 26) break;
        }

        return name.ToString();
    }
}
