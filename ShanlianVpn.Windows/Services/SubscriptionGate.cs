using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public static class SubscriptionGate
{
    public const string RequiredMessage = "需要有效订阅后才能连接。请先开通或续费套餐。";
    public const string UnverifiedMessage = "无法验证订阅，请检查网络后重试";

    public static bool CanConnect(Subscription? subscription) => subscription?.IsActive == true;

    public static string BlockerCode(Subscription? subscription)
    {
        if (subscription is null)
        {
            return "subscription_unverified";
        }

        return subscription.BlockerCode;
    }

    public static string BlockerMessage(Subscription? subscription)
    {
        if (subscription is null || subscription.AccessState == SubscriptionAccessState.Unknown)
        {
            return UnverifiedMessage;
        }

        return RequiredMessage;
    }

    public static string ConnectButtonText(Subscription? subscription)
    {
        if (subscription?.AccessState == SubscriptionAccessState.Unknown || subscription is null)
        {
            return "验证订阅后连接";
        }

        return subscription.IsActive ? "连接" : "开通后连接";
    }
}
