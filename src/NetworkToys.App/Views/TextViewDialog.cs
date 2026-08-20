using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NetworkToys.App.Views;

/// <summary>
/// 長い文章を読ませるための小窓（ライセンス表示など）。
///
/// <see cref="ConfirmDialog"/> は <c>SizeToContent</c> で伸び切るので長文に向かない。
/// こちらは大きさを決めてスクロールさせ、<b>選んでコピーできる</b>ようにする
/// （読み取り専用の <see cref="TextBox"/>。<see cref="TextBlock"/> だと選べない）。
///
/// 作りはほかの小窓（<see cref="ConfirmDialog"/> / <see cref="TextPromptDialog"/> /
/// <see cref="TargetPickerDialog"/>）に合わせてある — XAML を持たず、配色は
/// <c>SetResourceReference</c> で当て、タイトルバーの明暗も本体に揃える。
/// </summary>
internal sealed class TextViewDialog : Window
{
    private TextViewDialog(string title, string text, bool wrap)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // 文字の大きさに合わせて窓も大きくする（中身だけ大きくすると読める量が減る）
        Width = 720 * UiScale.Current;
        Height = 560 * UiScale.Current;
        MinWidth = 420 * UiScale.Current;
        MinHeight = 300 * UiScale.Current;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        // 等幅で出す。ライセンス本文は桁を揃えて書かれており、
        // プロポーショナルだと罫線や字下げが崩れる。
        // 折り返し（wrap）が既定 — 長い 1 行を横スクロールで追うのは読めない
        // （2026-08-20 ユーザー指示）。桁を揃えた文（ライセンス）だけ NoWrap で開く
        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        body.SetResourceReference(FontFamilyProperty, "Font.Mono");
        body.SetResourceReference(FontSizeProperty, "Size.Caption");

        var close = new Button { Content = "閉じる", MinWidth = 96, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(close);

        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(body);

        Content = panel;
    }

    /// <summary>読ませる。閉じるまで戻らない。既定は折り返し（横スクロールを出さない）。</summary>
    public static void Show(Window owner, string title, string text, bool wrap = true)
        => new TextViewDialog(title, text, wrap) { Owner = owner }.ShowDialog();

    /// <summary>タイトルバーの明暗も本体に合わせる。ハンドルができてからでないと効かない。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }
}
