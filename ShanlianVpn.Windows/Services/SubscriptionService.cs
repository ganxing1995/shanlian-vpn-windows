using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class SubscriptionService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static Subscription? _cachedSubscription;
    private static DateTimeOffset _cachedAt;
    private readonly ApiClient _api = new();

    public async Task<Subscription> GetSubscriptionAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedSubscription is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
        {
            return _cachedSubscription;
        }

        await CacheLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedSubscription is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
            {
                return _cachedSubscription;
            }

            var response = await _api.GetAsync("/api/subscription");
            var subscription = new Subscription
            {
                PlanName = GetPlanName(response),
                Status = JsonHelpers.GetString(response, "status", "subscription_status", "state"),
                ExpiresAt = JsonHelpers.GetDateTimeOffset(response, "expires_at", "expired_at", "expire_at", "end_at"),
                DeviceLimit = JsonHelpers.GetInt(response, "device_limit", "max_devices", "devices_limit"),
                BoundDevices = JsonHelpers.GetInt(response, "bound_devices", "used_devices", "devices_count", "device_count"),
                CanUse = HasBooleanAccessFlag(response)
                    ? JsonHelpers.GetBool(response, false, "can_use", "is_valid", "is_active", "active")
                    : null
            };

            SafeLogger.Info("subscription_loaded");
            _cachedSubscription = subscription;
            _cachedAt = DateTimeOffset.UtcNow;
            return subscription;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static bool HasBooleanAccessFlag(System.Text.Json.JsonElement response) =>
        JsonHelpers.TryGetProperty(response, "can_use", out _)
        || JsonHelpers.TryGetProperty(response, "is_valid", out _)
        || JsonHelpers.TryGetProperty(response, "is_active", out _)
        || JsonHelpers.TryGetProperty(response, "active", out _);

    private static string GetPlanName(System.Text.Json.JsonElement response)
    {
        var direct = JsonHelpers.GetString(response, "plan_name", "package_name");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (JsonHelpers.TryGetProperty(response, "plan", out var plan))
        {
            var nested = JsonHelpers.GetString(plan, "code", "name", "title", "plan_name", "type");
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }

            return JsonHelpers.GetString(response, "plan");
        }

        if (JsonHelpers.TryGetProperty(response, "subscription", out var subscription))
        {
            var nestedDirect = JsonHelpers.GetString(subscription, "plan_name", "package_name");
            if (!string.IsNullOrWhiteSpace(nestedDirect))
            {
                return nestedDirect;
            }

            if (JsonHelpers.TryGetProperty(subscription, "plan", out var nestedPlan))
            {
                var nested = JsonHelpers.GetString(nestedPlan, "code", "name", "title", "plan_name", "type");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return "";
    }
}
