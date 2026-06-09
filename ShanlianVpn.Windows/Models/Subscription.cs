using System.Text.Json.Serialization;

namespace ShanlianVpn.Windows.Models;

public sealed class Subscription
{
    [JsonPropertyName("plan_name")]
    public string PlanName { get; set; } = "未订阅";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("device_limit")]
    public int DeviceLimit { get; set; }

    [JsonPropertyName("bound_devices")]
    public int BoundDevices { get; set; }

    [JsonPropertyName("can_use")]
    public bool? CanUse { get; set; }

    public bool IsActive
    {
        get
        {
            if (CanUse == false)
            {
                return false;
            }

            if (ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.Now)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(Status) && ExpiresAt is not null)
            {
                return true;
            }

            return Status.Equals("active", StringComparison.OrdinalIgnoreCase)
                || Status.Equals("valid", StringComparison.OrdinalIgnoreCase)
                || Status.Equals("paid", StringComparison.OrdinalIgnoreCase)
                || CanUse == true;
        }
    }

    public int RemainingDays =>
        ExpiresAt is null ? 0 : Math.Max(0, (int)Math.Ceiling((ExpiresAt.Value - DateTimeOffset.Now).TotalDays));

    public string DisplayPlanName => PlanName.ToLowerInvariant() switch
    {
        var name when name.Contains("weekly") || name.Contains("week") => "周套餐",
        var name when name.Contains("monthly") || name.Contains("month") => "月套餐",
        var name when name.Contains("yearly") || name.Contains("year") => "年度套餐",
        _ => string.IsNullOrWhiteSpace(PlanName) ? "未订阅" : PlanName
    };
}
