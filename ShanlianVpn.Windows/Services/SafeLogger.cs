using System.Collections.Concurrent;
using System.IO;

namespace ShanlianVpn.Windows.Services;

public static class SafeLogger
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int MaxLogFiles = 5;
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static int _workerStarted;

    public static void Info(string eventName) => Write("INFO", eventName);

    public static void Error(string errorCode) => Write("ERROR", errorCode);

    public static void Performance(string metric, long elapsedMs)
    {
        var safeMetric = Sanitize(metric);
        Enqueue($"{DateTimeOffset.Now:O} PERF {safeMetric}={Math.Max(0, elapsedMs)}{Environment.NewLine}");
    }

    public static void Diagnostic(string stage, string errorCode, string summary)
    {
        var safeStage = Sanitize(stage);
        var safeErrorCode = Sanitize(errorCode);
        var safeSummary = SanitizeSummary(summary);
        Enqueue($"{DateTimeOffset.Now:O} DIAGNOSTIC stage={safeStage} error_code={safeErrorCode} stderr_summary={safeSummary}{Environment.NewLine}");
    }

    private static void Write(string level, string value)
    {
        var safeValue = Sanitize(value);
        if (level == "ERROR")
        {
            AppState.LastErrorCode = safeValue;
        }

        Enqueue($"{DateTimeOffset.Now:O} {level} {safeValue}{Environment.NewLine}");
    }

    private static void Enqueue(string line)
    {
        try
        {
            EnsureWorker();
            Queue.Enqueue(line);
            Signal.Release();
        }
        catch
        {
            // Logging must never break the VPN client.
        }
    }

    private static void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(DrainAsync);
    }

    private static async Task DrainAsync()
    {
        while (true)
        {
            try
            {
                await Signal.WaitAsync();
                AppPaths.EnsureDirectories();
                RotateIfNeeded();

                var lines = new List<string>();
                while (Queue.TryDequeue(out var line) && lines.Count < 128)
                {
                    lines.Add(line);
                }

                if (lines.Count > 0)
                {
                    await File.AppendAllTextAsync(AppPaths.LogPath, string.Concat(lines));
                }
            }
            catch
            {
                // Logging must never break the VPN client.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var logPath = AppPaths.LogPath;
            if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogBytes)
            {
                return;
            }

            for (var index = MaxLogFiles - 1; index >= 1; index--)
            {
                var source = $"{logPath}.{index}";
                var target = $"{logPath}.{index + 1}";
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (File.Exists(source))
                {
                    File.Move(source, target);
                }
            }

            File.Move(logPath, $"{logPath}.1", overwrite: true);

            var stale = $"{logPath}.{MaxLogFiles + 1}";
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }
        catch
        {
            // Best effort rotation.
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
