namespace ShanlianVpn.Windows.Models;

public sealed class UserSettings
{
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Global;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;
    public bool LaunchOnStartup { get; set; }
    public bool AutoConnect { get; set; }
}

public enum ConnectionMode
{
    Speed,
    Global
}

public enum ThemeMode
{
    Dark,
    Light,
    System
}
