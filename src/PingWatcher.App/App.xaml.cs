using System.Windows;

namespace PingWatcher.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 落ちたときの手掛かりを必ず残す。ローカルで実行できない以上、
        // これが無いと CI でも実機でも原因に辿り着けない。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception, "AppDomain.UnhandledException");

        DispatcherUnhandledException += (_, args) =>
            CrashLog.Write(args.Exception, "Application.DispatcherUnhandledException");

        // 待たれていない Task の例外はここにしか出てこない（拾わないと無音で消える）。
        // 記録するだけで、アプリは落とさない。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        // 配色はウィンドウを作る前に決めておく。後から差し替えると、
        // 明るい画面が一瞬出てから暗くなる。自己診断でも実際の配色で検査したい。
        ThemeManager.Initialize();

        // CI からの起動確認用。UI を出さずに自己診断だけ行って終了コードを返す。
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(SelfTest.Run());
            return;
        }

        base.OnStartup(e);

        // 30 日より古い測定ログの整理。起動を待たせないよう背景で
        _ = Task.Run(Services.SessionLogService.CleanupOldLogs);

        try
        {
            new Views.MainWindow().Show();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.Show");
            throw;
        }
    }
}
