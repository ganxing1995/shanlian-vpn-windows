using System.IO;

namespace ShanlianVpn.Windows.Services;

public static class AppPaths
{
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShanlianVPN");

    public static string RuntimeConfigPath => Path.Combine(AppDataDirectory, "runtime-config.json");
    public static string ProxyPreflightConfigPath => Path.Combine(AppDataDirectory, "proxy-preflight-config.json");
    public static string TokenPath => Path.Combine(AppDataDirectory, "auth.dat");
    public static string LogPath => Path.Combine(AppDataDirectory, "client.log");
    public static string SingBoxSessionPath => Path.Combine(AppDataDirectory, "sing-box-session.json");
    public static string ConnectionDiagnosticsPath => Path.Combine(AppDataDirectory, "connection-diagnostics.json");

    public static string SingBoxExePath => SingBoxExeCandidates.FirstOrDefault(File.Exists) ?? SingBoxExeCandidates[0];

    private static string[] SingBoxExeCandidates
    {
        get
        {
            var relativePath = Path.Combine("tools", "sing-box", "windows-amd64", "sing-box.exe");
            return
            [
                Path.Combine(AppContext.BaseDirectory, relativePath),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, relativePath))
            ];
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
    }
}
