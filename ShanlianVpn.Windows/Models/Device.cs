using System.Text.Json.Serialization;

namespace ShanlianVpn.Windows.Models;

public sealed class Device
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("device_id_masked")]
    public string DeviceIdMasked { get; set; } = "";

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("last_active_at")]
    public string LastActiveAtRaw { get; set; } = "";

    [JsonPropertyName("is_current")]
    public bool IsCurrent { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    public string ShortCode
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DeviceIdMasked))
            {
                return DeviceIdMasked;
            }

            if (string.IsNullOrWhiteSpace(DeviceId))
            {
                return "****----";
            }

            return DeviceId.Length <= 4 ? $"****{DeviceId}" : $"****{DeviceId[^4..]}";
        }
    }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(DeviceName) ? DeviceName : string.IsNullOrWhiteSpace(Model) ? "Windows 设备" : Model;

    public string DisplayPlatform =>
        !string.IsNullOrWhiteSpace(Platform) ? Platform : "Windows";

    public string LastActiveDisplay
    {
        get
        {
            if (DateTimeOffset.TryParse(LastActiveAtRaw, out var lastActive))
            {
                return lastActive.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            }

            return "最近活跃时间未知";
        }
    }
}
