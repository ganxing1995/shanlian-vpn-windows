using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class SubscriptionPage : Page
{
    private readonly PlanService _planService = new();
    private SubscriptionOrder? _currentOrder;

    public SubscriptionPage()
    {
        InitializeComponent();
        Render();
        _ = LoadPlansAsync();
    }

    private void Render()
    {
        var subscription = AppState.Subscription;
        PlanTextBlock.Text = subscription?.DisplayPlanName ?? "未订阅";
        StatusTextBlock.Text = subscription?.IsActive == true ? "有效" : "已过期";
        ExpiresTextBlock.Text = subscription?.ExpiresAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "--";
        DaysTextBlock.Text = $"{subscription?.RemainingDays ?? 0} 天";
        DeviceLimitTextBlock.Text = subscription?.DeviceLimit > 0 ? $"{subscription.DeviceLimit} 台" : "--";
        BoundDevicesTextBlock.Text = AppState.Devices.Count > 0
            ? $"{AppState.Devices.Count} 台"
            : $"{subscription?.BoundDevices ?? 0} 台";

        ExpiredTextBlock.Visibility = subscription?.IsActive == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadPlansAsync()
    {
        PlansStackPanel.Children.Clear();

        try
        {
            var plans = await _planService.GetPlansAsync();
            if (plans.Count == 0)
            {
                PlansStackPanel.Children.Add(MutedText("暂无可用套餐"));
                return;
            }

            foreach (var plan in plans)
            {
                PlansStackPanel.Children.Add(CreatePlanButton(plan));
            }
        }
        catch (ApiException ex)
        {
            PlansStackPanel.Children.Add(MutedText(ex.Message));
        }
        catch
        {
            PlansStackPanel.Children.Add(MutedText("套餐加载失败，请稍后重试"));
        }
    }

    private System.Windows.Controls.Button CreatePlanButton(Plan plan)
    {
        var currentPlan = AppState.Subscription?.DisplayPlanName ?? "";
        var isCurrent = currentPlan == plan.DisplayName;
        var action = isCurrent ? "续费" : "升级";
        var button = new System.Windows.Controls.Button
        {
            Content = $"{plan.DisplayName}  ¥{plan.Amount:0.##}    {action}",
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 236, 240)),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(16)
        };

        button.Click += async (_, _) => await CreateOrderAsync(plan, isCurrent ? "renew" : "upgrade");
        return button;
    }

    private async Task CreateOrderAsync(Plan plan, string type)
    {
        try
        {
            _currentOrder = await _planService.CreateOrderAsync(plan.Id, type);
            OrderNoTextBlock.Text = $"订单号：{_currentOrder.OrderNo}";
            OrderPlanTextBlock.Text = $"套餐名称：{(string.IsNullOrWhiteSpace(_currentOrder.PlanName) ? plan.DisplayName : _currentOrder.PlanName)}";
            OrderAmountTextBlock.Text = $"金额：¥{_currentOrder.Amount:0.##}";
            PayButton.Content = string.IsNullOrWhiteSpace(_currentOrder.PaymentUrl) ? "联系客服付款" : "去支付";
            OrderPanel.Visibility = Visibility.Visible;
        }
        catch (ApiException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static TextBlock MutedText(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 112, 133))
    };

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppState.Subscription = await new SubscriptionService().GetSubscriptionAsync();
            Render();
        }
        catch (ApiException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void PayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentOrder?.PaymentUrl))
        {
            Process.Start(new ProcessStartInfo(_currentOrder.PaymentUrl) { UseShellExecute = true });
            return;
        }

        System.Windows.MessageBox.Show("请联系客服完成付款", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}


