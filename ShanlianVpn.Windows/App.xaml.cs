using System.Windows;
using System.Diagnostics;
using ShanlianVpn.Windows.Services;
using ShanlianVpn.Windows.Views;

namespace ShanlianVpn.Windows;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var startup = Stopwatch.StartNew();
        base.OnStartup(e);

        AppPaths.EnsureDirectories();
        SafeLogger.Info("app_start");

        Window window = TokenStore.HasToken()
            ? new MainWindow()
            : new LoginWindow();

        window.Show();
        startup.Stop();
        SafeLogger.Performance("app_start_ms", startup.ElapsedMilliseconds);
        SafeLogger.Performance("app_start_total_ms", startup.ElapsedMilliseconds);
    }
}
