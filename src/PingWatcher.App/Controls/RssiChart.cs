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

    public RssiChart()
    {
        // 色は OnRender のたびに引き直しているが、再描画のきっかけが RSSI の
        // 更新しか無いと、無線の取れない端末（有線のみ等）では配色を切り替えても
        // 旧テーマの色のまま止まる。切替そのものを再描画の合図にする
        Loaded += (_, _) => ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // 色が変わるので、作り置きした Pen とラベルは捨てて描き直す
        _gridPen = null;
        _linePen = null;
        _labels = null;
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _labels = null;   // FormattedText は DPI を焼き込むので作り直す
    }

    // 毎 OnRender で Pen と FormattedText を作り直さないための置き場。
    // リサイズ中は描画が連続するので、テキスト整形を毎回やると引っかかる
    private Pen? _gridPen;
    private Pen? _linePen;
    private (double Level, FormattedText Text)[]? _labels;

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

        if (_gridPen is null || _linePen is null)
        {
            Brush gridBrush = TryFindResource("Brush.Border") as Brush ?? Brushes.LightGray;
            Brush lineBrush = TryFindResource("Brush.Chart.Line") as Brush ?? Brushes.SteelBlue;

            _gridPen = new Pen(gridBrush, 1);
            _gridPen.Freeze();
            _linePen = new Pen(lineBrush, 1.6) { LineJoin = PenLineJoin.Round };
            _linePen.Freeze();
        }

        if (_labels is null)
        {
            Brush textBrush = TryFindResource("Brush.TextMuted") as Brush ?? Brushes.Gray;
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            _labels = [.. new double[] { -30, -60, -90 }.Select(level =>
                (level, new FormattedText(
                    level.ToString("0", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"),
                    10,
                    textBrush,
                    pixelsPerDip)))];
        }

        // 目盛り
        foreach ((double level, FormattedText label) in _labels)
        {
            double y = ToY(level, height);
            drawingContext.DrawLine(_gridPen, new Point(30, y), new Point(width, y));
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
        drawingContext.DrawGeometry(null, _linePen, geometry);
    }

    private static double ToY(double rssi, double height)
    {
        double ratio = (Math.Clamp(rssi, Worst, Best) - Worst) / (Best - Worst);
        return height - ratio * height;
    }
}
