using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ShanlianVpn.Windows.Services;

public static class SystemProxyService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private static string StatePath => Path.Combine(AppPaths.AppDataDirectory, "system-proxy-state.json");

    public static void EnableLocalProxy(int port)
    {
        AppPaths.EnsureDirectories();
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

        var targetProxy = $"127.0.0.1:{port}";
        if (!IsUsingAppProxy(key, targetProxy))
        {
            var state = new ProxyState(
                key.GetValue("ProxyEnable"),
                key.GetValue("ProxyServer"),
                key.GetValue("ProxyOverride"));
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", targetProxy, RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        Refresh();
        SafeLogger.Info("system_proxy_enabled_by_app");
    }

    public static void Restore()
    {
        if (!File.Exists(StatePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ProxyState>(File.ReadAllText(StatePath));
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

            if (!IsUsingAppProxy(key, "127.0.0.1:20809"))
            {
                return;
            }

            RestoreValue(key, "ProxyEnable", state?.ProxyEnable, RegistryValueKind.DWord);
            RestoreValue(key, "ProxyServer", state?.ProxyServer, RegistryValueKind.String);
            RestoreValue(key, "ProxyOverride", state?.ProxyOverride, RegistryValueKind.String);
            Refresh();
            SafeLogger.Info("system_proxy_restored");
        }
        catch
        {
            SafeLogger.Error("system_proxy_restore_failed");
        }
        finally
        {
            try
            {
                File.Delete(StatePath);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static void RestoreValue(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        if (kind == RegistryValueKind.DWord && value is JsonElement element && element.ValueKind == JsonValueKind.Number)
        {
            key.SetValue(name, element.GetInt32(), kind);
            return;
        }

        key.SetValue(name, value.ToString() ?? string.Empty, kind);
    }

    private static bool IsUsingAppProxy(RegistryKey key, string targetProxy)
    {
        var proxyServer = key.GetValue("ProxyServer")?.ToString() ?? string.Empty;
        return string.Equals(proxyServer, targetProxy, StringComparison.OrdinalIgnoreCase);
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private sealed record ProxyState(object? ProxyEnable, object? ProxyServer, object? ProxyOverride);
}
