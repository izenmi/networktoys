using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetworkToys.App.Controls;

/// <summary>
/// 時系列の折れ線。<b>目盛りは 0 と上限で固定する</b> — 揺れの大きさを見誤らないため
/// （RSSI のグラフと同じ考え方。あちらは Wi-Fi の VM に結び付いていて使い回せない）。
///
/// 値の並びと上限・単位だけを受け取る。時刻の目盛りは持たない
/// （期間は画面の側に出ているので、ここに入れても読むものが増えるだけ）。
/// </summary>
public sealed class SeriesChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(SeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(SeriesChart),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit),
        typeof(string),
        typeof(SeriesChart),
        new FrameworkPropertyMetadata("%", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>縦軸の上限。0 以下なら値の最大から決める。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>目盛りに添える単位。</summary>
    public string? Unit
    {
        get => (string?)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public SeriesChart()
    {
        // 配色の切り替えも描き直しの合図にする（RSSI のグラフで踏んだのと同じ）
        Loaded += (_, _) => ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= 4 || height <= 4) return;

        var grid = new Pen(Brush("Brush.Row.Line", Colors.Gainsboro), 1);
        var line = new Pen(Brush("Brush.Chart.Line", Colors.SteelBlue), 1.6);

        grid.Freeze();
        line.Freeze();

        IReadOnlyList<double> values = Values ?? [];
        double top = Maximum > 0 ? Maximum : (values.Count > 0 ? Math.Max(values.Max(), 1) : 1);

        // 目盛りは 0 / 半分 / 上限の 3 本だけ。増やしても読み取りやすくならない
        foreach (double level in (double[])[0, top / 2, top])
        {
            double y = height - (level / top * (height - 14)) - 12;

            drawingContext.DrawLine(grid, new Point(34, y), new Point(width, y));
            drawingContext.DrawText(Label(level, top), new Point(0, y - 7));
        }

        if (values.Count < 2) return;

        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                double x = 34 + (width - 36) * i / (values.Count - 1);
                double y = height - (Math.Clamp(values[i], 0, top) / top * (height - 14)) - 12;

                if (i == 0) context.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                else context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, line, geometry);
    }

    private FormattedText Label(double level, double top)
    {
        string text = top >= 100
            ? level.ToString("0", CultureInfo.InvariantCulture)
            : level.ToString("0.#", CultureInfo.InvariantCulture);

        return new FormattedText(
            level >= top ? text + (Unit ?? "") : text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            9,
            Brush("Brush.TextMuted", Colors.Gray),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
