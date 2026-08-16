using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;

namespace PingWatcher.App.Views;

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

/// <summary>
/// 宛先リストから取り込む相手を選ぶ小窓。
///
/// MessageBox と同じくアプリの配色で自前に描く（ネイティブ描画だと
/// 暗い配色のときにここだけ白い箱が出る）。台数が多い現場を想定して
/// 絞り込み欄を持たせ、絞った結果に対して「すべて選ぶ」が効くようにしてある。
/// </summary>
internal sealed class TargetPickerDialog : Window
{
    private readonly ObservableCollection<PickableTarget> _all = [];
    private readonly ListBox _list;
    private readonly TextBlock _count;

    internal TargetPickerDialog(IEnumerable<(string Host, string Memo)> targets)
    {
        Title = "宛先リストから取り込む";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 460;
        Height = 520;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        foreach ((string host, string memo) in targets)
            _all.Add(new PickableTarget(host, memo));

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
    }

    /// <summary>選ばれた相手。</summary>
    public IReadOnlyList<(string Host, string Memo)> Selected =>
        [.. _all.Where(t => t.Selected).Select(t => (t.Host, t.Memo))];

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
}
