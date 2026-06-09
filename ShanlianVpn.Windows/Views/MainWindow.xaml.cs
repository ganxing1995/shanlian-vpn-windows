using System.Windows;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class MainWindow : Window
{
    private readonly AuthService _authService = new();
    private readonly SubscriptionService _subscriptionService = new();
    private readonly DeviceService _deviceService = new();
    private readonly NodeService _nodeService = new();
    private readonly WindowsDeviceIdProvider _deviceIdProvider = new();
    private readonly ConfigBuilder _configBuilder = new();
    private readonly SingBoxService _singBoxService = new();
    private readonly ConnectivityHealthCheck _healthCheck = new();
    private bool _isConnected;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshRemoteStateAsync();
        ShowHome();
    }

    private async Task RefreshRemoteStateAsync()
    {
        try
        {
            AppState.StableDeviceId = _deviceIdProvider.GetStableDeviceId();
            AppState.CurrentUser ??= await _authService.GetUserAsync();
            AppState.Subscription = await _subscriptionService.GetSubscriptionAsync();

            var deviceResult = await _deviceService.RegisterWindowsDeviceAsync(AppState.StableDeviceId);
            AppState.DeviceAllowed = deviceResult.IsAllowed;
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId);

            AppState.Nodes = await _nodeService.GetNodesAsync();
            AppState.SelectedNode ??= AppState.Nodes.FirstOrDefault();

            MessageTextBlock.Text = !IsDeviceLimitExceeded()
                ? "网络已准备"
                : "设备数量已达上限，请先在手机端或后台移除旧设备。";
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            TokenStore.Clear();
            MessageBox.Show("登录已过期，请重新登录", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            new LoginWindow().Show();
            Close();
        }
        catch (ApiException ex)
        {
            MessageTextBlock.Text = ex.Message;
            SetStatus("网络异常");
        }
        catch
        {
            MessageTextBlock.Text = "网络错误，请稍后重试";
            SetStatus("网络异常");
        }
        finally
        {
            UpdateHome();
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnected)
        {
            Disconnect();
            return;
        }

        if (AppState.Subscription?.IsActive != true)
        {
            MessageTextBlock.Text = "订阅已过期，请续费后使用。";
            ShowSubscription();
            return;
        }

        if (IsDeviceLimitExceeded())
        {
            MessageTextBlock.Text = "设备数量已达上限，请先在手机端或后台移除旧设备。";
            return;
        }

        if (AppState.SelectedNode is null)
        {
            MessageTextBlock.Text = "请先选择线路";
            ShowNodes();
            return;
        }

        if (!SingBoxService.IsAdministrator())
        {
            SetStatus("未连接");
            MessageTextBlock.Text = "请以管理员身份运行闪连 VPN";
            AdminRestartButton.Visibility = Visibility.Visible;
            SafeLogger.Error("not_admin");
            return;
        }

        AdminRestartButton.Visibility = Visibility.Collapsed;
        SetStatus("正在连接");
        ToggleConnectButtons(false);

        try
        {
            var nodeConfig = await _nodeService.GetNodeConfigAsync(AppState.SelectedNode.Id);
            var configPath = _configBuilder.BuildRuntimeConfig(nodeConfig);
            await _singBoxService.StartAsync(configPath);

            var health = await _healthCheck.CheckAsync();
            _isConnected = true;

            switch (health)
            {
                case HealthCheckResult.Success:
                    SetStatus("已连接");
                    MessageTextBlock.Text = "网络已连接";
                    break;
                case HealthCheckResult.DnsFailed:
                    SetStatus("网络异常");
                    MessageTextBlock.Text = "VPN 已启动，但网络异常";
                    break;
                default:
                    SetStatus("网络异常");
                    MessageTextBlock.Text = "VPN 已启动，但网络异常";
                    break;
            }
        }
        catch (ApiException ex)
        {
            SetStatus("未连接");
            MessageTextBlock.Text = ex.Message;
            _singBoxService.Stop();
        }
        catch
        {
            SetStatus("未连接");
            MessageTextBlock.Text = "线路连接失败，请切换线路重试";
            _singBoxService.Stop();
        }
        finally
        {
            ToggleConnectButtons(true);
            UpdateHome();
        }
    }

    private void Disconnect()
    {
        _singBoxService.Stop();
        _isConnected = false;
        SetStatus("未连接");
        MessageTextBlock.Text = "网络已准备";
        UpdateHome();
    }

    private void UpdateHome()
    {
        SelectedNodeTextBlock.Text = AppState.SelectedNode?.DisplayCountry ?? "未选择";

        if (AppState.Subscription is { IsActive: false })
        {
            CircleButton.Content = "续费后连接";
            SecondaryConnectButton.Content = "续费后连接";
            return;
        }

        CircleButton.Content = _isConnected ? "断开" : "连接";
        SecondaryConnectButton.Content = _isConnected ? "断开连接" : "连接";
    }

    private void AdminRestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsElevationService.RestartAsAdministrator())
        {
            Close();
            return;
        }

        MessageTextBlock.Text = "无法自动重启，请以管理员身份运行闪连 VPN";
    }

    private static bool IsDeviceLimitExceeded()
    {
        if (!AppState.DeviceAllowed)
        {
            return true;
        }

        var limit = AppState.Subscription?.DeviceLimit ?? 0;
        if (limit <= 0)
        {
            return false;
        }

        var used = AppState.Devices.Count > 0 ? AppState.Devices.Count : AppState.Subscription?.BoundDevices ?? 0;
        return used > limit;
    }

    private void SetStatus(string status)
    {
        StatusTextBlock.Text = status;
        TopStatusTextBlock.Text = status;
    }

    private void ToggleConnectButtons(bool enabled)
    {
        CircleButton.IsEnabled = enabled;
        SecondaryConnectButton.IsEnabled = enabled;
    }

    private void ShowHome()
    {
        ContentFrame.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        UpdateHome();
    }

    private void ShowNodes()
    {
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new NodesPage(UpdateHome);
    }

    private void ShowSubscription()
    {
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new SubscriptionPage();
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void NodesNav_Click(object sender, RoutedEventArgs e) => ShowNodes();
    private void SubscriptionNav_Click(object sender, RoutedEventArgs e) => ShowSubscription();

    private void AccountNav_Click(object sender, RoutedEventArgs e)
    {
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new AccountPage(() =>
        {
            Disconnect();
            _authService.Logout();
            new LoginWindow().Show();
            Close();
        });
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_singBoxService.IsRunning)
        {
            _singBoxService.Stop();
        }
    }
}
