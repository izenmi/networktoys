using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PingWatcher.App.Controls;

/// <summary>
/// 固定サイズのタイルを敷き詰める仮想化パネル。一望表示用。
///
/// WrapPanel は仮想化できないため、数百宛先では起動時に全タイルを組み立てて
/// しまう（CLAUDE.md の「一覧は仮想化必須」に反する）。タイルの大きさが
/// 一定であることを利用して位置を計算で出し、見えている行の前後 1 行ぶん
/// だけを実体化する。
///
/// 前提: すべてのタイルが <see cref="ItemWidth"/> × <see cref="ItemHeight"/>。
/// 大きさの違う項目には使えない。
/// </summary>
internal sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>タイル 1 枚が占める幅。コンテナの余白も含めた値を指定する。</summary>
    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>タイル 1 枚が占める高さ。コンテナの余白も含めた値を指定する。</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    private int ColumnsFor(double width) => Math.Max(1, (int)(width / ItemWidth));

    protected override Size MeasureOverride(Size availableSize)
    {
        // InternalChildren に触れるまでジェネレータは繋がらない（WPF の既知の癖）
        _ = InternalChildren;

        int itemCount = ItemCount;

        // 横スクロールは無効にしてあるので幅は有限のはず。万一無限なら 1 列で扱う
        double width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        int columns = ColumnsFor(width);
        int rows = itemCount == 0 ? 0 : (itemCount + columns - 1) / columns;

        var extent = new Size(width, rows * ItemHeight);
        var viewport = new Size(width, double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);

        if (extent != _extent || viewport != _viewport)
        {
            _extent = extent;
            _viewport = viewport;
            _offset.Y = Clamp(_offset.Y);
            ScrollOwner?.InvalidateScrollInfo();
        }

        if (itemCount == 0)
        {
            if (InternalChildren.Count > 0)
                RemoveInternalChildRange(0, InternalChildren.Count);

            return new Size(width, 0);
        }

        // 見えている行の前後 1 行ぶんまで実体化する
        int firstRow = Math.Max(0, (int)(_offset.Y / ItemHeight) - 1);
        int lastRow = Math.Min(rows - 1, (int)((_offset.Y + _viewport.Height) / ItemHeight) + 1);

        int firstIndex = firstRow * columns;
        int lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);

        IItemContainerGenerator generator = ItemContainerGenerator;
        GeneratorPosition startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (int index = firstIndex; index <= lastIndex; index++, childIndex++)
            {
                if (generator.GenerateNext(out bool newlyRealized) is not UIElement child)
                    break;

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanUp(firstIndex, lastIndex);

        return new Size(width, double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        int columns = ColumnsFor(finalSize.Width);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;

            int row = index / columns;
            int column = index % columns;

            InternalChildren[i].Arrange(new Rect(
                column * ItemWidth,
                (row * ItemHeight) - _offset.Y,
                ItemWidth,
                ItemHeight));
        }

        return finalSize;
    }

    /// <summary>見えている範囲の外に出たコンテナを片付ける。</summary>
    private void CleanUp(int firstIndex, int lastIndex)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;

        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);

            if (index < firstIndex || index > lastIndex)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0)
                    RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }

        InvalidateMeasure();
    }

    private double Clamp(double offset)
        => Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));

    // ===== IScrollInfo =====

    public bool CanVerticallyScroll { get; set; }

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - ItemHeight);

    public void LineDown() => SetVerticalOffset(VerticalOffset + ItemHeight);

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - (ItemHeight * 3));

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + (ItemHeight * 3));

    // 横スクロールは使わない
    public void LineLeft() { }

    public void LineRight() { }

    public void PageLeft() { }

    public void PageRight() { }

    public void MouseWheelLeft() { }

    public void MouseWheelRight() { }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Clamp(offset);
        if (offset == _offset.Y) return;

        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();

        // 実体化すべき範囲が変わるので測り直す
        InvalidateMeasure();
    }

    /// <summary>キーボード移動や ScrollIntoView で、対象の行が見えるところまでだけ動かす。</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element) return rectangle;

        int childIndex = InternalChildren.IndexOf(element);
        if (childIndex < 0) return rectangle;

        int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (index < 0) return rectangle;

        int columns = ColumnsFor(_viewport.Width);
        double top = (index / columns) * ItemHeight;

        if (top < _offset.Y)
            SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height)
            SetVerticalOffset(top + ItemHeight - _viewport.Height);

        return rectangle;
    }
}
