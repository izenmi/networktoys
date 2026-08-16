using System.ComponentModel;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PingWatcher.App.Services;
using PingWatcher.App.ViewModels;
using PingWatcher.Core.Storage;

namespace PingWatcher.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();

    public MainWindow() : this(null)
    {
    }

    /// <param name="handover">
    /// 管理者として起動し直したときに前のプロセスから渡された状態。通常起動では null。
    /// </param>
    public MainWindow(PingWatcher.Core.Storage.HandoverDocument? handover)
    {
        InitializeComponent();
        DataContext = _shell;
        UpdateThemeToggle();

        // メニューに表記したショートカットの実体。表記(InputGestureText)は飾りなので、
        // ここに足したらメニューの表記も揃えること
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(StartFromShortcut), new KeyGesture(Key.F5)));
        InputBindings.Add(new KeyBinding(
            _shell.Monitor.StopCommand, new KeyGesture(Key.F5, ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(() => OnScreenshot(this, new RoutedEventArgs())),
            new KeyGesture(Key.F12)));

        _shell.DeviceCompare.RequestScrollIntoView += OnScrollDiffIntoView;

        // 「すべて消す」でキーを捨てたら、画面の伏せ字欄も空にする
        // (PasswordBox は中身をバインドできないので VM から知らせてもらう)
        _shell.Meraki.ApiKeyCleared += (_, _) => MerakiKeyBox.Clear();

        // WFP の記録はシステム全体に効く設定なので、立てる前に内容を確認してもらう
        _shell.Wfp.ConfirmEnableCollection += () => ConfirmDialog.Confirm(
            this,
            "ネットワークイベントの記録を有効にする",
            "Windows に、遮断を含むネットワークイベントの記録を始めさせます。\n" +
            "これは PC 全体に効く設定で、他のアプリからも見えます。\n\n" +
            "PingWatcher を閉じるときに元へ戻します。続けますか？",
            okLabel: "有効にする");

        if (handover is not null)
            ApplyHandover(handover);
    }

    /// <summary>
    /// 昇格前の状態を書き戻す。宛先リストと設定は settings.json から
    /// すでに読めているので、ここで扱うのはファイルに残らないものだけ。
    /// </summary>
    private void ApplyHandover(PingWatcher.Core.Storage.HandoverDocument handover)
    {
        try
        {
            Services.HandoverService.Apply(handover, _shell);

            if (handover.WindowWidth > 0 && handover.WindowHeight > 0)
            {
                Left = handover.WindowLeft;
                Top = handover.WindowTop;
                Width = handover.WindowWidth;
                Height = handover.WindowHeight;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (handover.WindowMaximized)
                WindowState = WindowState.Maximized;

            foreach (object? item in MainTabs.Items)
            {
                if (item is TabItem tab && Equals(tab.Header, handover.SelectedTab))
                {
                    tab.IsSelected = true;
                    break;
                }
            }

            // 止まっていたなら止まったまま。動いていたなら測り直しではなく続きから
            if (handover.WasRunning && _shell.Monitor.StartCommand.CanExecute(null))
                _shell.Monitor.StartCommand.Execute(null);

            if (handover.TcpWasRunning && _shell.Tcp.StartCommand.CanExecute(null))
                _shell.Tcp.StartCommand.Execute(null);
        }
        catch (Exception ex)
        {
            // 引き継ぎに失敗しても起動はさせる（引き継げないより起動しない方が困る）
            CrashLog.Write(ex, "MainWindow.ApplyHandover");
        }
    }

    /// <summary>
    /// API キーを VM へ渡す。<see cref="PasswordBox.Password"/> はバインドできない
    /// (平文を依存関係プロパティに置かない設計)ので、変更のたびに手で押し込む。
    /// </summary>
    private void OnMerakiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            _shell.Meraki.ApiKey = box.Password;
    }

    /// <summary>F5 での開始。ボタンと同じく、開始できたら Ping タブへ移る。</summary>
    private void StartFromShortcut()
    {
        if (!_shell.Monitor.StartCommand.CanExecute(null)) return;

        _shell.Monitor.StartCommand.Execute(null);
        PingTab.IsSelected = true;
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

    /// <summary>
    /// Ping 以外の一覧の列幅。Tag は "テーブル名.列番号"。
    /// 掴んだ境界がカーソルから離れないための符号の判断は
    /// <see cref="TableColumns"/> の表の宣言 1 か所に閉じてある。
    /// </summary>
    private void OnTableColumnResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
            TableColumns.Instance.Drag(key, e.HorizontalChange);
    }

    /// <summary>
    /// 表ごとの「列番号 → 並べ替えに使うプロパティ」。空文字の列は並べ替えない。
    /// 行テンプレートの Binding と同じ名前でないと効かないので、列を足したらここも直す。
    /// </summary>
    private static readonly Dictionary<string, string[]> SortPaths = new()
    {
        ["trace"] = ["Ttl", "Address", "HostName", "Rtt", "Note", ""],
        ["scan"] = ["Address", "Rtt", "HostName", "Mac", "Vendor", "Ports", ""],
        ["ftplog"] = ["Time", "Remote", "Text"],
        ["tftplog"] = ["Time", "Remote", "Text"],
        ["sftplog"] = ["Time", "Remote", "Text"],
        ["syslog"] = ["Time", "Remote", "Text"],
        ["snmptrap"] = ["Time", "Remote", "Text"],
        ["snmpget"] = ["Oid", "Name", "Type", "Value"],
        ["mnet"] = ["Name", "Id", "ProductTypes", "TimeZone", "Tags"],
        ["mdev"] = ["Name", "Model", "Serial", "Firmware", "Network", "State", "PublicIp", "LanIp"],
        ["mup"] = ["Network", "Serial", "Interface", "State", "Ip", "Gateway", "PublicIp"],
        ["mcli"] = ["Description", "Ip", "Mac", "Vlan", "Manufacturer", "Usage", "LastSeen"],
    };

    /// <summary>
    /// 見出しの列をクリックして並べ替える（自前の並べ替えを持たない一覧用）。
    ///
    /// どの列かは<b>クリックされた X 座標</b>から求める。列ごとに当たり判定の要素を
    /// 置くとヘッダ 1 つにつき数個ずつ増えるので、見出しの Grid 1 つで受ける。
    /// 並べ替えは <see cref="ICollectionView.SortDescriptions"/> に任せる（VM を
    /// 触らずに済み、仮想化も効いたまま）。押すたびに 昇順 → 降順 → 元の並び。
    /// </summary>
    private void OnTableHeaderSort(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Grid { Tag: string table } header
            || !SortPaths.TryGetValue(table, out string[]? paths))
            return;

        int column = ColumnAt(header, e.GetPosition(header).X);
        if (column < 0 || column >= paths.Length || paths[column].Length == 0) return;

        if (FindList(header) is not { ItemsSource: { } source }) return;

        ICollectionView view = CollectionViewSource.GetDefaultView(source);
        string path = paths[column];

        ListSortDirection? current = null;
        foreach (SortDescription description in view.SortDescriptions)
        {
            if (description.PropertyName != path) continue;

            current = description.Direction;
            break;
        }

        view.SortDescriptions.Clear();

        // 昇順 → 降順 → 元の並び
        if (current is null)
            view.SortDescriptions.Add(new SortDescription(path, ListSortDirection.Ascending));
        else if (current == ListSortDirection.Ascending)
            view.SortDescriptions.Add(new SortDescription(path, ListSortDirection.Descending));

        ShowSortMark(header, column, view.SortDescriptions.Count == 0 ? null : view.SortDescriptions[0].Direction);
    }

    /// <summary>X 座標から列番号を求める。列幅はドラッグで変わるので実測値で見る。</summary>
    private static int ColumnAt(Grid header, double x)
    {
        double left = 0;

        for (int i = 0; i < header.ColumnDefinitions.Count; i++)
        {
            double width = header.ColumnDefinitions[i].ActualWidth;
            if (x >= left && x < left + width) return i;

            left += width;
        }

        return -1;
    }

    /// <summary>見出しと同じ入れ物にいる一覧を探す。</summary>
    private static ItemsControl? FindList(DependencyObject header)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(header);
        return parent is null ? null : FindDescendant(parent);

        static ItemsControl? FindDescendant(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);

                // 見出しの Grid 自身や中の TextBlock を拾わないよう、一覧だけを見る
                if (child is ListBox list) return list;
                if (FindDescendant(child) is { } found) return found;
            }

            return null;
        }
    }

    /// <summary>並べ替えた列の見出しに ▲▼ を付ける。どこで並べ替えているか分かるように。</summary>
    private static void ShowSortMark(Grid header, int column, ListSortDirection? direction)
    {
        foreach (UIElement child in header.Children)
        {
            if (child is not TextBlock text) continue;

            string label = text.Text.TrimEnd(' ', '▲', '▼');

            text.Text = Grid.GetColumn(text) == column && direction is { } d
                ? label + (d == ListSortDirection.Ascending ? " ▲" : " ▼")
                : label;
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
            case WfpViewModel wfp:
                wfp.SortBy(column);
                break;
        }
    }

    /// <summary>
    /// WFP の記録を有効にする。<b>システム全体に効く設定</b>を変えるので、
    /// VM から確認ダイアログを出してもらってから実行する。
    /// </summary>
    private void OnEnableWfpCollection(object sender, RoutedEventArgs e) => _shell.Wfp.EnableCollection();

    /// <summary>
    /// 接続タブの通信量(ETW)や遮断一覧のための管理者再起動。asInvoker は変えない方針なので、
    /// 昇格したい人だけがここから明示的に選ぶ。
    ///
    /// いまの状態は引き継ぎファイルに控えて次のプロセスへ渡す。渡せないのは
    /// 待受中のサーバ(ソケットはプロセスをまたげない)と、進行中の処理だけ。
    /// </summary>
    private void OnRelaunchAsAdmin(object sender, RoutedEventArgs e)
    {
        bool serversRunning = _shell.Ftp.IsRunning || _shell.Tftp.IsRunning || _shell.Sftp.IsRunning
                              || _shell.Syslog.IsRunning || _shell.SnmpTrap.IsRunning;

        string caution = serversRunning
            ? "待受中のサーバは引き継げないので停止します（開き直してください）。\n"
            : "";

        bool confirmed = ConfirmDialog.Confirm(
            this,
            "管理者として再起動",
            "いったん終了して、管理者権限で起動し直します。\n" +
            "測定の結果と各画面の入力はそのまま引き継ぎます。\n" +
            caution +
            "Meraki の API キーだけは保存しない決まりなので、入れ直してください。\n\n" +
            "続けますか？",
            okLabel: "再起動する");

        if (!confirmed) return;

        string handoverPath = HandoverService.NewPath();

        try
        {
            // 単一ファイル発行では Assembly.Location が空になるので Environment.ProcessPath を使う
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("実行ファイルの場所が分かりません");

            HandoverDocument document = HandoverService.Capture(_shell);

            document.SelectedTab = MainTabs.SelectedItem is TabItem tab ? tab.Header?.ToString() ?? "" : "";
            document.WindowMaximized = WindowState == WindowState.Maximized;

            // 最大化中は Left/Width が画面いっぱいの値になるので、元の大きさを控える
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            document.WindowLeft = bounds.Left;
            document.WindowTop = bounds.Top;
            document.WindowWidth = bounds.Width;
            document.WindowHeight = bounds.Height;

            HandoverStore.Save(handoverPath, document);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{HandoverService.Switch} \"{handoverPath}\"",
                UseShellExecute = true,   // UAC の昇格ダイアログを出すのに必須
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or UnauthorizedAccessException)
        {
            // UAC で「いいえ」が選ばれた(1223)か、起動できなかった。いまの窓のまま続ける。
            // 控えたファイルは機器の出力を含みうるので必ず消す
            HandoverStore.Delete(handoverPath);
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

    /// <summary>結果はヘッダの文字で 2 秒だけ知らせる。トーストや別窓は出さない。</summary>
    private void ShowScreenshotResult(string text)
    {
        HeaderNotice.Text = text;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HeaderNotice.Text = "";
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

    private void OnTopmostMenu(object sender, RoutedEventArgs e)
        => Topmost = TopmostMenuItem.IsChecked;

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppData.Directory(),
                UseShellExecute = true,   // フォルダはシェル(エクスプローラー)に開かせる
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnOpenDataFolder");
        }
    }

    private void OnOpenUsage(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/izenmi/pastelnet/blob/main/docs/USAGE.md",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnOpenUsage");
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        // 単一ファイル発行でも AssemblyInformationalVersion は埋め込まれて読める。
        // ビルド環境によっては "+コミットID" が付くので表示用に落とす
        string version = (typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "不明").Split('+')[0];

        ConfirmDialog.Show(
            this,
            "バージョン情報",
            $"PingWatcher {version}\n\n" +
            "色々できるネットワーク診断ツール\n" +
            "https://github.com/izenmi/pastelnet");
    }

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
    /// <summary>右クリックした宛先へ Tera Term で SSH 接続する。</summary>
    private void OnSshFromRow(object sender, RoutedEventArgs e) => ConnectWithTeraTerm(sender, ssh: true);

    /// <summary>右クリックした宛先へ Tera Term で Telnet 接続する。</summary>
    private void OnTelnetFromRow(object sender, RoutedEventArgs e) => ConnectWithTeraTerm(sender, ssh: false);

    /// <summary>
    /// Tera Term でつなぐ（ユーザー指示。現場で使い慣れているものを開く）。
    ///
    /// 場所は既定の導入先から探し、見つからなければ 1 度だけ選んでもらって覚える。
    /// TCP 画面の宛先はポートを持っているので、それがあれば優先する。
    /// </summary>
    private void ConnectWithTeraTerm(object sender, bool ssh)
    {
        if (RowOf(sender) is not { } row || row.Host.Length == 0) return;

        string? exePath = TeraTerm.Locate() ?? AskForTeraTerm();
        if (exePath is null) return;

        int port = row.Target.Kind == PingWatcher.Core.Models.ProbeKind.Tcp ? row.Target.Port : 0;

        if (TeraTerm.Connect(exePath, row.Host, port, ssh) is { } error)
        {
            CrashLog.Write(new InvalidOperationException(error), "MainWindow.ConnectWithTeraTerm");
            ConfirmDialog.Show(this, "Tera Term を起動できません", error);
        }
    }

    /// <summary>見つからないときに場所を選んでもらう。選ばれたら次回から覚えている。</summary>
    private string? AskForTeraTerm()
    {
        ConfirmDialog.Show(
            this,
            "Tera Term が見つかりません",
            "Tera Term (ttermpro.exe) を自動で見つけられませんでした。\n" +
            "次の画面で ttermpro.exe を選んでください（次回からは覚えています）。");

        var dialog = new OpenFileDialog
        {
            Title = "ttermpro.exe を選ぶ",
            FileName = "ttermpro.exe",
            Filter = "Tera Term (ttermpro.exe)|ttermpro.exe|プログラム (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return null;

        TeraTerm.Remember(dialog.FileName);
        return dialog.FileName;
    }

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

        // 遮断一覧も見えている間だけ WFP のエンジンを開く
        if (WfpTab.IsSelected)
            _shell.Wfp.OnActivated();
        else
            _shell.Wfp.OnDeactivated();

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
            _shell.Wfp.OnDeactivated();

            // 自分で立てた WFP の記録設定を戻す(システム全体に効く設定なので置き去りにしない)
            _shell.Wfp.RestoreCollectionSetting();

            _shell.Ftp.Reset();
            _shell.Tftp.Reset();
            _shell.Sftp.Reset();
            _shell.Syslog.Reset();
            _shell.SnmpTrap.Reset();
            ColumnLayout.Instance.Save();
            TableColumns.Instance.Save();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClosing");
        }
    }
}
