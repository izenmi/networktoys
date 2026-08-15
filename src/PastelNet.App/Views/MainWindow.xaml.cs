using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PastelNet.App.ViewModels;

namespace PastelNet.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();

    // 見張りモードから戻すために覚えておく
    private double _normalWidth;
    private double _normalHeight;
    private bool _wasTopmost;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
        => Topmost = TopmostToggle.IsChecked == true;

    /// <summary>
    /// 小さく表示して、離れた場所からでも状態が分かるようにする。
    /// 別ウィンドウにはしない（測定の状態を持ち回らずに済む）。
    /// </summary>
    private void OnEnterWatchMode(object sender, RoutedEventArgs e)
    {
        _normalWidth = Width;
        _normalHeight = Height;
        _wasTopmost = Topmost;

        MainPanel.Visibility = Visibility.Collapsed;
        WatchPanel.Visibility = Visibility.Visible;

        MinWidth = 260;
        MinHeight = 170;
        Width = 320;
        Height = 210;

        // 見張るのだから手前に出ていないと意味がない
        Topmost = true;
        TopmostToggle.IsChecked = true;
    }

    private void OnLeaveWatchMode(object sender, RoutedEventArgs e)
    {
        WatchPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;

        MinWidth = 900;
        MinHeight = 480;
        Width = _normalWidth > 0 ? _normalWidth : 1140;
        Height = _normalHeight > 0 ? _normalHeight : 720;

        Topmost = _wasTopmost;
        TopmostToggle.IsChecked = _wasTopmost;
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
