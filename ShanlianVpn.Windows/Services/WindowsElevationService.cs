using System.Diagnostics;

namespace ShanlianVpn.Windows.Services;

public static class WindowsElevationService
{
    public static bool RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            SafeLogger.Error("elevation_failed");
            return false;
        }
    }
}
