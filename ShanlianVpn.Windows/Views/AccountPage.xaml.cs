using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class AccountPage : Page
{
    private readonly Action _logout;
    private readonly DeviceService _deviceService = new();

    public AccountPage(Action logout)
    {
        _logout = logout;
        InitializeComponent();
        EmailTextBlock.Text = AppState.CurrentUser?.Email ?? "--";
        VersionTextBlock.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        RenderDevices();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e) => _logout();

    private void RenderDevices()
    {
        DevicesStackPanel.Children.Clear();
        var limit = AppState.Subscription?.DeviceLimit ?? 0;
        DeviceSummaryTextBlock.Text = $"可用设备：{(limit > 0 ? $"{limit} 台" : "--")}    已绑定：{AppState.Devices.Count} / {(limit > 0 ? limit.ToString() : "--")}";

        var ordered = AppState.Devices
            .OrderByDescending(IsCurrentDevice)
            .ToList();

        if (ordered.Count == 0)
        {
            DevicesStackPanel.Children.Add(DeviceRow(new Device
            {
                DeviceName = "Windows PC",
                Platform = "Windows",
                DeviceId = AppState.StableDeviceId
            }, true));
            return;
        }

        foreach (var device in ordered)
        {
            DevicesStackPanel.Children.Add(DeviceRow(device, IsCurrentDevice(device)));
        }
    }

    private UIElement DeviceRow(Device device, bool isCurrent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = $"{(isCurrent ? "本设备" : "其他设备")}  {device.ShortCode}",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 248, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(text);

        if (!isCurrent)
        {
            var button = new System.Windows.Controls.Button { Content = "移除", Padding = new Thickness(12, 4, 12, 4) };
            button.Click += async (_, _) => await RemoveDeviceAsync(device);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return grid;
    }

    private bool IsCurrentDevice(Device device) =>
        !string.IsNullOrWhiteSpace(device.DeviceId)
        && device.DeviceId.Equals(AppState.StableDeviceId, StringComparison.OrdinalIgnoreCase);

    private async Task RemoveDeviceAsync(Device device)
    {
        if (System.Windows.MessageBox.Show("确认移除此设备？", "闪连 VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _deviceService.RemoveDeviceAsync(device.Id);
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId);
            System.Windows.MessageBox.Show("设备已移除", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            RenderDevices();
        }
        catch (ApiException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(DiagnosticsService.BuildSafeDiagnostics());
        System.Windows.MessageBox.Show("安全诊断信息已复制", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
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
}


