using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PastelNet.App.ViewModels;

namespace PastelNet.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();
    private bool _stopped;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _shell;
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
    }

    /// <summary>
    /// 閉じる前に測定を止める。停止は非同期なので、いったん閉じるのを取り消し、
    /// 完了してから閉じ直す（UI スレッドで同期的に待つとデッドロックする）。
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_stopped)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        try
        {
            await _shell.Monitor.StopAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClosing");
        }

        _stopped = true;
        Close();
    }
}
