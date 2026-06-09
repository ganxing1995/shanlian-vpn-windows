namespace ShanlianVpn.Windows.Models;

public sealed class DeviceRegistrationResult
{
    public bool IsAllowed { get; set; } = true;
    public string Message { get; set; } = "";
}

