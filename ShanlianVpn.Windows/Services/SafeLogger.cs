using System.IO;

namespace ShanlianVpn.Windows.Services;

public static class SafeLogger
{
    private static readonly object Lock = new();

    public static void Info(string eventName) => Write("INFO", eventName);

    public static void Error(string errorCode) => Write("ERROR", errorCode);

    private static void Write(string level, string value)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var safeValue = Sanitize(value);
            if (level == "ERROR")
            {
                AppState.LastErrorCode = safeValue;
            }

            var line = $"{DateTimeOffset.Now:O} {level} {safeValue}{Environment.NewLine}";
            lock (Lock)
            {
                File.AppendAllText(AppPaths.LogPath, line);
            }
        }
        catch
        {
            // Logging must never break the VPN client.
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
            .Take(64)
            .ToArray();

        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
