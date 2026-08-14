using System.ComponentModel;
using System.Windows;
using PastelNet.App.ViewModels;

namespace PastelNet.App.Views;

public partial class MainWindow : Window
{
    private readonly MonitorViewModel _viewModel = new();
    private bool _stopped;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
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
            await _viewModel.StopAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClosing");
        }

        _stopped = true;
        Close();
    }
}
