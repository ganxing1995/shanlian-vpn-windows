using System.Reflection;
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
    private readonly DeviceService _deviceService = new();

    public AccountPage(Action logout, Action loginAnother)
    {
        _logout = logout;
        _loginAnother = loginAnother;
        InitializeComponent();
        EmailTextBlock.Text = MaskEmail(AppState.CurrentUser?.Email);
        LoginStateTextBlock.Text = TokenStore.HasToken() ? "已登录" : "未登录";
        VersionTextBlock.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        RenderDevices();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e) => _logout();

    private void LoginAnotherButton_Click(object sender, RoutedEventArgs e) => _loginAnother();

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        new RegisterWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void RenderDevices()
    {
        DevicesStackPanel.Children.Clear();
        var limit = AppState.Subscription?.DeviceLimit ?? 0;
        DeviceSummaryTextBlock.Text = $"可用设备：{(limit > 0 ? $"{limit} 台" : "--")}    已绑定：{AppState.Devices.Count} / {(limit > 0 ? limit.ToString() : "--")}";

        var ordered = AppState.Devices.ToList();
        var currentDevice = ordered.FirstOrDefault(IsCurrentDevice) ?? new Device
        {
            DeviceName = "当前 Windows 设备",
            Platform = "Windows",
            DeviceId = AppState.StableDeviceId
        };
        var otherDevices = ordered
            .Where(device => !IsCurrentDevice(device))
            .ToList();

        DevicesStackPanel.Children.Add(DeviceSectionTitle("本机设备"));
        DevicesStackPanel.Children.Add(DeviceCard(currentDevice, isCurrent: true));

        DevicesStackPanel.Children.Add(DeviceSectionTitle("其他设备", topMargin: 10));
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

    private static TextBlock DeviceSectionTitle(string text, double topMargin = 0) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(WpfColor.FromRgb(168, 179, 199)),
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, topMargin, 0, 8)
    };

    private static TextBlock EmptyDeviceText(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(WpfColor.FromRgb(168, 179, 199)),
        Margin = new Thickness(0, 0, 0, 10)
    };

    private UIElement DeviceCard(Device device, bool isCurrent)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(21, 36, 60)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(36, 52, 79)),
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
            Foreground = new SolidColorBrush(WpfColor.FromRgb(245, 248, 255)),
            FontWeight = FontWeights.SemiBold
        });
        textStack.Children.Add(new TextBlock
        {
            Text = $"{device.DisplayPlatform}  {device.ShortCode}",
            Foreground = new SolidColorBrush(WpfColor.FromRgb(168, 179, 199)),
            Margin = new Thickness(0, 6, 0, 0)
        });
        textStack.Children.Add(new TextBlock
        {
            Text = isCurrent ? "本机设备不可在当前设备移除" : $"最近活跃：{device.LastActiveDisplay}",
            Foreground = new SolidColorBrush(WpfColor.FromRgb(168, 179, 199)),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0)
        });
        grid.Children.Add(textStack);

        if (isCurrent)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(23, 43, 72)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(44, 74, 118)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "本机",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(88, 166, 255)),
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
                Background = new SolidColorBrush(WpfColor.FromRgb(30, 51, 84)),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(245, 248, 255)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(49, 85, 126)),
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
        !string.IsNullOrWhiteSpace(device.DeviceId)
        && device.DeviceId.Equals(AppState.StableDeviceId, StringComparison.OrdinalIgnoreCase);

    private async Task RemoveDeviceAsync(Device device)
    {
        var message = "确定要移除此设备吗？移除后该设备将无法继续使用 VPN。";
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

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return "--";
        }

        var parts = email.Split('@', 2);
        var name = parts[0];
        if (name.Length <= 2)
        {
            return $"*{name[^1]}@{parts[1]}";
        }

        return $"{name[0]}***{name[^1]}@{parts[1]}";
    }
}
