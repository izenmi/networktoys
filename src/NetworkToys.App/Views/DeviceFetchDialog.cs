using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NetworkToys.App.Services;
using NetworkToys.Core.Terminal;

namespace NetworkToys.App.Views;

/// <summary>
/// 機器へ入って <c>show</c> を 1 本流し、その出力をそのまま持ち帰る小窓。
///
/// 差分比較の「作業前」「作業後」に貼るためのもの。会話そのものは収集タブと同じ
/// <see cref="DeviceCollector"/>（＝Core の <see cref="CiscoSession"/>）に任せるので、
/// ページャ無効化・プロンプトの学習・パスワード 2 回で中断は<b>あちらが面倒を見る</b>。
///
/// <b>パスワードは持ち帰らない・保存しない。</b>覚えるのは（このアプリを閉じるまでのあいだ）
/// 接続先とユーザー名と方式だけで、設定ファイルには書かない。
/// </summary>
internal sealed class DeviceFetchDialog : Window
{
    // 作業前・作業後で 2 度開くので、打ち直さなくて済むように覚えておく。
    // <b>プロセスの中だけ。</b>settings.json には書かない（パスワードは覚えない、と同じ扱い）
    private static string _lastHost = "";
    private static string _lastUser = "";
    private static bool _lastUseSsh = true;

