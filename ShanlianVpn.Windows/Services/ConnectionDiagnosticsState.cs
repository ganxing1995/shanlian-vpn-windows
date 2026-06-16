using System.IO;
using System.Text.Json;

namespace ShanlianVpn.Windows.Services;

public static class ConnectionDiagnosticsState
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, object?> State = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static bool _dirty;
    private static int _flushScheduled;

    public static void Update(params (string Key, object? Value)[] values)
    {
        try
        {
            lock (Lock)
            {
                EnsureLoaded();
                foreach (var (key, value) in values)
                {
                    State[key] = value;
                }

                State["updated_at"] = DateTimeOffset.Now.ToString("O");
                _dirty = true;
            }

            ScheduleFlush();
        }
        catch
        {
            // Diagnostics must never interrupt VPN flow.
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(AppPaths.ConnectionDiagnosticsPath))
            {
                return;
            }

            var json = File.ReadAllText(AppPaths.ConnectionDiagnosticsPath);
            var existing = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            if (existing is null)
            {
                return;
            }

            foreach (var item in existing)
            {
                State[item.Key] = item.Value;
            }
        }
        catch
        {
            State.Clear();
        }
    }

    private static void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(FlushAsync);
    }

    private static async Task FlushAsync()
    {
        try
        {
            await Task.Delay(150);
            Dictionary<string, object?> snapshot;
            lock (Lock)
            {
                _dirty = false;
                snapshot = new Dictionary<string, object?>(State, StringComparer.OrdinalIgnoreCase);
            }

            AppPaths.EnsureDirectories();
            await File.WriteAllTextAsync(
                AppPaths.ConnectionDiagnosticsPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must never interrupt VPN flow.
        }
        finally
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            var shouldFlushAgain = false;
            lock (Lock)
            {
                shouldFlushAgain = _dirty;
            }

            if (shouldFlushAgain)
            {
                ScheduleFlush();
            }
        }
    }
}
