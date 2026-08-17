using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NetworkToys.App.Views;

/// <summary>
/// 短い文字を 1 つ聞く小窓（ひな型の名前など）。
///
/// <see cref="ConfirmDialog"/> と同じくアプリの配色で自前に描く
/// （ネイティブ描画だと、暗い配色のときにここだけ白い箱が出る）。
/// </summary>
internal sealed class TextPromptDialog : Window
{
    private readonly TextBox _input;

    private TextPromptDialog(string title, string message, string initial)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 420 * UiScale.Current;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        _input.SelectAll();

        var ok = new Button { Content = "決定", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button { Content = "やめる", MinWidth = 96, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "Button.Subtle");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(text);
        body.Children.Add(_input);
        body.Children.Add(buttons);

        Content = body;

        // 開いた瞬間に打てる状態にする。名前を聞くだけの窓で操作を増やさない
        Loaded += (_, _) => _input.Focus();
    }

    /// <summary>入れてもらった文字。やめたときは null。</summary>
    public static string? Ask(Window owner, string title, string message, string initial = "")
    {
        var dialog = new TextPromptDialog(title, message, initial) { Owner = owner };

        return dialog.ShowDialog() == true ? dialog._input.Text : null;
    }

    /// <summary>タイトルバーの明暗も本体に合わせる。ハンドルができてからでないと効かない。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }
}
