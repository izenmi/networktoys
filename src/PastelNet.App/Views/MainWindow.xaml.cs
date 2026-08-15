using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PastelNet.App.ViewModels;

namespace PastelNet.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
        UpdateThemeToggle();

        _shell.DeviceCompare.RequestScrollIntoView += OnScrollDiffIntoView;
    }

    /// <summary>
    /// 差分をたどるとき、移った先を見えるところへ持ってくる。
    ///
    /// 一覧は仮想化しているので、まだ実体のない行は <see cref="ListBox.ScrollIntoView"/> に
    /// 任せる（内部でスクロールしてから実体を作ってくれる）。
    /// </summary>
    private void OnScrollDiffIntoView(object? sender, int index)
    {
        if (index < 0 || index >= DiffList.Items.Count) return;

        DiffList.ScrollIntoView(DiffList.Items[index]);
    }

    /// <summary>
    /// タイトルバーの明暗を窓の中身に合わせる。ハンドルができてからでないと効かない。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBar();
    }

    private void ApplyTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeToggle();
        ApplyTitleBar();
    }

    /// <summary>
    /// ボタンには「切り替えた先」を出す。いまの状態を出すと、
    /// 押すとどうなるのかが読み取れない。
    /// </summary>
    private void UpdateThemeToggle()
        => ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ 明るく" : "☾ 暗く";

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
        => Topmost = TopmostToggle.IsChecked == true;

    /// <summary>
    /// 測った結果をすべて捨てて起動直後に戻す。宛先リストだけは残す。
    ///
    /// 作業中に誤って押すと、作業前の記録も不通の記録も消えて取り返しがつかない。
    /// 元に戻す手段が無い操作なので、ここだけは確認を挟む。
    /// </summary>
    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            "測定結果・作業の記録・各画面の入力を、起動直後の状態に戻します。\n" +
            "宛先リストは残ります。保存済みのレポートやセッションのファイルは消えません。\n\n" +
            "元に戻せません。実行しますか？",
            "クリア",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        try
        {
            _shell.ClearAll();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClearAll");
        }
    }

    /// <summary>
    /// 右クリックされた行を取り出す。
    ///
    /// <see cref="ContextMenu"/> は論理ツリーが本体から切れているため
    /// 親を辿れない。XAML 側で <c>PlacementTarget</c> の DataContext を
    /// 引き継いであるので、ここでは送り主の DataContext を見ればよい。
    /// <b>選択行は使わない</b>（右クリックでは選択が動かないため）。
    /// </summary>
    private static TargetRowViewModel? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TargetRowViewModel;

    /// <summary>
    /// 落ちている宛先を見つけたとき、そのまま経路を追えるようにする。
    /// 打ち直しの手間と打ち間違いを無くすのが目的。
    /// </summary>
    private void OnTraceFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _shell.Trace.Host = row.Host;
        TraceTab.IsSelected = true;

        if (_shell.Trace.TraceCommand.CanExecute(null))
            _shell.Trace.TraceCommand.Execute(null);
    }

    private void OnResolveFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _shell.Dns.Name = row.Host;
        DnsTab.IsSelected = true;

        if (_shell.Dns.QueryCommand.CanExecute(null))
            _shell.Dns.QueryCommand.Execute(null);
    }

    /// <summary>
    /// 解決済みのアドレスを写す。IP で登録した宛先や、まだ引けていない宛先では
    /// 空になるので、そのときは書いてある文字列をそのまま渡す。
    /// </summary>
    private void OnCopyAddressFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        CopyText(string.IsNullOrWhiteSpace(row.Address) ? row.Host : row.Address);
    }

    private void OnCopyHostFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            CopyText(row.Host);
    }

    /// <summary>
    /// クリップボードは他のプロセスが掴んでいると失敗する。
    /// 写せなかったからといって落ちる操作ではないので、記録して黙って諦める。
    /// </summary>
    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Clipboard.SetText");
        }
    }

    /// <summary>
    /// 無線画面は開かれたときに初めて API を叩く。
    /// Windows 11 24H2 以降はスキャンに位置情報の同意が要るので、
    /// 起動時に呼ぶと脈絡のないタイミングで許可を求めることになる。
    /// </summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 内側の ListBox などの選択変更が浮上してくるので、TabControl 由来だけを扱う
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        if (WifiTab.IsSelected)
            _shell.Wifi.OnActivated();
        else
            _shell.Wifi.OnDeactivated();

        if (ReportTab.IsSelected)
            _shell.Report.OnActivated();

        if (WorkTab.IsSelected)
            _shell.Work.OnActivated();
    }

    /// <summary>
    /// 閉じるときは測定に停止を伝えるだけで、<b>完了は待たない</b>。
    ///
    /// 以前は完了を待ってから閉じていたが、名前解決中やタイムアウト待ちの宛先が
    /// あると終了が数秒固まっていた。設定と宛先リストは編集のたびに保存しているので、
    /// ここで待って守るものは無い。ソケットはプロセス終了時に OS が片付ける。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        try
        {
            _shell.Monitor.BeginStop();
            _shell.Wifi.OnDeactivated();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClosing");
        }
    }
}
