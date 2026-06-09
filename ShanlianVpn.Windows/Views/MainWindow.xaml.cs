using System.Windows;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;
using Forms = System.Windows.Forms;

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
    private Forms.NotifyIcon? _notifyIcon;
    private bool _allowExit;
    private bool _isConnected;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTray();
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
            System.Windows.MessageBox.Show("登录已过期，请重新登录", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
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
        SafeLogger.Info("connect_clicked");

        if (_isConnected)
        {
            Disconnect();
            return;
        }

        AdminRestartButton.Visibility = Visibility.Collapsed;
        SetStatus("正在连接");
        MessageTextBlock.Text = "正在建立安全连接";
        ToggleConnectButtons(false);

        try
        {
            StageStart("auth_check");
            if (!TokenStore.HasToken())
            {
                StageFailed("auth_check", "login_required");
                Fail("auth_check", "login_required", "请先登录");
            }

            AppState.CurrentUser = await _authService.GetUserAsync();
            StageSuccess("auth_check");

            StageStart("subscription_check");
            AppState.Subscription = await _subscriptionService.GetSubscriptionAsync();
            if (AppState.Subscription?.IsActive != true)
            {
                StageFailed("subscription_check", "subscription_expired");
                ShowSubscription();
                Fail("subscription_check", "subscription_expired", "订阅已过期，请续费后使用");
            }

            StageSuccess("subscription_check");

            if (string.IsNullOrWhiteSpace(AppState.StableDeviceId))
            {
                AppState.StableDeviceId = _deviceIdProvider.GetStableDeviceId();
            }

            StageStart("device_register");
            var deviceResult = await _deviceService.RegisterWindowsDeviceAsync(AppState.StableDeviceId);
            AppState.DeviceAllowed = deviceResult.IsAllowed;
            if (!deviceResult.IsAllowed)
            {
                StageFailed("device_register", "device_limit_reached");
                Fail("device_register", "device_limit_reached", "设备数量已达上限，请先移除旧设备或联系客服");
            }

            StageSuccess("device_register");

            StageStart("devices_fetch");
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId);
            if (IsDeviceLimitExceeded())
            {
                StageFailed("devices_fetch", "device_limit_reached");
                Fail("devices_fetch", "device_limit_reached", "设备数量已达上限，请先移除旧设备或联系客服");
            }

            StageSuccess("devices_fetch");

            StageStart("nodes_fetch");
            AppState.Nodes = await _nodeService.GetNodesAsync();
            if (AppState.Nodes.Count == 0)
            {
                StageFailed("nodes_fetch", "nodes_fetch_failed");
                Fail("nodes_fetch", "nodes_fetch_failed", "暂无可用线路，请稍后重试");
            }

            if (AppState.SelectedNode is null || AppState.Nodes.All(node => node.Id != AppState.SelectedNode.Id))
            {
                AppState.SelectedNode = AppState.Nodes.FirstOrDefault();
            }

            StageSuccess("nodes_fetch");

            if (AppState.SelectedNode is null)
            {
                Fail("node_select", "node_not_selected", "请先选择线路");
            }

            var selectedNode = AppState.SelectedNode;
            if (selectedNode is null)
            {
                Fail("node_select", "node_not_selected", "请先选择线路");
            }

            if (!SingBoxService.IsAdministrator())
            {
                AdminRestartButton.Visibility = Visibility.Visible;
                StageFailed("sing_box_start", "not_admin");
                Fail("sing_box_start", "not_admin", "请以管理员身份运行闪连 VPN");
            }

            StageStart("node_config");
            var nodeConfig = await _nodeService.GetNodeConfigAsync(selectedNode!.Id);
            StageSuccess("node_config");

            StageStart("config_generate");
            var configPath = _configBuilder.BuildRuntimeConfig(nodeConfig);
            StageSuccess("config_generate");

            await _singBoxService.CheckConfigAsync(configPath);
            await _singBoxService.StartAsync(configPath);

            MessageTextBlock.Text = "正在确认网络";
            StageStart("dns_check");
            if (!await _healthCheck.CheckDnsAsync())
            {
                SafeLogger.Diagnostic("dns_check", "dns_failed", _singBoxService.GetOutputSummary());
                StageFailed("dns_check", "dns_failed");
                Fail("dns_check", "dns_failed", "VPN 已启动，但网络解析异常");
            }

            StageSuccess("dns_check");

            StageStart("internet_check");
            if (!await _healthCheck.CheckInternetAsync())
            {
                SafeLogger.Diagnostic("internet_check", "internet_check_failed", _singBoxService.GetOutputSummary());
                StageFailed("internet_check", "internet_check_failed");
                Fail("internet_check", "internet_check_failed", "VPN 已启动，但网络不可用");
            }

            StageSuccess("internet_check");
            _isConnected = true;
            SafeLogger.Info("connect_success");
            SetStatus("已连接");
            MessageTextBlock.Text = "网络已连接";
        }
        catch (ApiException ex)
        {
            var stage = string.IsNullOrWhiteSpace(AppState.LastErrorStage) ? "unknown" : AppState.LastErrorStage;
            var errorCode = NormalizeErrorCode(stage, ex.ErrorCode);
            StageFailed(stage, errorCode);
            SetStatus(errorCode is "dns_failed" or "internet_check_failed" ? "网络异常" : "未连接");
            MessageTextBlock.Text = ToUserMessage(errorCode, ex.Message);
            _singBoxService.Stop();
        }
        catch (Exception)
        {
            StageFailed("unknown", "unknown_error");
            SetStatus("未连接");
            MessageTextBlock.Text = "连接失败，请切换线路重试";
            _singBoxService.Stop();
        }
        finally
        {
            ToggleConnectButtons(true);
            UpdateHome();
        }
    }

    private static void StageStart(string stage)
    {
        AppState.LastErrorStage = stage;
        SafeLogger.Info($"{stage}_start");
    }

    private static void StageSuccess(string stage)
    {
        SafeLogger.Info($"{stage}_success");
    }

    private static void StageFailed(string stage, string errorCode)
    {
        AppState.LastErrorStage = stage;
        AppState.LastErrorCode = errorCode;
        SafeLogger.Info($"{stage}_failed");
        SafeLogger.Error(errorCode);
    }

    private static void Fail(string stage, string errorCode, string message)
    {
        StageFailed(stage, errorCode);
        throw new ApiException(message, errorCode: errorCode);
    }

    private static string NormalizeErrorCode(string stage, string? errorCode)
    {
        if (errorCode is "network_error" or "network_timeout" or "http_500" or "http_502" or "http_503" or "http_504")
        {
            return stage switch
            {
                "device_register" => "device_register_failed",
                "devices_fetch" => "devices_fetch_failed",
                "nodes_fetch" => "nodes_fetch_failed",
                "node_config" => "node_config_failed",
                _ => "server_unreachable"
            };
        }

        return errorCode switch
        {
            "missing_sing_box" => "sing_box_missing",
            "invalid_node_config" => "node_config_failed",
            null or "" => stage switch
            {
                "node_config" => "node_config_failed",
                "config_generate" => "config_generate_failed",
                _ => "unknown_error"
            },
            _ => errorCode
        };
    }

    private static string ToUserMessage(string errorCode, string fallback) => errorCode switch
    {
        "login_required" => "请先登录",
        "subscription_expired" => "订阅已过期，请续费后使用",
        "device_limit_reached" => "设备数量已达上限，请先移除旧设备或联系客服",
        "device_register_failed" => "设备注册失败，请稍后重试",
        "devices_fetch_failed" => "设备状态获取失败，请稍后重试",
        "nodes_fetch_failed" => "线路列表获取失败，请稍后重试",
        "node_not_selected" => "请先选择线路",
        "node_config_failed" => "线路连接失败，请切换线路重试",
        "config_generate_failed" => "VPN 配置生成失败，请联系客服",
        "sing_box_missing" => "缺少 VPN 核心文件，请联系客服",
        "sing_box_config_invalid" => "VPN 配置无效，请联系客服",
        "sing_box_start_failed" => "线路连接失败，请切换线路重试",
        "not_admin" => "请以管理员身份运行闪连 VPN",
        "tun_permission_failed" => "请以管理员身份运行闪连 VPN",
        "dns_failed" => "VPN 已启动，但网络解析异常",
        "internet_check_failed" => "VPN 已启动，但网络不可用",
        "server_unreachable" => "服务器不可达，请稍后重试",
        "handshake_failed" => "线路连接失败，请切换线路重试",
        "auth_password_wrong" => "线路认证失败，请切换线路重试",
        "tls_or_sni_failed" => "线路连接失败，请切换线路重试",
        "route_failed" => "VPN 已启动，但系统路由异常",
        _ => string.IsNullOrWhiteSpace(fallback) || fallback == "网络错误，请稍后重试"
            ? "连接失败，请切换线路重试"
            : fallback
    };

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
        if (AppState.SelectedNode is not null
            && AppState.NodeLatencies.TryGetValue(AppState.SelectedNode.Id, out var latency))
        {
            LatencyTextBlock.Text = latency.HasValue ? $"{latency.Value} ms" : "检测失败";
        }
        else
        {
            LatencyTextBlock.Text = "-- ms";
        }

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
        AppState.ConnectionStatus = status;
        StatusTextBlock.Text = status;
        TopStatusTextBlock.Text = status;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = $"闪连 VPN - {status}";
        }
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
        ContentFrame.Content = new NodesPage(() =>
        {
            UpdateHome();
            ShowHome();
        });
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
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(1500, "闪连 VPN", "已最小化到托盘", Forms.ToolTipIcon.Info);
            return;
        }

        if (_singBoxService.IsRunning)
        {
            _singBoxService.Stop();
        }

        _notifyIcon?.Dispose();
    }

    private void InitializeTray()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "闪连 VPN - 未连接",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        _notifyIcon.ContextMenuStrip.Items.Add("打开闪连 VPN", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _notifyIcon.ContextMenuStrip.Items.Add("连接", null, (_, _) => Dispatcher.Invoke(() => ConnectButton_Click(this, new RoutedEventArgs())));
        _notifyIcon.ContextMenuStrip.Items.Add("断开", null, (_, _) => Dispatcher.Invoke(Disconnect));
        _notifyIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Disconnect();
        Close();
    }
}


