using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PastelNet.App.Views;

/// <summary>
/// Phase 0 時点の外枠。中身はまだ表示イメージのモックで、Phase 1 以降で
/// ViewModel バインディングに置き換える。ここで作り込む価値があるのは配色の確認だけ。
/// </summary>
public partial class MainWindow : Window
{
    private enum MockState { Ok, Slow, Down, Pending }

    private sealed record MockRow(
        MockState State,
        string Host,
        string Ip,
        string Rtt,
        string Avg,
        string Loss,
        double[] Samples,
        string Note);

    private static readonly MockRow[] MockRowData =
    [
        new(MockState.Ok, "gateway", "192.168.1.1", "1.2 ms", "1.4 ms", "0%",
            [2, 1, 2, 1, 1, 2, 1, 1, 1, 2, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1], "既定ゲートウェイ"),
        new(MockState.Ok, "dns.google", "8.8.8.8", "12.4 ms", "12.9 ms", "0%",
            [13, 12, 14, 12, 13, 12, 15, 13, 12, 13, 12, 14, 12, 13, 12, 12, 13, 14, 12, 12], "外部疎通の基準"),
        new(MockState.Slow, "file-srv01", "192.168.1.24", "148 ms", "96 ms", "4%",
            [40, 52, 38, 120, 45, 160, 48, 90, 150, 44, 180, 46, 110, 148, 42, 96, 155, 50, 130, 148], "夕方に遅くなる"),
        new(MockState.Down, "old-nas", "192.168.1.88", "—", "—", "100%",
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], "先週から応答なし"),
        new(MockState.Pending, "vpn.example.jp", "解決中…", "—", "—", "—",
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], "名前解決を待っています"),
    ];

    private static readonly (string Label, string BgKey, string FgKey)[] SwatchData =
    [
        ("背景",         "Brush.Background", "Brush.Text"),
        ("サーフェス",   "Brush.Surface",    "Brush.Text"),
        ("交互行",       "Brush.SurfaceAlt", "Brush.Text"),
        ("罫線",         "Brush.Border",     "Brush.Text"),
        ("文字(淡)",     "Brush.Surface",    "Brush.TextMuted"),
        ("ミント/応答",  "Brush.Ok.Bg",      "Brush.Ok.Fg"),
        ("ピーチ/遅延",  "Brush.Warn.Bg",    "Brush.Warn.Fg"),
        ("ローズ/不達",  "Brush.Error.Bg",   "Brush.Error.Fg"),
        ("スカイ/情報",  "Brush.Info.Bg",    "Brush.Info.Fg"),
        ("ラベンダー",   "Brush.Accent.Bg",  "Brush.Accent.Fg"),
    ];

    public MainWindow()
    {
        InitializeComponent();
        BuildMockRows();
        BuildSwatches();
        BuildStatusBar();
    }

    /// <summary>監視タブの一覧ヘッダと同じ列幅。ここを変えるときは XAML 側も合わせる。</summary>
    private static void AddColumns(Grid grid)
    {
        double[] widths = [86, 180, 130, 76, 76, 64];
        foreach (double w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
    }

    private void BuildMockRows()
    {
        var captionStyle = (Style)FindResource("Caption");
        var monoStyle = (Style)FindResource("Mono");
        var badgeStyle = (Style)FindResource("Badge");
        var badgeTextStyle = (Style)FindResource("Badge.Text");

        for (int i = 0; i < MockRowData.Length; i++)
        {
            MockRow row = MockRowData[i];

            var grid = new Grid { Height = 30 };
            AddColumns(grid);

            (string text, string bgKey, string fgKey) = row.State switch
            {
                MockState.Ok => ("● 応答", "Brush.Ok.Bg", "Brush.Ok.Fg"),
                MockState.Slow => ("▲ 遅延", "Brush.Warn.Bg", "Brush.Warn.Fg"),
                MockState.Down => ("✕ 不達", "Brush.Error.Bg", "Brush.Error.Fg"),
                _ => ("◌ 待機", "Brush.Info.Bg", "Brush.Info.Fg"),
            };

            var badge = new Border
            {
                Style = badgeStyle,
                Background = (Brush)FindResource(bgKey),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Style = badgeTextStyle,
                    Foreground = (Brush)FindResource(fgKey),
                    Text = text,
                },
            };
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            AddCell(grid, 1, row.Host, null, HorizontalAlignment.Left);
            AddCell(grid, 2, row.Ip, monoStyle, HorizontalAlignment.Left);
            AddCell(grid, 3, row.Rtt, monoStyle, HorizontalAlignment.Right);
            AddCell(grid, 4, row.Avg, monoStyle, HorizontalAlignment.Right);
            AddCell(grid, 5, row.Loss, monoStyle, HorizontalAlignment.Right);

            var spark = BuildSparkline(row.Samples, row.State);
            Grid.SetColumn(spark, 6);
            grid.Children.Add(spark);

            AddCell(grid, 7, row.Note, captionStyle, HorizontalAlignment.Left);

            // 交互行の塗り分け。1 行だけだと目が滑るので薄く敷く
            var wrapper = new Border
            {
                Background = i % 2 == 1 ? (Brush)FindResource("Brush.SurfaceAlt") : Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6, 0, 6, 0),
                Child = grid,
            };
            MockRows.Children.Add(wrapper);
        }
    }

    private void AddCell(Grid grid, int column, string text, Style? style, HorizontalAlignment align)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (style is not null) block.Style = style;
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    /// <summary>
    /// RTT の推移。Phase 1 では DrawingVisual に置き換えるが、
    /// モック 5 行なら Polyline で十分。
    /// </summary>
    private UIElement BuildSparkline(double[] samples, MockState state)
    {
        var container = new Grid { Margin = new Thickness(12, 6, 12, 6), MinHeight = 18 };

        if (state == MockState.Down || state == MockState.Pending)
        {
            container.Children.Add(new TextBlock
            {
                Text = "─────────────",
                Foreground = (Brush)FindResource("Brush.Border"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return container;
        }

        double max = Math.Max(samples.Max(), 1);
        var points = new PointCollection(samples.Length);
        for (int i = 0; i < samples.Length; i++)
            points.Add(new Point(i, max - samples[i]));   // Y を反転して上に伸ばす

        container.Children.Add(new Polyline
        {
            Points = points,
            Stroke = (Brush)FindResource("Brush.Chart.Line"),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Fill,
        });
        return container;
    }

    private void BuildSwatches()
    {
        foreach ((string label, string bgKey, string fgKey) in SwatchData)
        {
            var swatch = new Border
            {
                Background = (Brush)FindResource(bgKey),
                BorderBrush = (Brush)FindResource("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Height = 62,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = label,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = (Brush)FindResource(fgKey),
                        },
                        new TextBlock
                        {
                            Text = ((SolidColorBrush)FindResource(bgKey)).Color.ToString(),
                            FontSize = 11,
                            Foreground = (Brush)FindResource(fgKey),
                            Opacity = 0.85,
                        },
                    },
                },
            };
            SwatchGrid.Children.Add(swatch);
        }
    }

    private void BuildStatusBar()
    {
        var captionStyle = (Style)FindResource("Caption");
        (string, string)[] items =
        [
            ("SSID", "—"),
            ("IP", "—"),
            ("ゲートウェイ", "—"),
            ("DNS", "—"),
        ];

        bool first = true;
        foreach ((string label, string value) in items)
        {
            if (!first)
            {
                StatusBarPanel.Children.Add(new TextBlock
                {
                    Text = "／",
                    Style = captionStyle,
                    Margin = new Thickness(12, 0, 12, 0),
                });
            }
            first = false;

            StatusBarPanel.Children.Add(new TextBlock { Text = label + ": ", Style = captionStyle });
            StatusBarPanel.Children.Add(new TextBlock { Text = value });
        }

        StatusBarPanel.Children.Add(new TextBlock
        {
            Text = "（接続環境の表示は Phase 4 で実装）",
            Style = captionStyle,
            Margin = new Thickness(16, 0, 0, 0),
        });
    }
}
