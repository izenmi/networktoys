using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;

namespace NetworkToys.App.Views;

/// <summary>選択画面の 1 行。</summary>
public sealed class PickableTarget(string host, string memo) : INotifyPropertyChanged
{
    private bool _selected;

    public string Host { get; } = host;
    public string Memo { get; } = memo;

    /// <summary>取り込む相手か。</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>選択元のリスト 1 本(「いまの宛先」や保存したリスト)。</summary>
internal sealed record TargetListSource(string Name, IReadOnlyList<(string Host, string Memo)> Targets);

/// <summary>
/// 宛先リストから取り込む相手を選ぶ小窓。
///
/// MessageBox と同じくアプリの配色で自前に描く（ネイティブ描画だと
/// 暗い配色のときにここだけ白い箱が出る）。台数が多い現場を想定して
/// 絞り込み欄を持たせ、絞った結果に対して「すべて選ぶ」が効くようにしてある。
/// </summary>
internal sealed class TargetPickerDialog : Window
{
    /// <summary>リストごとの行。選択はリストをまたいで覚えたまま切り替えられる。</summary>
    private readonly IReadOnlyList<(TargetListSource Source, ObservableCollection<PickableTarget> Rows)> _sources;

    /// <summary>いま表示しているリストの行。</summary>
    private ObservableCollection<PickableTarget> _all;

    private readonly ListBox _list;
    private readonly TextBlock _count;

    internal TargetPickerDialog(IEnumerable<(string Host, string Memo)> targets)
        : this([new TargetListSource("いまの宛先", [.. targets])])
    {
    }

    internal TargetPickerDialog(IReadOnlyList<TargetListSource> sources)
    {
        Title = "宛先リストから取り込む";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // 文字の大きさに合わせて窓も大きくする
        Width = 460 * UiScale.Current;
        Height = 520 * UiScale.Current;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        _sources =
        [
            .. sources.Select(s => (s,
                new ObservableCollection<PickableTarget>(
                    s.Targets.Select(t => new PickableTarget(t.Host, t.Memo))))),
        ];
        _all = _sources[0].Rows;

        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        filter.SetResourceReference(FontFamilyProperty, "Font.Mono");
        filter.TextChanged += (_, _) => ApplyFilter(filter.Text);

        var filterLabel = new TextBlock { Text = "絞り込み（宛先・備考の一部）", Margin = new Thickness(0, 0, 0, 3) };
        filterLabel.SetResourceReference(StyleProperty, "Caption");

        _list = new ListBox
        {
            ItemsSource = _all,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = BuildRowTemplate(),
        };

        _count = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        _count.SetResourceReference(StyleProperty, "Caption");

        var selectAll = new Button { Content = "表示中をすべて選ぶ", Margin = new Thickness(0, 0, 6, 0) };
        selectAll.SetResourceReference(StyleProperty, "Button.Subtle");
        selectAll.Click += (_, _) => SetAllVisible(true);

        var clearAll = new Button { Content = "選択を外す" };
        clearAll.SetResourceReference(StyleProperty, "Button.Subtle");
        clearAll.Click += (_, _) => SetAllVisible(false);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tools.Children.Add(selectAll);
        tools.Children.Add(clearAll);

        var ok = new Button { Content = "取り込む", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button { Content = "やめる", MinWidth = 96, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "Button.Subtle");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new DockPanel { Margin = new Thickness(14) };

        // どのリストから選ぶか。保存したリスト(Ping/TCP)も全部並ぶ(2026-08-21 ユーザー指示)。
        // 選択はリストをまたいで残るので、複数のリストから拾って一度に取り込める
        if (_sources.Count > 1)
        {
            var listLabel = new TextBlock { Text = "宛先リスト", Margin = new Thickness(0, 0, 0, 3) };
            listLabel.SetResourceReference(StyleProperty, "Caption");

            var picker = new ComboBox
            {
                ItemsSource = _sources.Select(s => s.Source.Name).ToList(),
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 6),
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0) return;

                _all = _sources[picker.SelectedIndex].Rows;
                _list.ItemsSource = _all;
                ApplyFilter(filter.Text);
            };

            DockPanel.SetDock(listLabel, Dock.Top);
            DockPanel.SetDock(picker, Dock.Top);
            body.Children.Add(listLabel);
            body.Children.Add(picker);
        }

