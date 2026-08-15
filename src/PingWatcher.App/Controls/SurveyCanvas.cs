using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PingWatcher.App.ViewModels;
using PingWatcher.Core.Survey;

namespace PingWatcher.App.Controls;

/// <summary>
/// サーベイの描画面。フロア図（または方眼）→ ヒートマップ → 測定点の順に
/// <see cref="DrawingContext"/> へ直接描く。左クリックで測定、右クリックで点の削除。
///
/// ヒートマップはグリッド解像度の <see cref="WriteableBitmap"/> に焼いてキャッシュし、
/// 表示矩形へ拡大して描く（WPF の拡大補間が無料の平滑化になる。セルごとに
/// DrawRectangle すると 1 万超の描画命令になるので不可）。
/// </summary>
public sealed class SurveyCanvas : FrameworkElement
{
    /// <summary>右クリック削除の許容距離（表示ピクセル）。</summary>
    private const double RemoveTolerancePixels = 12;

    private BitmapImage? _floorImage;
    private string? _floorImagePath;
    private WriteableBitmap? _heatBitmap;
    private bool _heatDirty = true;

    // 配色切替で引き直すブラシキャッシュ。static にしないこと（切替に追随しなくなる）
    private Brush?[] _bandBrushes = new Brush?[Heatmap.BandCount];
    private Brush? _surfaceBrush;
    private Brush? _textBrush;
    private Brush? _accentBrush;
    private Pen? _gridPen;
    private Pen? _pendingPen;
    private double _pixelsPerDip = 1;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(WifiSurveyViewModel),
        typeof(SurveyCanvas),
        new PropertyMetadata(null, OnSourceChanged));

    public WifiSurveyViewModel? Source
    {
        get => (WifiSurveyViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public SurveyCanvas()
    {
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (SurveyCanvas)d;

        if (e.OldValue is WifiSurveyViewModel previous)
            previous.SurveyChanged -= canvas.OnSurveyChanged;

        if (e.NewValue is WifiSurveyViewModel current)
            current.SurveyChanged += canvas.OnSurveyChanged;

        canvas._heatDirty = true;
        canvas.InvalidateVisual();
    }

    private void OnSurveyChanged(object? sender, EventArgs e)
    {
        _heatDirty = true;
        InvalidateVisual();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _bandBrushes = new Brush?[Heatmap.BandCount];
        _surfaceBrush = null;
        _textBrush = null;
        _accentBrush = null;
        _gridPen = null;
        _pendingPen = null;
        _heatDirty = true;   // バンド色が変わるのでビットマップも焼き直す
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _pixelsPerDip = newDpi.PixelsPerDip;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (Source is not { } source || ToNormalized(e.GetPosition(this)) is not { } position)
            return;

        _ = source.AddPointAtAsync(position.X, position.Y);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        if (Source is not { } source || ToNormalized(e.GetPosition(this)) is not { } position)
            return;

        (double x, double y, double w, double h) = FitRect();
        if (w <= 0)
            return;

        source.RemovePointNear(position.X, position.Y, RemoveTolerancePixels / Math.Min(w, h));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        if (Source is not { } source || width <= 1 || height <= 1)
            return;

        // クリックを面全体で受けるため、透明でも背景を敷く
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        (double fx, double fy, double fw, double fh) = FitRect();
        if (fw <= 0)
            return;

        var area = new Rect(fx, fy, fw, fh);

        DrawBackdrop(drawingContext, source, area);
        DrawHeat(drawingContext, source, area);
        DrawPoints(drawingContext, source, area);
    }

    private void DrawBackdrop(DrawingContext drawingContext, WifiSurveyViewModel source, Rect area)
    {
        if (source.FloorImagePath is { } path)
        {
            if (LoadFloorImage(path) is { } image)
            {
                drawingContext.DrawImage(image, area);
                return;
            }
        }

        // 方眼: Surface の地に 1/20 間隔の薄線
        Brush surface = _surfaceBrush ??= FindBrush("Brush.Surface");
        Pen gridPen = _gridPen ??= FrozenPen(FindBrush("Brush.Row.Line"), 1);

        drawingContext.DrawRectangle(surface, null, area);
        for (int i = 1; i < 20; i++)
        {
            double x = area.X + area.Width * i / 20;
            double y = area.Y + area.Height * i / 20;
            drawingContext.DrawLine(gridPen, new Point(x, area.Top), new Point(x, area.Bottom));
            if (y < area.Bottom)
                drawingContext.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
        }
    }

    private void DrawHeat(DrawingContext drawingContext, WifiSurveyViewModel source, Rect area)
    {
        if (source.HeatGrid is not { } grid)
        {
            _heatBitmap = null;
            return;
        }

        if (_heatDirty || _heatBitmap is null)
        {
            _heatBitmap = BakeHeatBitmap(grid, WifiSurveyViewModel.GridWidth, source.GridHeight, source.HeatOpacity);
            _heatDirty = false;
        }

        if (_heatBitmap is not null)
            drawingContext.DrawImage(_heatBitmap, area);
    }

    private WriteableBitmap? BakeHeatBitmap(float[] grid, int gridWidth, int gridHeight, double opacity)
    {
        if (grid.Length != gridWidth * gridHeight)
            return null;

        // バンド色を BGRA(プリマルチ)へ。NaN は完全透明 = 塗らない
        var colors = new uint[Heatmap.BandCount];
        byte alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        for (int band = 0; band < Heatmap.BandCount; band++)
        {
            Color color = BandBrush(band) is SolidColorBrush solid ? solid.Color : Colors.Gray;
            colors[band] = PremultiplyBgra(color, alpha);
        }

        var bitmap = new WriteableBitmap(gridWidth, gridHeight, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new uint[grid.Length];
        for (int i = 0; i < grid.Length; i++)
        {
            float value = grid[i];
            pixels[i] = float.IsNaN(value) ? 0u : colors[Math.Clamp(Heatmap.BandIndex(value), 0, Heatmap.BandCount - 1)];
        }

        bitmap.WritePixels(new Int32Rect(0, 0, gridWidth, gridHeight), pixels, gridWidth * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private void DrawPoints(DrawingContext drawingContext, WifiSurveyViewModel source, Rect area)
    {
        Brush accent = _accentBrush ??= FindBrush("Brush.Accent.Fg");
        Brush chip = _surfaceBrush ??= FindBrush("Brush.Surface");
        Brush text = _textBrush ??= FindBrush("Brush.Text");
        HeatmapSource heatSource = source.CurrentSource;

        foreach (SurveyPoint point in source.Points)
        {
            var center = new Point(area.X + point.X * area.Width, area.Y + point.Y * area.Height);
            drawingContext.DrawEllipse(accent, null, center, 4.5, 4.5);

            if (Heatmap.SelectValue(point.Readings, point.ConnectedBssid, heatSource) is not double value)
                continue;

            // 値チップ。ヒートマップ色は文字の地にしない(読めなくなる)
            var label = new FormattedText(
                value.ToString("0", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                11,
                text,
                _pixelsPerDip);

            var chipRect = new Rect(center.X + 6, center.Y - label.Height / 2 - 2,
                label.Width + 8, label.Height + 4);
            drawingContext.PushOpacity(0.85);
            drawingContext.DrawRoundedRectangle(chip, null, chipRect, 3, 3);
            drawingContext.Pop();
            drawingContext.DrawText(label, new Point(chipRect.X + 4, chipRect.Y + 2));
        }

        if (source.PendingPoint is { } pending)
        {
            Pen pendingPen = _pendingPen ??= FrozenPen(accent, 1.5);
            var center = new Point(area.X + pending.X * area.Width, area.Y + pending.Y * area.Height);
            drawingContext.DrawEllipse(null, pendingPen, center, 4.5, 4.5);
        }
    }

    private (double X, double Y, double W, double H) FitRect()
    {
        double aspect = Source?.AspectRatio ?? 4.0 / 3.0;
        (double x, double y, double w, double h) = Heatmap.FitRect(aspect, ActualWidth, ActualHeight);
        return (x, y, w, h);
    }

    private (double X, double Y)? ToNormalized(Point position)
    {
        (double x, double y, double w, double h) = FitRect();
        if (w <= 0 || h <= 0)
            return null;

        double nx = (position.X - x) / w;
        double ny = (position.Y - y) / h;

        if (nx is < 0 or > 1 || ny is < 0 or > 1)
            return null;

        return (nx, ny);
    }

    private BitmapImage? LoadFloorImage(string path)
    {
        if (_floorImage is not null && string.Equals(_floorImagePath, path, StringComparison.OrdinalIgnoreCase))
            return _floorImage;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;   // 読み込み後にファイルをロックしない
            image.EndInit();
            image.Freeze();

            _floorImagePath = path;
            return _floorImage = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or UnauthorizedAccessException)
        {
            _floorImagePath = path;   // 壊れた画像を毎フレーム読み直さない
            return _floorImage = null;
        }
    }

    private Brush BandBrush(int band)
        => _bandBrushes[band] ??= FindBrush($"Brush.Heatmap.{band + 1}");

    private Brush FindBrush(string key)
        => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static uint PremultiplyBgra(Color color, byte alpha)
    {
        uint r = (uint)(color.R * alpha / 255);
        uint g = (uint)(color.G * alpha / 255);
        uint b = (uint)(color.B * alpha / 255);
        return ((uint)alpha << 24) | (r << 16) | (g << 8) | b;
    }
}
