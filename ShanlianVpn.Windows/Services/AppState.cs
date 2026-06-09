using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public static class AppState
{
    public static User? CurrentUser { get; set; }
    public static Subscription? Subscription { get; set; }
    public static IReadOnlyList<Device> Devices { get; set; } = [];
    public static IReadOnlyList<VpnNode> Nodes { get; set; } = [];
    public static VpnNode? SelectedNode { get; set; }
    public static string StableDeviceId { get; set; } = "";
    public static bool DeviceAllowed { get; set; } = true;

    public static string DeviceShortCode =>
        StableDeviceId.Length <= 8 ? StableDeviceId : StableDeviceId[..8].ToUpperInvariant();
}

