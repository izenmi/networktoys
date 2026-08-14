using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PastelNet.App.ViewModels;

namespace PastelNet.App.Views;

public partial class MainWindow : Window
{
    private static readonly (string Label, string BgKey, string FgKey)[] SwatchData =
    [
        ("背景",         "Brush.Background", "Brush.Text"),
        ("サーフェス",   "Brush.Surface",    "Brush.Text"),
        ("交互行",       "Brush.SurfaceAlt", "Brush.Text"),
        ("罫線",         "Brush.Border",     "Brush.Text"),
        ("文字(淡)",     "Brush.Surface",    "Brush.TextMuted"),
        ("ミント/応答",  "Brush.Ok.Bg",      "Brush.Ok.Fg"),
        ("ピーチ/遅延",  "Brush.Warn.Bg",    "Brush.Warn.Fg"),
        ("ローズ/不達",  "Brush.Error.Bg",   "Brush.Error.Fg"),
        ("スカイ/情報",  "Brush.Info.Bg",    "Brush.Info.Fg"),
        ("ラベンダー",   "Brush.Accent.Bg",  "Brush.Accent.Fg"),
    ];

    private readonly MonitorViewModel _viewModel = new();
    private bool _stopped;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        BuildSwatches();
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

    private void BuildSwatches()
    {
        foreach ((string label, string bgKey, string fgKey) in SwatchData)
        {
            var swatch = new Border
            {
                Background = (Brush)FindResource(bgKey),
                BorderBrush = (Brush)FindResource("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Height = 62,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = label,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = (Brush)FindResource(fgKey),
                        },
                        new TextBlock
                        {
                            Text = ((SolidColorBrush)FindResource(bgKey)).Color.ToString(),
                            FontSize = 11,
                            Foreground = (Brush)FindResource(fgKey),
                            Opacity = 0.85,
                        },
                    },
                },
            };
            SwatchGrid.Children.Add(swatch);
        }
    }
}
