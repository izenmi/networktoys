using System.ComponentModel;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PingWatcher.App.Services;
using PingWatcher.App.ViewModels;

namespace PingWatcher.App.Views;

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

    /// <summary>開始したら測定の画面へ移る。押した場所がどのタブでも、見たいのは結果。</summary>
    private void OnStartClicked(object sender, RoutedEventArgs e) => PingTab.IsSelected = true;

    /// <summary>
    /// Ping 一覧の列幅ドラッグ。掴んだ列だけを伸縮させ、余りは可変幅の備考列に
    /// 吸収させる(GridSplitter の「隣の列も動く」挙動を避けるための自前実装)。
    ///
    /// 備考(星列)より右の列は、幅を変えても<b>右端は動かず左端が動く</b>。
    /// そのため RTT/ロス/推移のつまみは左端の境界にあり、符号を反転して
    /// 「境界を右へ動かす=その列が縮む」にしている。こうすると境界が
    /// カーソルに 1:1 で追従する(右端につまみを置くとカーソルから離れて
    /// ドラッグ量が暴走する — 実際に起きた)。
    /// </summary>
    private void OnColumnResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column })
            return;

        ColumnLayout layout = ColumnLayout.Instance;

        GridLength Grow(GridLength current)
            => new(Math.Clamp(current.Value + e.HorizontalChange, 36, 600));

        GridLength Shrink(GridLength current)
            => new(Math.Clamp(current.Value - e.HorizontalChange, 36, 600));

        switch (column)
        {
            // 星列より左: 右端の境界が幅と一緒に動くので、そのまま足す
            case "State": layout.State = Grow(layout.State); break;
            case "Target": layout.Target = Grow(layout.Target); break;

            // 星列より右: 左端の境界を掴んでいるので逆向き
            case "Rtt": layout.Rtt = Shrink(layout.Rtt); break;
            case "Loss": layout.Loss = Shrink(layout.Loss); break;
            case "Spark": layout.Spark = Shrink(layout.Spark); break;
        }
    }

    /// <summary>一覧の見出しクリックで並べ替える。列名は Tag、対象は DataContext で見分ける。</summary>
    private void OnListHeaderSort(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column } element)
            return;

        switch (element.DataContext)
        {
            case MonitorViewModel monitor:
                monitor.SortBy(column);
                break;
            case ConnectionsViewModel connections:
                connections.SortBy(column);
                break;
            case WifiViewModel wifi:
                wifi.SortAccessPointsBy(column);
                break;
        }
    }

    /// <summary>
    /// 接続タブの通信量(ETW)のための管理者再起動。asInvoker は変えない方針なので、
    /// 昇格したい人だけがここから明示的に選ぶ。測定中の結果は失われるため確認を挟む。
    /// </summary>
    private void OnRelaunchAsAdmin(object sender, RoutedEventArgs e)
    {
        bool confirmed = ConfirmDialog.Confirm(
            this,
            "管理者として再起動",
            "いったん終了して、管理者権限で起動し直します。\n" +
            "測定中の結果と各画面の表示は失われます（宛先リストと設定は残ります）。\n\n" +
            "続けますか？",
            okLabel: "再起動する");

        if (!confirmed) return;

        try
        {
            // 単一ファイル発行では Assembly.Location が空になるので Environment.ProcessPath を使う
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("実行ファイルの場所が分かりません");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,   // UAC の昇格ダイアログを出すのに必須
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // UAC で「いいえ」が選ばれた(1223)か、起動できなかった。いまの窓のまま続ける
            return;
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// いまの画面を PNG にして logs フォルダへ残す。証跡は「見たままの画面」が
    /// 一番説明が早いので、レポートとは別にワンボタンで撮れるようにしている。
    /// </summary>
    private void OnScreenshot(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = SessionLogService.NewScreenshotPath();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            using (FileStream stream = File.Create(path))
                CaptureWindow().Save(stream);

            ShowScreenshotResult("✓ 保存しました");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnScreenshot");
            ShowScreenshotResult("保存できません");
        }
    }

    /// <summary>
    /// ウィンドウの中身を実 DPI で描画して PNG エンコーダに載せる。
    /// Window そのものを Render すると枠ぶんの余白がずれることがあるため、
    /// VisualBrush で中身を描き直す。地色は Window 側が持っているので先に敷く。
    /// </summary>
    internal PngBitmapEncoder CaptureWindow()
    {
        var root = (FrameworkElement)Content;
        var area = new Rect(new Size(root.ActualWidth, root.ActualHeight));

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Background, null, area);
            context.DrawRectangle(new VisualBrush(root), null, area);
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(area.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(area.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }

    /// <summary>
    /// IP 設定の適用。自分の足場(この PC の通信)を壊せる操作なので、
    /// UAC の前に内容の確認を挟む(UAC は「昇格の同意」、こちらは「内容の確認」)。
    /// </summary>
    private async void OnApplyIpConfig(object sender, RoutedEventArgs e)
    {
        string summary = _shell.IpConfig.ConfirmationSummary();
        if (summary.Length == 0)
            return;

        bool confirmed = ConfirmDialog.Confirm(
            this,
            "IP 設定の適用",
            summary + "\n\nこの PC の通信が一時的に切れることがあります。続けますか？",
            okLabel: "適用する");

        if (!confirmed)
            return;

        try
        {
            await _shell.IpConfig.ApplyAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnApplyIpConfig");
        }
    }

    /// <summary>結果はボタンの文字で 2 秒だけ知らせる。トーストや別窓は出さない。</summary>
    private void ShowScreenshotResult(string text)
    {
        ScreenshotButton.Content = text;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ScreenshotButton.Content = "📷 画面";
        };
        timer.Start();
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

    /// <summary>消す範囲を選ばせる。ボタンの下にメニューを出す。</summary>
    private void OnClearMenu(object sender, RoutedEventArgs e)
    {
        if (ClearButton.ContextMenu is not { } menu) return;

        menu.PlacementTarget = ClearButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 測った結果だけを消す。宛先も、作業の記録も、機器の貼り付けも残る。
    ///
    /// やり直しの範囲が小さく、Ping タブの「履歴を消去」と同じ重さなので確認は挟まない。
    /// </summary>
    private async void OnClearPingResults(object sender, RoutedEventArgs e)
    {
        try
        {
            await _shell.Monitor.ResetResultsAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClearPingResults");
        }
    }

    /// <summary>
    /// 測った結果をすべて捨てて起動直後に戻す。宛先リストだけは残す。
    ///
    /// 作業中に誤って押すと、作業前の記録も不通の記録も消えて取り返しがつかない。
    /// 元に戻す手段が無い操作なので、ここだけは確認を挟む。
    /// </summary>
    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        // MessageBox はネイティブ描画で、ダークテーマだとそこだけ白い箱が出る。
        // アプリの配色で描く自前の確認ダイアログを使う
        bool confirmed = ConfirmDialog.Confirm(
            this,
            "クリア",
            "測定結果・作業の記録・各画面の入力を、起動直後の状態に戻します。\n" +
            "宛先リストは残ります。保存済みのレポートやセッションのファイルは消えません。\n\n" +
            "元に戻せません。実行しますか？",
            okLabel: "すべて消す");

        if (!confirmed) return;

        try
        {
            await _shell.ClearAllAsync();
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
    /// <summary>
    /// 自己診断が無線タブの画面を実体化して検査するときに true。
    /// タブ選択に反応して WLAN API へ触れるのを止める（位置情報の同意を求めないため）。
    /// </summary>
    internal bool SuppressWifiActivation;

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 内側の ListBox などの選択変更が浮上してくるので、TabControl 由来だけを扱う
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        if (SuppressWifiActivation) return;

        if (WifiTab.IsSelected)
            _shell.Wifi.OnActivated();
        else
            _shell.Wifi.OnDeactivated();

        // 接続一覧もタブが見えている間だけ OS を叩く
        if (ConnectionsTab.IsSelected)
            _shell.Connections.OnActivated();
        else
            _shell.Connections.OnDeactivated();

        // IP設定はタブを開いたときにアダプタを列挙し直す
        if (IpConfigTab.IsSelected)
            _shell.IpConfig.OnActivated();

        if (ReportTab.IsSelected)
            _shell.Report.OnActivated();
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
            _shell.Tcp.BeginStop();
            _shell.Wifi.OnDeactivated();
            _shell.Connections.OnDeactivated();
            _shell.Ftp.Reset();
            _shell.Tftp.Reset();
            _shell.Sftp.Reset();
            _shell.Syslog.Reset();
            _shell.SnmpTrap.Reset();
            ColumnLayout.Instance.Save();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClosing");
        }
    }
}