        DockPanel.SetDock(filterLabel, Dock.Top);
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(tools, Dock.Bottom);
        DockPanel.SetDock(_count, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);

        body.Children.Add(filterLabel);
        body.Children.Add(filter);
        body.Children.Add(buttons);
        body.Children.Add(tools);
        body.Children.Add(_count);
        body.Children.Add(_list);

        Content = body;
        UpdateCount();

        // 開いたらすぐ打てるように、絞り込み欄へフォーカス（2026-08-20 の UI 改善）
        Loaded += (_, _) => filter.Focus();
    }

    /// <summary>全リストの全行。自己診断が選択の横断をなぞるためだけの口。</summary>
    internal IEnumerable<PickableTarget> AllRowsForSelfTest => _sources.SelectMany(s => s.Rows);

    /// <summary>選ばれた相手(全リスト横断・重複は先勝ち)。</summary>
    public IReadOnlyList<(string Host, string Memo)> Selected =>
        [.. _sources.SelectMany(s => s.Rows)
            .Where(t => t.Selected)
            .DistinctBy(t => t.Host, StringComparer.OrdinalIgnoreCase)
            .Select(t => (t.Host, t.Memo))];

    private DataTemplate BuildRowTemplate()
    {
        // チェックと文字を 1 行に並べるだけ。XAML を持たない小窓なのでコードで組む
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButtonIsCheckedProperty, new Binding(nameof(PickableTarget.Selected)) { Mode = BindingMode.TwoWay });
        check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));

        var host = new FrameworkElementFactory(typeof(TextBlock));
        host.SetBinding(TextBlock.TextProperty, new Binding(nameof(PickableTarget.Host)));
        host.SetValue(WidthProperty, 150.0);
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var memo = new FrameworkElementFactory(typeof(TextBlock));
        memo.SetBinding(TextBlock.TextProperty, new Binding(nameof(PickableTarget.Memo)));
        memo.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        memo.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(check);
        panel.AppendChild(host);
        panel.AppendChild(memo);

        return new DataTemplate { VisualTree = panel };
    }

    private static readonly DependencyProperty ToggleButtonIsCheckedProperty =
        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private void ApplyFilter(string text)
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(_all);

        view.Filter = text.Length == 0
            ? null
            : o => o is PickableTarget t
                   && (t.Host.Contains(text, StringComparison.OrdinalIgnoreCase)
                       || t.Memo.Contains(text, StringComparison.OrdinalIgnoreCase));

        UpdateCount();
    }

    /// <summary>いま見えている行だけを対象にする（絞り込みの意味が消えないように）。</summary>
    private void SetAllVisible(bool selected)
    {
        foreach (object item in CollectionViewSource.GetDefaultView(_all))
        {
            if (item is PickableTarget target) target.Selected = selected;
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        int visible = CollectionViewSource.GetDefaultView(_all).Cast<object>().Count();

        _count.Text = $"表示 {visible} 件 / 全 {_all.Count} 件　選択 {Selected.Count} 件";
    }

    /// <summary>タイトルバーの明暗も本体に合わせる。ハンドルができてからでないと効かない。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }

    /// <summary>選ばれた宛先を返す。取り込まないときは空。</summary>
    public static IReadOnlyList<(string Host, string Memo)> Pick(
        Window owner, IEnumerable<(string Host, string Memo)> targets)
    {
        var dialog = new TargetPickerDialog(targets) { Owner = owner };

        return dialog.ShowDialog() == true ? dialog.Selected : [];
    }

    /// <summary>複数のリストから選ばせる版。</summary>
    public static IReadOnlyList<(string Host, string Memo)> Pick(
        Window owner, IReadOnlyList<TargetListSource> sources)
    {
        var dialog = new TargetPickerDialog(sources) { Owner = owner };

        return dialog.ShowDialog() == true ? dialog.Selected : [];
    }
}
