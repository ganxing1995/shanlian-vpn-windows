using System.Windows;
using ShanlianVpn.Windows.Services;
using ShanlianVpn.Windows.Views;

namespace ShanlianVpn.Windows;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirectories();
        SafeLogger.Info("app_start");

        Window window = TokenStore.HasToken()
            ? new MainWindow()
            : new LoginWindow();

        window.Show();
    }
}
