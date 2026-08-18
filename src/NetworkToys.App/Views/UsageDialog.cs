using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using NetworkToys.Core.Work;

namespace NetworkToys.App.Views;

/// <summary>
/// 使い方を読ませる小窓。
///
/// <b>Markdown をそのまま出さず、組んで見せる</b>（2026-08-18 ユーザー指示）。
/// <c>#</c> や <c>|</c> が並んだ生の文字は、画面で読むものとしては辛い。
///
/// <b>ライブラリを足さない。</b>解釈は Core の <see cref="MarkdownDocument"/>（CI で固めてある）で、
/// ここは <see cref="FlowDocument"/> に組み替えるだけ。作りはほかの小窓に合わせ、
/// XAML を持たず配色は <c>SetResourceReference</c> で当てる。
/// </summary>
internal sealed class UsageDialog : Window
{
    private UsageDialog(string title, string markdown)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // 文字の大きさに合わせて窓も大きくする（中身だけ大きくすると読める量が減る）
        Width = 860 * UiScale.Current;
        Height = 620 * UiScale.Current;
        MinWidth = 480 * UiScale.Current;
        MinHeight = 320 * UiScale.Current;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        var viewer = new FlowDocumentScrollViewer
        {
            Document = Build(markdown),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsToolBarVisible = false,
            Padding = new Thickness(0),
        };

        var close = new Button { Content = "閉じる", MinWidth = 96, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(close);

        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(viewer);

        Content = panel;
    }

    public static void Show(Window owner, string title, string markdown)
        => new UsageDialog(title, markdown) { Owner = owner }.ShowDialog();

    /// <summary>
    /// 窓を出さずに組んでみるだけ。自己診断が<b>組み立てで落ちないこと</b>を見るための口
    /// （FlowDocument の作りは XAML と同じで、コンパイルを通り抜けて実行時に落ちる）。
    /// </summary>
    internal static int Preview(string markdown) => Build(markdown).Blocks.Count;

    /// <summary>読み込んだ Markdown を組む。<b>色と書体は必ず資源から引く</b>（配色の切替に追随する）。</summary>
    private static FlowDocument Build(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(4, 0, 12, 0),
            TextAlignment = TextAlignment.Left,
            IsOptimalParagraphEnabled = false,
        };

        document.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        document.SetResourceReference(TextElement.FontSizeProperty, "Size.Body");
        document.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        foreach (MarkdownBlock block in MarkdownDocument.Parse(markdown))
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    document.Blocks.Add(Heading(heading));
                    break;

                case MarkdownParagraph paragraph:
                    document.Blocks.Add(Body(paragraph.Text));
                    break;

                case MarkdownList list:
                    document.Blocks.Add(Bullets(list));
                    break;

                case MarkdownTable table:
                    document.Blocks.Add(Grid(table));
                    break;

                case MarkdownCode code:
                    document.Blocks.Add(Code(code.Text));
                    break;

                case MarkdownQuote quote:
                    document.Blocks.Add(Quote(quote.Text));
                    break;

