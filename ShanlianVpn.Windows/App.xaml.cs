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
        AppPaths.EnsureDirectories();
        var settings = UserSettingsService.Current;
        ThemeService.Apply(settings.ThemeMode);

        if (!SingleInstanceService.TryAcquire("ShanlianVPN.Windows", ActivateExistingWindow))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        SafeLogger.Info("app_start");

        Window window = TokenStore.HasToken()
            ? new MainWindow()
            : new LoginWindow();

        MainWindow = window;
        window.Show();
        startup.Stop();
        SafeLogger.Performance("app_start_ms", startup.ElapsedMilliseconds);
        SafeLogger.Performance("app_start_total_ms", startup.ElapsedMilliseconds);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstanceService.Release();
        base.OnExit(e);
    }

    private static void ActivateExistingWindow()
    {
        if (Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.RestoreFromExternalActivation();
            return;
        }

        if (Current?.MainWindow is Window window)
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
    }
}
