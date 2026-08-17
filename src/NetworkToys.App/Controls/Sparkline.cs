using System.Windows;
using System.Windows.Media;
using NetworkToys.App.ViewModels;
using NetworkToys.Core.Models;

namespace NetworkToys.App.Controls;

/// <summary>
/// RTT の推移を描く小さな折れ線。
///
/// 数百行に並ぶので、Polyline を要素として積むのではなく <see cref="DrawingContext"/> へ
/// 直接描く。ビジュアルツリーに増えるのはこの要素 1 つだけになる。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    /// <summary>描画用の一時バッファ。描画は UI スレッドのみなのでスレッド単位で使い回す。</summary>
    [ThreadStatic]
    private static ProbeSample[]? _scratch;

    /// <summary>
    /// 線の Pen。<see cref="LineBrush"/> が変わるたびに作り直す。
    ///
    /// 以前はここを static にしていたが、それだと配色を切り替えても
    /// 最初に引いた色を握ったままになる。インスタンスごとに持つこと。
    /// </summary>
    private Pen? _linePen;

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(
            Brushes.SteelBlue,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnLineBrushChanged));

    public static readonly DependencyProperty LostBrushProperty = DependencyProperty.Register(
        nameof(LostBrush),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 折れ線の色。テンプレート側から DynamicResource で与えること。
    /// そうすれば配色の切り替えにそのまま追随する。
    /// </summary>
    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>応答が無かった位置に立てる印の色。</summary>
    public Brush LostBrush
    {
        get => (Brush)GetValue(LostBrushProperty);
        set => SetValue(LostBrushProperty, value);
    }

    private static void OnLineBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Sparkline)d)._linePen = null;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(TargetRowViewModel),
        typeof(Sparkline),
        new PropertyMetadata(null, OnSourceChanged));

    public TargetRowViewModel? Source
    {
        get => (TargetRowViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;

        if (e.OldValue is TargetRowViewModel previous)
            previous.HistoryChanged -= sparkline.OnHistoryChanged;

        if (e.NewValue is TargetRowViewModel current)
            current.HistoryChanged += sparkline.OnHistoryChanged;

        sparkline.InvalidateVisual();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        if (Source is null || width <= 1 || height <= 1)
            return;

        // 横 1 ピクセルに 1 点より細かく描いても見えない。表示幅ぶんだけ取り出す。
        int wanted = Math.Max(2, Math.Min(300, (int)width));
        ProbeSample[] buffer = _scratch ??= new ProbeSample[300];
        int count = Source.CopyHistory(buffer.AsSpan(0, Math.Min(wanted, buffer.Length)));

        if (count == 0)
            return;

        double scale = 1;
        for (int i = 0; i < count; i++)
        {
            if (buffer[i].Status.IsReachable() && buffer[i].RttMs > scale)
                scale = buffer[i].RttMs;
        }

        // 数 ms しか出ない相手で拡大しすぎると、ただの揺らぎが山脈に見える
        scale = Math.Max(scale, 10);

        double step = count > 1 ? width / (count - 1) : width;
        Pen pen = LinePen();
        Brush lost = LostBrush;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            bool drawing = false;

            for (int i = 0; i < count; i++)
            {
                double x = i * step;

                if (!buffer[i].Status.IsReachable())
                {
                    // 応答が無かった位置は線を切り、下端に印を残す
                    drawing = false;
                    drawingContext.DrawRectangle(lost, null, new Rect(Math.Max(0, x - 0.75), height - 3, 1.5, 3));
                    continue;
                }

                double y = height - (buffer[i].RttMs / scale * height);
                var point = new Point(x, Math.Clamp(y, 0, height));

                if (drawing)
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }
                else
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    drawing = true;
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private Pen LinePen()
    {
        if (_linePen is not null) return _linePen;

        var pen = new Pen(LineBrush, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return _linePen = pen;
    }
}
