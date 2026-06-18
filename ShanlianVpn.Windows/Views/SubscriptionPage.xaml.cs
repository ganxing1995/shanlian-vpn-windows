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
        StatusTextBlock.Text = subscription?.StatusDisplay ?? "无法验证订阅";
        ExpiresTextBlock.Text = subscription?.ExpiresDisplay ?? "-";
        DaysTextBlock.Text = subscription?.RemainingDaysDisplay ?? "-";
        DeviceLimitTextBlock.Text = subscription?.DeviceLimit > 0 ? $"{subscription.DeviceLimit} 台" : "--";
        BoundDevicesTextBlock.Text = AppState.Devices.Count > 0
            ? $"{AppState.Devices.Count} 台"
            : $"{subscription?.BoundDevices ?? 0} 台";

        ExpiredTextBlock.Text = SubscriptionGate.BlockerMessage(subscription);
        ExpiredTextBlock.Visibility = SubscriptionGate.CanConnect(subscription) ? Visibility.Collapsed : Visibility.Visible;
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
        var currentPlan = Subscription.NormalizePlanName(AppState.Subscription?.PlanName ?? "");
        var isSubscriptionActive = AppState.Subscription?.AccessState == SubscriptionAccessState.Active;
        var isCurrent = isSubscriptionActive && string.Equals(currentPlan, plan.DisplayName, StringComparison.Ordinal);
        var action = !isSubscriptionActive
            ? "立即开通"
            : isCurrent ? "续费" : "升级";
        var orderType = isCurrent ? "renew" : "upgrade";

        var titleText = new TextBlock
        {
            Text = plan.DisplayName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.SetResourceReference(ForegroundProperty, "BrightTextBrush");

        var priceText = new TextBlock
        {
            Text = plan.PriceDisplay,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        priceText.SetResourceReference(ForegroundProperty, "BrightTextBrush");

        var actionText = new TextBlock
        {
            Text = action,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        actionText.SetResourceReference(ForegroundProperty, "ModeBadgeTextBrush");

        var leftStack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftStack.Children.Add(titleText);
        leftStack.Children.Add(priceText);

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.Children.Add(leftStack);
        Grid.SetColumn(actionText, 1);
        contentGrid.Children.Add(actionText);

        var button = new System.Windows.Controls.Button
        {
            Content = contentGrid,
            Style = (Style)FindResource("PlanOptionButtonStyle")
        };

        if (isCurrent)
        {
            button.SetResourceReference(BackgroundProperty, "ModeBadgeBrush");
            button.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "ModeBadgeBorderBrush");
        }

        button.Click += async (_, _) => await CreateOrderAsync(plan, orderType);
        return button;
    }

    private async Task CreateOrderAsync(Plan plan, string type)
    {
        try
        {
            _currentOrder = await _planService.CreateOrderAsync(plan.Id, type);
            var orderPlanName = Subscription.NormalizePlanName(_currentOrder.PlanName);
            OrderNoTextBlock.Text = $"订单号：{_currentOrder.OrderNo}";
            OrderPlanTextBlock.Text = $"套餐名称：{(string.IsNullOrWhiteSpace(orderPlanName) ? plan.DisplayName : orderPlanName)}";
            OrderAmountTextBlock.Text = $"金额：{plan.PriceDisplay}";
            PayButton.Content = string.IsNullOrWhiteSpace(_currentOrder.PaymentUrl) ? "联系客服付款" : "前往支付";
            OrderPanel.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(_currentOrder.PaymentUrl))
            {
                Process.Start(new ProcessStartInfo(_currentOrder.PaymentUrl) { UseShellExecute = true });
                System.Windows.MessageBox.Show("购买页面已在浏览器中打开。", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (ApiException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static TextBlock MutedText(string text) => new()
    {
        Text = text,
        Foreground = System.Windows.Application.Current.TryFindResource("SoftTextBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(168, 179, 199))
    };

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        object? originalContent = null;

        if (button is not null)
        {
            originalContent = button.Content;
            button.IsEnabled = false;
            button.Content = "刷新中...";
        }

        try
        {
            AppState.Subscription = await new SubscriptionService().GetSubscriptionAsync(forceRefresh: true);
            Render();
            await LoadPlansAsync();
        }
        catch (ApiException ex)
        {
            AppState.Subscription = null;
            Render();
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
                button.Content = originalContent ?? "刷新订阅状态";
            }
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


