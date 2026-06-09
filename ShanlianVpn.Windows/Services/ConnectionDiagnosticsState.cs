using System.IO;
using System.Text.Json;

namespace ShanlianVpn.Windows.Services;

public static class ConnectionDiagnosticsState
{
    private static readonly object Lock = new();

    public static void Update(params (string Key, object? Value)[] values)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var state = ReadState();
            foreach (var (key, value) in values)
            {
                state[key] = value;
            }

            state["updated_at"] = DateTimeOffset.Now.ToString("O");
            lock (Lock)
            {
                File.WriteAllText(
                    AppPaths.ConnectionDiagnosticsPath,
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // Diagnostics must never interrupt VPN flow.
        }
    }

    private static Dictionary<string, object?> ReadState()
    {
        lock (Lock)
        {
            if (!File.Exists(AppPaths.ConnectionDiagnosticsPath))
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var json = File.ReadAllText(AppPaths.ConnectionDiagnosticsPath);
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                    ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
