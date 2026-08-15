using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PingWatcher.App.ViewModels;

namespace PingWatcher.App.Controls;

/// <summary>
/// 接続中 AP の受信強度の推移。
///
/// 電波の弱い場所を歩いて特定する用途なので、絶対値が読めることが大事。
/// 目盛りを -30 / -60 / -90 dBm に固定して、揺れの大きさを見誤らないようにする。
/// </summary>
public sealed class RssiChart : FrameworkElement
{
    private const double Best = -30;
    private const double Worst = -90;

    [ThreadStatic]
    private static double[]? _scratch;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(WifiViewModel),
        typeof(RssiChart),
        new PropertyMetadata(null, OnSourceChanged));

    public WifiViewModel? Source
    {
        get => (WifiViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (RssiChart)d;

        if (e.OldValue is WifiViewModel previous)
            previous.RssiHistoryChanged -= chart.OnHistoryChanged;

        if (e.NewValue is WifiViewModel current)
            current.RssiHistoryChanged += chart.OnHistoryChanged;

        chart.InvalidateVisual();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        if (Source is null || width <= 1 || height <= 1)
            return;

        Brush gridBrush = TryFindResource("Brush.Border") as Brush ?? Brushes.LightGray;
        Brush textBrush = TryFindResource("Brush.TextMuted") as Brush ?? Brushes.Gray;
        Brush lineBrush = TryFindResource("Brush.Chart.Line") as Brush ?? Brushes.SteelBlue;

        var gridPen = new Pen(gridBrush, 1);
        gridPen.Freeze();
        var linePen = new Pen(lineBrush, 1.6) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();

        // 目盛り
        foreach (double level in (double[])[-30, -60, -90])
        {
            double y = ToY(level, height);
            drawingContext.DrawLine(gridPen, new Point(30, y), new Point(width, y));

            var label = new FormattedText(
                $"{level:0}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                10,
                textBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            drawingContext.DrawText(label, new Point(4, y - label.Height / 2));
        }

        double[] buffer = _scratch ??= new double[120];
        int count = Source.CopyRssiHistory(buffer);
        if (count < 2) return;

        double plotLeft = 32;
        double plotWidth = Math.Max(1, width - plotLeft);
        double step = plotWidth / (count - 1);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(plotLeft, ToY(buffer[0], height)), isFilled: false, isClosed: false);

            for (int i = 1; i < count; i++)
                context.LineTo(new Point(plotLeft + i * step, ToY(buffer[i], height)), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, linePen, geometry);
    }

    private static double ToY(double rssi, double height)
    {
        double ratio = (Math.Clamp(rssi, Worst, Best) - Worst) / (Best - Worst);
        return height - ratio * height;
    }
}
