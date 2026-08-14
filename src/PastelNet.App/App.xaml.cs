using System.Windows;

namespace PastelNet.App;

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

        // CI からの起動確認用。UI を出さずに自己診断だけ行って終了コードを返す。
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(SelfTest.Run());
            return;
        }

        base.OnStartup(e);

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
