using System.Text.Json.Serialization;

namespace ShanlianVpn.Windows.Models;

public sealed class Subscription
{
    [JsonPropertyName("plan_name")]
    public string PlanName { get; set; } = "";

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

    public SubscriptionAccessState AccessState
    {
        get
        {
            if (CanUse == false)
            {
                return IsExpiredStatus ? SubscriptionAccessState.Expired : SubscriptionAccessState.None;
            }

            if (ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.Now)
            {
                return SubscriptionAccessState.Expired;
            }

            if (CanUse == true)
            {
                return SubscriptionAccessState.Active;
            }

            if (IsActiveStatus)
            {
                return SubscriptionAccessState.Active;
            }

            if (IsExpiredStatus)
            {
                return SubscriptionAccessState.Expired;
            }

            if (IsNoneStatus)
            {
                return SubscriptionAccessState.None;
            }

            if (string.IsNullOrWhiteSpace(Status) && ExpiresAt is not null)
            {
                return SubscriptionAccessState.Active;
            }

            return SubscriptionAccessState.Unknown;
        }
    }

    public bool IsActive => AccessState == SubscriptionAccessState.Active;

    public int RemainingDays =>
        ExpiresAt is null ? 0 : Math.Max(0, (int)Math.Ceiling((ExpiresAt.Value - DateTimeOffset.Now).TotalDays));

    public string DisplayPlanName
    {
        get
        {
            var normalized = NormalizePlanName(PlanName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return AccessState == SubscriptionAccessState.Active ? "有效套餐" : "未订阅";
        }
    }

    public string StatusDisplay => AccessState switch
    {
        SubscriptionAccessState.Active => "有效",
        SubscriptionAccessState.Expired => "已过期",
        SubscriptionAccessState.None => "未订阅",
        _ => "无法验证订阅"
    };

    public string ExpiresDisplay => ExpiresAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string RemainingDaysDisplay => AccessState switch
    {
        SubscriptionAccessState.Active => $"{RemainingDays} 天",
        SubscriptionAccessState.Expired => "0 天",
        _ => "-"
    };

    public string BlockerCode => AccessState switch
    {
        SubscriptionAccessState.Expired => "subscription_expired",
        SubscriptionAccessState.None => "subscription_required",
        _ => "subscription_unverified"
    };

    public string BlockerMessage => AccessState switch
    {
        SubscriptionAccessState.Unknown => "无法验证订阅，请检查网络后重试",
        _ => "需要有效订阅后才能连接。请先开通或续费套餐。"
    };

    public static string NormalizePlanName(string planName)
    {
        var name = planName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return name.ToLowerInvariant() switch
        {
            var value when value.Contains("weekly") || value.Contains("week") || value.Contains("周") => "周套餐",
            var value when value.Contains("monthly") || value.Contains("month") || value.Contains("月") => "月套餐",
            var value when value.Contains("yearly") || value.Contains("annual") || value.Contains("year") || value.Contains("年") => "年度套餐",
            var value when value.Contains("none") || value.Contains("unsubscribed") || value.Contains("未订阅") => "",
            _ => name
        };
    }

    private bool IsActiveStatus => Status.Equals("active", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("valid", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("paid", StringComparison.OrdinalIgnoreCase);

    private bool IsExpiredStatus => Status.Equals("expired", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("inactive", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("ended", StringComparison.OrdinalIgnoreCase);

    private bool IsNoneStatus => string.IsNullOrWhiteSpace(Status)
        || Status.Equals("none", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("no_subscription", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("unsubscribed", StringComparison.OrdinalIgnoreCase);
}

public enum SubscriptionAccessState
{
    Active,
    None,
    Expired,
    Unknown
}
