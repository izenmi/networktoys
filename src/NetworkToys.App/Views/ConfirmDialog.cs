using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NetworkToys.App.Views;

/// <summary>
/// アプリの配色で描く小さな確認ダイアログ。
///
/// MessageBox はネイティブ描画のため、ダークテーマのときにそこだけ
/// 白い箱が出て目に刺さる。取り返しのつかない操作（全消去）の確認にしか
/// 使わないので、必要最小限の OK / キャンセルだけを持つ。
/// </summary>
internal sealed class ConfirmDialog : Window
{
    internal ConfirmDialog(string title, string message, string okLabel, bool showCancel = true)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
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
            MaxWidth = 420 * UiScale.Current,
            Margin = new Thickness(0, 0, 0, 14),
        };

        var ok = new Button { Content = okLabel, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);

        if (showCancel)
        {
            // 既定はキャンセル側。Enter の空押しで消えてしまわないように
            var cancel = new Button { Content = "やめる", MinWidth = 96, IsDefault = true, IsCancel = true };
            cancel.SetResourceReference(StyleProperty, "Button.Subtle");
            buttons.Children.Add(cancel);
        }
        else
        {
            // 知らせるだけのときは選択肢がないので、Enter でも Esc でも閉じられるように
            ok.IsDefault = true;
            ok.IsCancel = true;
        }

        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        body.Children.Add(text);
        body.Children.Add(buttons);

        Content = body;
    }

    /// <summary>タイトルバーの明暗も本体に合わせる。ハンドルができてからでないと効かない。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }

    /// <summary>OK が押されたときだけ true。</summary>
    public static bool Confirm(Window owner, string title, string message, string okLabel = "実行する")
    {
        var dialog = new ConfirmDialog(title, message, okLabel) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    /// <summary>知らせるだけの版(バージョン情報など)。閉じるだけで選択肢はない。</summary>
    public static void Show(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog(title, message, okLabel: "閉じる", showCancel: false) { Owner = owner };
        dialog.ShowDialog();
    }
}