    private readonly TextBox _host = new() { Width = 190 };
    private readonly TextBox _user = new() { Width = 130 };
    private readonly PasswordBox _password = new() { Width = 130 };
    private readonly PasswordBox _enable = new() { Width = 130 };
    private readonly TextBox _command = new() { Width = 260 };
    private readonly TextBox _port = new() { Width = 60 };
    private readonly RadioButton _ssh = new() { Content = "SSH", IsChecked = true, Margin = new Thickness(0, 0, 10, 0) };
    private readonly RadioButton _telnet = new() { Content = "Telnet" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _pick = new() { Content = "宛先から", Margin = new Thickness(6, 0, 0, 0) };

    // Enter で取得できるように IsDefault。取得中は IsEnabled=false なので再入はしない
    private readonly Button _fetch = new() { Content = "取得", MinWidth = 96, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Content = "やめる", MinWidth = 96, IsCancel = true };

    private CancellationTokenSource? _cts;

    /// <summary>Ping / TCP に登録してある宛先。<b>空なら「宛先から」は押せない。</b></summary>
    private readonly IReadOnlyList<(string Host, string Memo)> _targets;

    internal DeviceFetchDialog(
        string title, string command, IReadOnlyList<(string Host, string Memo)>? targets = null)
    {
        _targets = targets ?? [];

        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 460 * UiScale.Current;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        SetResourceReference(BackgroundProperty, "Brush.Window.Backdrop");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Body");

        _host.Text = _lastHost;
        _user.Text = _lastUser;
        _command.Text = command;
        _ssh.IsChecked = _lastUseSsh;
        _telnet.IsChecked = !_lastUseSsh;
        _port.Text = _lastUseSsh ? "22" : "23";

        // 方式を変えたら待ち受けポートも既定に戻す（打ち替えはできる）
        _ssh.Checked += (_, _) => _port.Text = "22";
        _telnet.Checked += (_, _) => _port.Text = "23";

        _host.ToolTip = "機器の IP かホスト名。";
        _user.ToolTip = "read-only のユーザーで構いません。";
        _password.ToolTip = "保存しません。窓を閉じると消えます。";
        _enable.ToolTip = "特権に上がる必要が無ければ空のままで構いません。保存しません。";
        _command.ToolTip = "比較する対象に合わせた show が入っています。打ち替えられます。";
        _telnet.ToolTip = "Telnet は暗号化されません。SSH が使えないときだけにしてください。";

        _status.SetResourceReference(StyleProperty, "Caption");
        _cancel.SetResourceReference(StyleProperty, "Button.Subtle");

        _fetch.Click += async (_, _) => await FetchAsync().ConfigureAwait(true);

        _pick.SetResourceReference(StyleProperty, "Button.Subtle");
        _pick.IsEnabled = _targets.Count > 0;
        _pick.ToolTip = _targets.Count > 0
            ? "Ping / TCP タブに登録してある宛先から選びます。打ち込んでも構いません。"
            : "Ping / TCP タブに宛先が登録されていません。";

        _pick.Click += (_, _) =>
        {
            if (TargetPickerDialog.Pick(this, _targets) is [var first, ..])
                _host.Text = first.Host;
        };

        var protocol = new StackPanel { Orientation = Orientation.Horizontal };
        protocol.Children.Add(_ssh);
        protocol.Children.Add(_telnet);
        protocol.Children.Add(new TextBlock
        {
            Text = "ポート",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 6, 0),
        });
        protocol.Children.Add(_port);

        var form = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 接続先は打ってもよいし、Ping の宛先から選んでもよい（2026-08-18 ユーザー指示）。
        // ComboBox の IsEditable はこのアプリのテンプレートでは使えないので、
        // 素の TextBox に「選ぶ」ボタンを添える形にする
        var destination = new StackPanel { Orientation = Orientation.Horizontal };
        destination.Children.Add(_host);
        destination.Children.Add(_pick);

        AddRow(form, "接続先", destination);
        AddRow(form, "方式", protocol);
        AddRow(form, "ユーザー", _user);
        AddRow(form, "パスワード", _password);
        AddRow(form, "enable", _enable);
        AddRow(form, "コマンド", _command);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_fetch);
        buttons.Children.Add(_cancel);

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new TextBlock
        {
            Text = "機器へ入って show を 1 本流し、その出力をこの欄に入れます。"
                   + "設定は書きません（読み取りだけです）。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        body.Children.Add(form);
        body.Children.Add(buttons);
        body.Children.Add(_status);

        Content = body;

        // 前と同じ相手なら、打つのはパスワードだけで済むようにする
        Loaded += (_, _) => ((Control)(_host.Text.Length == 0 ? _host : _password)).Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>取ってきた出力。やめたときは null。</summary>
    public string? Output { get; private set; }

    /// <summary>
    /// 小窓を出して、機器から 1 本取ってくる。
    /// <paramref name="command"/> は比較する対象に合わせた既定（打ち替えられる）。
    /// </summary>
    /// <param name="targets">接続先の候補（Ping / TCP の宛先）。<b>打ち込みも受ける。</b></param>
    public static string? Fetch(
        Window owner, string title, string command,
        IReadOnlyList<(string Host, string Memo)>? targets = null)
    {
        var dialog = new DeviceFetchDialog(title, command, targets) { Owner = owner };

        return dialog.ShowDialog() == true ? dialog.Output : null;
    }

    private static void AddRow(Grid grid, string label, UIElement input)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 10, 3),
        };

        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        if (input is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.Margin = new Thickness(0, 3, 0, 3);
        }

        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    private async Task FetchAsync()
    {
        if (_cts is not null) return;

        string host = _host.Text.Trim();
        string command = _command.Text.Trim();

        if (host.Length == 0 || _user.Text.Trim().Length == 0)
        {
            _status.Text = "接続先とユーザー名を入れてください。";
            return;
        }

        if (command.Length == 0)
        {
            _status.Text = "流すコマンドを入れてください。";
            return;
        }

        // 収集タブと同じ物差しで弾く。ここでも上書きの手段は作らない
        CommandVerdict verdict = CiscoCommandGuard.Classify(command);

        if (verdict.Risk == CommandRisk.Blocked)
        {
            _status.Text = $"このコマンドは流せません: {verdict.Reason}";
            return;
        }

        if (!int.TryParse(_port.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            _status.Text = "ポート番号が正しくありません。";
            return;
        }

        bool useSsh = _ssh.IsChecked == true;

        _cts = new CancellationTokenSource();
        _fetch.IsEnabled = false;
        _status.Text = $"{host} へ接続しています…";

        try
        {
            var request = new CollectRequest(
                Host: host,
                Port: port,
                UseSsh: useSsh,
                Credentials: new DeviceCredentials(_user.Text.Trim(), _password.Password, _enable.Password),
                Memo: "差分比較");

            var progress = new Progress<CollectProgress>(p => _status.Text = p.Message);

            DeviceCollectionResult result = await DeviceCollector.CollectAsync(
                request, [command], new CiscoSessionOptions(),
                TimeSpan.FromSeconds(15), progress, _cts.Token).ConfigureAwait(true);

            if (result.FailureMessage is { Length: > 0 } failure)
            {
                _status.Text = failure;
                return;
            }

            CommandResult? output = result.Commands.FirstOrDefault();

            if (output is null || output.Output.Trim().Length == 0)
            {
                _status.Text = "つながりましたが、出力が空でした。コマンドを確かめてください。";
                return;
            }

            // 覚えるのは接続先とユーザー名と方式だけ。パスワードは覚えない
            _lastHost = host;
            _lastUser = _user.Text.Trim();
            _lastUseSsh = useSsh;

            Output = output.Output;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "中断しました。";
        }
        catch (Exception ex)
        {
            _status.Text = $"取得できませんでした: {ex.Message}";
            CrashLog.Write(ex, "DeviceFetchDialog.FetchAsync");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _fetch.IsEnabled = true;
        }
    }

    /// <summary>タイトルバーの明暗も本体に合わせる。ハンドルができてからでないと効かない。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }
}
