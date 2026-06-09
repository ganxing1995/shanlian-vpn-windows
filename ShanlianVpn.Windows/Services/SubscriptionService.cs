using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class SubscriptionService
{
    private readonly ApiClient _api = new();

    public async Task<Subscription> GetSubscriptionAsync()
    {
        var response = await _api.GetAsync("/api/subscription");
        var subscription = new Subscription
        {
            PlanName = JsonHelpers.GetString(response, "plan_name", "plan", "package_name"),
            Status = JsonHelpers.GetString(response, "status", "subscription_status"),
            ExpiresAt = JsonHelpers.GetDateTimeOffset(response, "expires_at", "expired_at", "expire_at", "end_at"),
            DeviceLimit = JsonHelpers.GetInt(response, "device_limit", "max_devices", "devices_limit"),
            BoundDevices = JsonHelpers.GetInt(response, "bound_devices", "used_devices", "devices_count", "device_count"),
            CanUse = JsonHelpers.TryGetProperty(response, "can_use", out _) || JsonHelpers.TryGetProperty(response, "is_valid", out _)
                ? JsonHelpers.GetBool(response, false, "can_use", "is_valid")
                : null
        };

        SafeLogger.Info("subscription_loaded");
        return subscription;
    }
}
