using System.Text.Json.Serialization;

namespace ShanlianVpn.Windows.Models;

public sealed class Device
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";
}

