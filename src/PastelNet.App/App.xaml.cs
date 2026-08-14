using System.Windows;

namespace PastelNet.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // CI からの起動確認用。UI を出さずに自己診断だけ行って終了コードを返す。
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(SelfTest.Run());
            return;
        }

        base.OnStartup(e);
        new Views.MainWindow().Show();
    }
}
