using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PingWatcher.Core.Work;

namespace PingWatcher.App.Controls;

/// <summary>
/// 差分行のテキスト。区切り(<see cref="DiffSegment"/>)ごとに Run を並べ、
/// 違っている箇所だけ地色を変える(WinMerge の行内色分け)。
/// TextBlock 派生なので Mono スタイルや FontSize はそのまま効く。
/// </summary>
public sealed class DiffText : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<DiffSegment>),
        typeof(DiffText),
        new PropertyMetadata(null, OnChanged));

    /// <summary>
    /// 違う箇所に敷く地色。テンプレート側から DynamicResource で与えること
    /// (配色切替に追随させるため。static に握らない)。
    /// </summary>
    public static readonly DependencyProperty ChangedBrushProperty = DependencyProperty.Register(
        nameof(ChangedBrush),
        typeof(Brush),
        typeof(DiffText),
        new PropertyMetadata(null, OnChanged));

    public IReadOnlyList<DiffSegment>? Segments
    {
        get => (IReadOnlyList<DiffSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush? ChangedBrush
    {
        get => (Brush?)GetValue(ChangedBrushProperty);
        set => SetValue(ChangedBrushProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DiffText)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        if (Segments is not { Count: > 0 } segments)
            return;

        foreach (DiffSegment segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.Changed && ChangedBrush is { } brush)
                run.Background = brush;
            Inlines.Add(run);
        }
    }
}
