using System.IO;
using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public static class UserSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static UserSettings? _current;

    public static UserSettings Current => _current ??= Load();

    public static UserSettings Load()
    {
        AppPaths.EnsureDirectories();
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                return new UserSettings();
            }

            var json = File.ReadAllText(AppPaths.SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public static void Save()
    {
        Save(Current);
    }

    public static void Save(UserSettings settings)
    {
        AppPaths.EnsureDirectories();
        _current = settings;
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AppPaths.SettingsPath, json);
    }
}
