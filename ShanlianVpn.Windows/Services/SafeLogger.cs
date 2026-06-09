using System.IO;

namespace ShanlianVpn.Windows.Services;

public static class SafeLogger
{
    private static readonly object Lock = new();

    public static void Info(string eventName) => Write("INFO", eventName);

    public static void Error(string errorCode) => Write("ERROR", errorCode);

    public static void Diagnostic(string stage, string errorCode, string summary)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var safeStage = Sanitize(stage);
            var safeErrorCode = Sanitize(errorCode);
            var safeSummary = SanitizeSummary(summary);
            var line = $"{DateTimeOffset.Now:O} DIAGNOSTIC stage={safeStage} error_code={safeErrorCode} stderr_summary={safeSummary}{Environment.NewLine}";
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

    private static string SanitizeSummary(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "--";
        }

        var sanitized = value;
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(?i)(password|auth_password|token|authorization|configJson|raw_config)(\s*[:=]\s*)\S+",
            "$1$2[redacted]");
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(?i)(check|run)\s+-c\s+""?[^""\r\n]+""?",
            "$1 -c [path]");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"[A-Za-z0-9+/=]{32,}", "[redacted]");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();

        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }
}
