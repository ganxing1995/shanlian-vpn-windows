using System.Windows;
using System.Windows.Controls;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class SubscriptionPage : Page
{
    public SubscriptionPage()
    {
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        var subscription = AppState.Subscription;
        PlanTextBlock.Text = subscription?.PlanName ?? "未订阅";
        StatusTextBlock.Text = subscription?.IsActive == true ? "有效" : "已过期";
        ExpiresTextBlock.Text = subscription?.ExpiresAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "--";
        DaysTextBlock.Text = $"{subscription?.RemainingDays ?? 0} 天";
        DeviceLimitTextBlock.Text = subscription?.DeviceLimit > 0 ? $"{subscription.DeviceLimit} 台" : "--";
        BoundDevicesTextBlock.Text = AppState.Devices.Count > 0
            ? $"{AppState.Devices.Count} 台"
            : $"{subscription?.BoundDevices ?? 0} 台";

        ExpiredTextBlock.Visibility = subscription?.IsActive == true ? Visibility.Collapsed : Visibility.Visible;
    }
}

