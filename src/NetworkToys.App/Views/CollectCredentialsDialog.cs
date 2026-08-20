using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NetworkToys.App.Views;

/// <summary>
/// ログ採取の全行へ、同じ認証情報を一度に流し込む小窓（2026-08-20 の UX 改善）。
///
/// 現場は共通の TACACS+/AD が多く、20 台に同じパスワードを 1 行ずつ打つのは
/// 手間でしかない。作りは <see cref="DeviceFetchDialog"/> と同じコード組み。
///
/// <b>パスワードはどこにも保存しない。</b>この窓のローカル → 行の VM に入るだけで、
/// settings.json にも引き継ぎファイルにも入れ物が無い（既存の名前検査が守っている）。
/// </summary>
internal sealed class CollectCredentialsDialog : Window
{
    private readonly TextBox _user = new() { Width = 150 };
    private readonly PasswordBox _password = new() { Width = 150 };
    private readonly PasswordBox _enable = new() { Width = 150 };

    // 既定は「空の欄だけに入れる」。ユーザー名は前回値の自動補完で埋まっている行が多く、
    // 常時上書きにするとローカル認証の例外機器（1〜2 台だけ別パスワード）を黙って潰す
    private readonly CheckBox _overwrite = new()
    {
        Content = "入力済みの欄も上書きする",
        ToolTip = "外したままなら、空いている欄にだけ入れます（手で入れた例外の機器を潰しません）。",
    };

    private readonly Button _apply = new() { Content = "全行に入れる", MinWidth = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Content = "やめる", MinWidth = 96, IsCancel = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    internal CollectCredentialsDialog()
    {
        Title = "全行に認証情報を入れる";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 400 * UiScale.Current;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        _user.ToolTip = "空のままなら、ユーザー名の欄は触りません。";
        _password.ToolTip = "空のままなら、パスワードの欄は触りません。保存しません。";
        _enable.ToolTip = "空のままなら、enable の欄は触りません。保存しません。";

        _status.SetResourceReference(StyleProperty, "Caption");
        _cancel.SetResourceReference(StyleProperty, "Button.Subtle");

        _apply.Click += (_, _) =>
        {
            // 3 欄とも空なら、何をしたいのか分からないので閉じずに案内する
            if (_user.Text.Length == 0 && _password.Password.Length == 0 && _enable.Password.Length == 0)
            {
                _status.Text = "入れたい欄を 1 つ以上書いてください（空の欄は触りません）。";
                return;
            }

            DialogResult = true;
        };

        var form = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        DeviceFetchDialog.AddRow(form, "ユーザー", _user);
        DeviceFetchDialog.AddRow(form, "パスワード", _password);
        DeviceFetchDialog.AddRow(form, "enable", _enable);
        DeviceFetchDialog.AddRow(form, "", _overwrite);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_apply);
        buttons.Children.Add(_cancel);

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new TextBlock
        {
            Text = "一覧の全行に、同じ認証情報を一度に入れます。空にした欄は触りません。"
                   + "パスワードは保存しません。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        body.Children.Add(form);
        body.Children.Add(buttons);
        body.Children.Add(_status);

        Content = body;

        Loaded += (_, _) => ((Control)(_user.Text.Length == 0 ? _user : _password)).Focus();
    }

    /// <summary>
    /// 小窓を出して、入れる内容を返す。やめたときは null。
    /// </summary>
    public static (string User, string Password, string Enable, bool Overwrite)? Ask(Window owner)
    {
        var dialog = new CollectCredentialsDialog { Owner = owner };

        return dialog.ShowDialog() == true
            ? (dialog._user.Text.Trim(), dialog._password.Password, dialog._enable.Password,
               dialog._overwrite.IsChecked == true)
            : null;
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }
}