                case MarkdownRule:
                    document.Blocks.Add(Rule());
                    break;
            }
        }

        return document;
    }

    private static Paragraph Heading(MarkdownHeading heading)
    {
        // 見出しは 3 段まで見た目を変える。それより下は太字だけ
        double scale = heading.Level switch { 1 => 1.6, 2 => 1.3, 3 => 1.12, _ => 1.0 };

        var paragraph = new Paragraph
        {
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, heading.Level <= 2 ? 16 : 12, 0, 5),
        };

        foreach (Inline inline in Inlines(heading.Text)) paragraph.Inlines.Add(inline);

        Scale(paragraph, scale);

        if (heading.Level <= 2)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.Border");
            paragraph.Padding = new Thickness(0, 0, 0, 3);
        }

        return paragraph;
    }

    private static Paragraph Body(IReadOnlyList<MarkdownInline> text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 };

        foreach (Inline inline in Inlines(text)) paragraph.Inlines.Add(inline);

        return paragraph;
    }

    private static List Bullets(MarkdownList list)
    {
        var result = new List
        {
            MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 0, 0, 8),
            Padding = new Thickness(0),
        };

        foreach (IReadOnlyList<MarkdownInline> item in list.Items)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2), LineHeight = 20 };

            foreach (Inline inline in Inlines(item)) paragraph.Inlines.Add(inline);

            result.ListItems.Add(new ListItem(paragraph));
        }

        return result;
    }

    private static Table Grid(MarkdownTable table)
    {
        var result = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };

        int columns = Math.Max(table.Header.Count, table.Rows.Count > 0 ? table.Rows.Max(r => r.Count) : 0);

        for (int i = 0; i < columns; i++)
            result.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        result.RowGroups.Add(group);

        if (table.Header.Count > 0) group.Rows.Add(Row(table.Header, columns, header: true));

        foreach (IReadOnlyList<MarkdownCell> row in table.Rows)
            group.Rows.Add(Row(row, columns, header: false));

        return result;
    }

    private static TableRow Row(IReadOnlyList<MarkdownCell> cells, int columns, bool header)
    {
        var row = new TableRow();

        for (int i = 0; i < columns; i++)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };

            if (i < cells.Count)
            {
                foreach (Inline inline in Inlines(cells[i].Text)) paragraph.Inlines.Add(inline);
            }

            var cell = new TableCell(paragraph)
            {
                Padding = new Thickness(7, 4, 7, 4),
                BorderThickness = new Thickness(0, 0, 1, 1),
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
            };

            cell.SetResourceReference(TableCell.BorderBrushProperty, "Brush.Border");

            if (header) cell.SetResourceReference(TableCell.BackgroundProperty, "Brush.SurfaceAlt");

            Scale(paragraph, 0.92);

            row.Cells.Add(cell);
        }

        return row;
    }

    private static Section Code(string text)
    {
        var paragraph = new Paragraph(new Run(text)) { Margin = new Thickness(0) };

        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        Scale(paragraph, 0.92);

        var section = new Section(paragraph)
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(9, 7, 9, 7),
            BorderThickness = new Thickness(1),
        };

        section.SetResourceReference(Block.BackgroundProperty, "Brush.SurfaceAlt");
        section.SetResourceReference(Block.BorderBrushProperty, "Brush.Border");

        return section;
    }

    /// <summary>引用。左に縦線を引いて、本文と見分けが付くようにする。</summary>
    private static Section Quote(IReadOnlyList<MarkdownInline> text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 21 };

        foreach (Inline inline in Inlines(text)) paragraph.Inlines.Add(inline);

        var section = new Section(paragraph)
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10, 7, 10, 7),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };

        section.SetResourceReference(Block.BackgroundProperty, "Brush.SurfaceAlt");
        section.SetResourceReference(Block.BorderBrushProperty, "Brush.Accent.Bg");

        return section;
    }

    private static BlockUIContainer Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 10) };

        line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");

        return new BlockUIContainer(line);
    }

    private static IEnumerable<Inline> Inlines(IReadOnlyList<MarkdownInline> parts)
    {
        foreach (MarkdownInline part in parts)
        {
            var run = new Run(part.Text);

            if (part.Bold) run.FontWeight = FontWeights.Bold;

            if (part.Code)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                run.SetResourceReference(TextElement.BackgroundProperty, "Brush.SurfaceAlt");
            }

            yield return run;
        }
    }

    /// <summary>
    /// 本文の大きさを基準に倍率を掛ける。
    /// <b>数で書かない</b> — 文字サイズは 4 段階から選べるので、選び直しても比が保たれるようにする。
    /// </summary>
    private static void Scale(Block block, double scale)
    {
        double body = Application.Current?.TryFindResource("Size.Body") is double size ? size : 13;

        block.FontSize = Math.Round(body * scale);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // タイトルバーの明暗を本体に揃える（ほかの小窓と同じ）
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }
}
