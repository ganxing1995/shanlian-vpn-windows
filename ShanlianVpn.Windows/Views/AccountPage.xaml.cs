using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;

namespace ShanlianVpn.Windows.Views;

public partial class AccountPage : Page
{
    private readonly Action _logout;
    private readonly Action _loginAnother;
    private readonly AuthService _authService = new();
    private readonly DeviceService _deviceService = new();

    public AccountPage(Action logout, Action loginAnother)
    {
        _logout = logout;
        _loginAnother = loginAnother;
        InitializeComponent();
        RenderAccountHeader();
        RenderDevices();
        _ = LoadAccountAsync();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        const string message = "确定要退出当前账号吗？退出后需要重新登录才能继续使用。";
        if (System.Windows.MessageBox.Show(message, "闪连 VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _logout();
    }

    private async Task LoadAccountAsync()
    {
        if (!TokenStore.HasToken())
        {
            return;
        }

        try
        {
            AppState.CurrentUser = await _authService.GetUserAsync();
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId, forceRefresh: true);
        }
        catch
        {
            // Keep the last known state on screen.
        }

        RenderAccountHeader();
        RenderDevices();
    }

    private void RenderAccountHeader()
    {
        LoginStateTextBlock.Text = TokenStore.HasToken() ? "已登录" : "未登录";
        EmailTextBlock.Text = FormatAccountLabel(AppState.CurrentUser);
    }

    private void RenderDevices()
    {
        DevicesStackPanel.Children.Clear();

        var limit = AppState.Subscription?.DeviceLimit ?? 0;
        var limitText = limit > 0 ? $"{limit} 台" : "--";
        DeviceSummaryTextBlock.Text = $"可用设备：{limitText}    已绑定：{AppState.Devices.Count} / {limitText}";

        var ordered = AppState.Devices
            .OrderByDescending(device => device.IsCurrent)
            .ThenByDescending(device => device.LastActiveAtRaw)
            .ToList();

        var currentDevice = ordered.FirstOrDefault(IsCurrentDevice);
        var otherDevices = ordered.Where(device => !IsCurrentDevice(device)).ToList();

        if (currentDevice is not null)
        {
            DevicesStackPanel.Children.Add(DeviceSectionTitle("本机设备"));
            DevicesStackPanel.Children.Add(DeviceCard(currentDevice, isCurrent: true));
        }

        DevicesStackPanel.Children.Add(DeviceSectionTitle("其他设备", topMargin: currentDevice is null ? 0 : 10));
        if (otherDevices.Count == 0)
        {
            DevicesStackPanel.Children.Add(EmptyDeviceText("暂无其他设备"));
            return;
        }

        foreach (var device in otherDevices)
        {
            DevicesStackPanel.Children.Add(DeviceCard(device, isCurrent: false));
        }
    }

    private TextBlock DeviceSectionTitle(string text, double topMargin = 0) => new()
    {
        Text = text,
        Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, topMargin, 0, 8)
    };

    private TextBlock EmptyDeviceText(string text) => new()
    {
        Text = text,
        Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
        Margin = new Thickness(0, 0, 0, 10)
    };

    private UIElement DeviceCard(Device device, bool isCurrent)
    {
        var border = new Border
        {
            Background = ResourceBrush("CardBrush", 21, 36, 60),
            BorderBrush = ResourceBrush("CardBorderBrush", 36, 52, 79),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = device.DisplayName,
            Foreground = ResourceBrush("BrightTextBrush", 245, 248, 255),
            FontWeight = FontWeights.SemiBold
        });
        textStack.Children.Add(new TextBlock
        {
            Text = $"{device.DisplayPlatform}  {device.ShortCode}",
            Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
            Margin = new Thickness(0, 6, 0, 0)
        });
        textStack.Children.Add(new TextBlock
        {
            Text = isCurrent ? "本机设备不能在当前设备移除" : $"最近活跃：{device.LastActiveDisplay}",
            Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0)
        });
        grid.Children.Add(textStack);

        if (isCurrent)
        {
            var badge = new Border
            {
                Background = ResourceBrush("ModeBadgeBrush", 23, 43, 72),
                BorderBrush = ResourceBrush("ModeBadgeBorderBrush", 44, 74, 118),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "本机",
                    Foreground = ResourceBrush("ModeBadgeTextBrush", 88, 166, 255),
                    FontWeight = FontWeights.SemiBold
                }
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }
        else
        {
            var button = new WpfButton
            {
                Content = "移除设备",
                Padding = new Thickness(12, 6, 12, 6),
                Background = ResourceBrush("AccentBrush", 27, 110, 243),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = ResourceBrush("AccentBrush", 27, 110, 243),
                BorderThickness = new Thickness(1)
            };
            button.Click += async (_, _) => await RemoveDeviceAsync(device);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        border.Child = grid;
        return border;
    }

    private bool IsCurrentDevice(Device device) =>
        device.IsCurrent
        || (!string.IsNullOrWhiteSpace(device.DeviceId)
            && device.DeviceId.Equals(AppState.StableDeviceId, StringComparison.OrdinalIgnoreCase));

    private async Task RemoveDeviceAsync(Device device)
    {
        const string message = "确定要移除此设备吗？移除后该设备将无法继续使用 VPN。";
        if (System.Windows.MessageBox.Show(message, "闪连 VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _deviceService.RemoveDeviceAsync(device.Id);
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId, forceRefresh: true);
            System.Windows.MessageBox.Show("设备已移除。", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            RenderDevices();
        }
        catch (ApiException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        new ChangePasswordWindow(() =>
        {
            TokenStore.Clear();
            _logout();
        })
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private static string FormatAccountLabel(User? user)
    {
        if (user is null)
        {
            return "--";
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            return user.Email;
        }

        return string.IsNullOrWhiteSpace(user.Name) ? "--" : user.Name;
    }

    private static SolidColorBrush ResourceBrush(string key, byte r, byte g, byte b) =>
        System.Windows.Application.Current.TryFindResource(key) as SolidColorBrush
        ?? new SolidColorBrush(WpfColor.FromRgb(r, g, b));
}
