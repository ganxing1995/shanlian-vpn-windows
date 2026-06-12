using System.Windows;
using System.Diagnostics;
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
    private DateTimeOffset _lastMessageShownAt = DateTimeOffset.MinValue;
    private string _lastMessageCode = "";

    public MainWindow()
    {
        InitializeComponent();
        InitializeTray();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var loaded = Stopwatch.StartNew();
        ShowHome();
        loaded.Stop();
        SafeLogger.Performance("main_window_loaded_ms", loaded.ElapsedMilliseconds);
        _ = RefreshRemoteStateAsync(forceRefresh: false);
    }

    private async Task RefreshRemoteStateAsync(bool forceRefresh)
    {
        var total = Stopwatch.StartNew();
        try
        {
            var authLoad = Stopwatch.StartNew();
            AppState.StableDeviceId = _deviceIdProvider.GetStableDeviceId();
            authLoad.Stop();
            SafeLogger.Performance("auth_load_ms", authLoad.ElapsedMilliseconds);

            var authCheck = Stopwatch.StartNew();
            AppState.CurrentUser ??= await _authService.GetUserAsync();
            authCheck.Stop();
            SafeLogger.Performance("auth_check_ms", authCheck.ElapsedMilliseconds);

            var subscription = Stopwatch.StartNew();
            AppState.Subscription = await _subscriptionService.GetSubscriptionAsync(forceRefresh);
            subscription.Stop();
            SafeLogger.Performance("subscription_fetch_ms", subscription.ElapsedMilliseconds);

            var devices = Stopwatch.StartNew();
            var deviceResult = await _deviceService.RegisterWindowsDeviceAsync(AppState.StableDeviceId);
            AppState.DeviceAllowed = deviceResult.IsAllowed;
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId, forceRefresh);
            devices.Stop();
            SafeLogger.Performance("devices_fetch_ms", devices.ElapsedMilliseconds);

            var nodes = Stopwatch.StartNew();
            AppState.Nodes = await _nodeService.GetNodesAsync(forceRefresh);
            AppState.SelectedNode ??= AppState.Nodes.FirstOrDefault();
            nodes.Stop();
            SafeLogger.Performance("nodes_fetch_ms", nodes.ElapsedMilliseconds);

            MessageTextBlock.Text = !IsDeviceLimitExceeded()
                ? "网络已准备"
                : "设备数量已达上限，请先在手机端或后台移除旧设备。";
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            TokenStore.Clear();
            ShowMessageBoxOnce("login_expired", "登录已过期，请重新登录");
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
            total.Stop();
            SafeLogger.Performance("remote_state_refresh_ms", total.ElapsedMilliseconds);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var connectTotal = Stopwatch.StartNew();
        var feedback = Stopwatch.StartNew();
        SafeLogger.Info("connect_clicked");

        if (_isConnected)
        {
            Disconnect();
            return;
        }

        AdminRestartButton.Visibility = Visibility.Collapsed;
        SetStatus("正在连接");
        MessageTextBlock.Text = "正在建立安全连接";
        CircleButton.Content = "正在连接...";
        SecondaryConnectButton.Content = "正在连接...";
        ToggleConnectButtons(false);
        feedback.Stop();
        SafeLogger.Performance("connect_click_to_ui_feedback_ms", feedback.ElapsedMilliseconds);

        try
        {
            StageStart("auth_check");
            if (!TokenStore.HasToken())
            {
                StageFailed("auth_check", "login_required");
                Fail("auth_check", "login_required", "请先登录");
            }

            var authCheck = Stopwatch.StartNew();
            AppState.CurrentUser = await _authService.GetUserAsync();
            authCheck.Stop();
            SafeLogger.Performance("auth_check_ms", authCheck.ElapsedMilliseconds);
            StageSuccess("auth_check");

            StageStart("subscription_check");
            var subscription = Stopwatch.StartNew();
            AppState.Subscription = await _subscriptionService.GetSubscriptionAsync(forceRefresh: true);
            subscription.Stop();
            SafeLogger.Performance("subscription_fetch_ms", subscription.ElapsedMilliseconds);
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
            var devices = Stopwatch.StartNew();
            AppState.Devices = await _deviceService.GetDevicesAsync(AppState.StableDeviceId, forceRefresh: true);
            devices.Stop();
            SafeLogger.Performance("devices_fetch_ms", devices.ElapsedMilliseconds);
            if (IsDeviceLimitExceeded())
            {
                StageFailed("devices_fetch", "device_limit_reached");
                Fail("devices_fetch", "device_limit_reached", "设备数量已达上限，请先移除旧设备或联系客服");
            }

            StageSuccess("devices_fetch");

            StageStart("nodes_fetch");
            var nodes = Stopwatch.StartNew();
            AppState.Nodes = await _nodeService.GetNodesAsync(forceRefresh: true);
            nodes.Stop();
            SafeLogger.Performance("nodes_fetch_ms", nodes.ElapsedMilliseconds);
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

            StageStart("proxy_preflight");
            var preflightTimer = Stopwatch.StartNew();
            var preflight = new Hysteria2PreflightService(_configBuilder, _singBoxService);
            await preflight.RunAsync(nodeConfig);
            preflightTimer.Stop();
            SafeLogger.Performance("preflight_total_ms", preflightTimer.ElapsedMilliseconds);
            StageSuccess("proxy_preflight");

            var profiles = new[] { VpnConfigProfile.StrictRoute, VpnConfigProfile.RelaxedRoute, VpnConfigProfile.SimpleDns };
            for (var index = 0; index < profiles.Length; index++)
            {
                if (await TryConnectProfileAsync(nodeConfig, profiles[index], index == profiles.Length - 1))
                {
                    return;
                }

                if (index < profiles.Length - 1)
                {
                    MessageTextBlock.Text = "正在尝试备用网络配置";
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }
        catch (ApiException ex)
        {
            var stage = string.IsNullOrWhiteSpace(AppState.LastErrorStage) ? "unknown" : AppState.LastErrorStage;
            var errorCode = NormalizeErrorCode(stage, ex.ErrorCode);
            ConnectionDiagnosticsState.Update(("final_blocker", errorCode));
            StageFailed(stage, errorCode);
            SetStatus(errorCode is "dns_failed" or "internet_check_failed" ? "网络异常" : "未连接");
            MessageTextBlock.Text = ToUserMessage(errorCode, ex.Message);
            _singBoxService.Stop();
        }
        catch (Exception)
        {
            ConnectionDiagnosticsState.Update(("final_blocker", "unknown_error"));
            StageFailed("unknown", "unknown_error");
            SetStatus("未连接");
            MessageTextBlock.Text = "连接失败，请切换线路重试";
            _singBoxService.Stop();
        }
        finally
        {
            connectTotal.Stop();
            SafeLogger.Performance("connect_total_ms", connectTotal.ElapsedMilliseconds);
            ToggleConnectButtons(true);
            UpdateHome();
        }
    }

    private async Task<bool> TryConnectProfileAsync(NodeConfig nodeConfig, VpnConfigProfile profile, bool isFinalProfile)
    {
        var profileName = GetProfileName(profile);
        var profileTimer = Stopwatch.StartNew();
        SafeLogger.Info($"config_profile_{profileName}");
        SafeLogger.Diagnostic("config_profile", "none", $"config_profile={profileName}");
        ConnectionDiagnosticsState.Update(("latest_profile", profileName), ($"profile_{profileName}_started", true));

        try
        {
            StageStart("config_generate");
            var configPath = _configBuilder.BuildRuntimeConfig(nodeConfig, profile);
            StageSuccess("config_generate");

            await _singBoxService.CheckConfigAsync(configPath);
            SafeLogger.Info("profile_check_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_check", "success"));
            StageStart("sing_box_start");
            await _singBoxService.StartAsync(configPath, mode: "tun", profile: profileName);
            SafeLogger.Info("profile_start_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_start", "success"));
            StageSuccess("sing_box_start");

            MessageTextBlock.Text = "正在等待 VPN 网络";
            StageStart("tun_check");
            if (!await _healthCheck.WaitForTunAdapterAsync())
            {
                SafeLogger.Info("tun_detect_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_tun", "failed"));
                await HandleStartedProfileFailureAsync("tun_check", "tun_adapter_missing", isFinalProfile);
                return false;
            }

            SafeLogger.Info("tun_detect_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_tun", "success"));
            StageSuccess("tun_check");
            MessageTextBlock.Text = "正在等待系统路由";
            StageStart("route_check");
            if (!await _healthCheck.WaitForRouteAsync())
            {
                SafeLogger.Info("route_detect_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_route", "failed"));
                await HandleStartedProfileFailureAsync("route_check", "route_failed", isFinalProfile);
                return false;
            }

            SafeLogger.Info("route_detect_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_route", "success"));
            StageSuccess("route_check");
            var healthTimer = Stopwatch.StartNew();
            await _healthCheck.WaitForRouteAndDnsSettleAsync();

            MessageTextBlock.Text = "正在确认网络";
            StageStart("dns_check");
            if (!await _healthCheck.CheckDnsAsync())
            {
                SafeLogger.Info("profile_dns_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_dns", "failed"));
                await HandleStartedProfileFailureAsync("dns_check", "dns_failed", isFinalProfile);
                return false;
            }

            SafeLogger.Info("profile_dns_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_dns", "success"));
            StageSuccess("dns_check");

            StageStart("internet_check");
            if (!await _healthCheck.CheckInternetAsync())
            {
                SafeLogger.Info("https_check_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_https", "failed"));
                await HandleStartedProfileFailureAsync("internet_check", "internet_check_failed", isFinalProfile);
                return false;
            }

            SafeLogger.Info("https_check_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_https", "success"));
            StageSuccess("internet_check");
            healthTimer.Stop();
            SafeLogger.Performance("health_check_total_ms", healthTimer.ElapsedMilliseconds);
            profileTimer.Stop();
            if (profileName == "B")
            {
                SafeLogger.Performance("tun_profile_B_total_ms", profileTimer.ElapsedMilliseconds);
            }

            _isConnected = true;
            SafeLogger.Info("connect_success");
            SetStatus("已连接");
            MessageTextBlock.Text = "网络已连接";
            ConnectionDiagnosticsState.Update(
                ("tun_success", true),
                ("successful_profile", profileName),
                ("final_blocker", "none"));
            return true;
        }
        catch (ApiException ex) when (!isFinalProfile)
        {
            var errorCode = NormalizeErrorCode(AppState.LastErrorStage, ex.ErrorCode);
            SafeLogger.Info("profile_start_failed");
            StageFailed(AppState.LastErrorStage, errorCode);
            var checkState = AppState.LastErrorStage is "config_generate" ? "failed" : "success";
            var startState = AppState.LastErrorStage is "sing_box_start" ? "failed" : "unknown";
            ConnectionDiagnosticsState.Update(
                ($"profile_{profileName}_check", checkState),
                ($"profile_{profileName}_start", startState),
                ($"profile_{profileName}_blocker", errorCode));
            if (_singBoxService.IsRunning)
            {
                await PreserveStartedFailureAsync(errorCode);
            }

            _singBoxService.Stop();
            profileTimer.Stop();
            if (profileName == "B")
            {
                SafeLogger.Performance("tun_profile_B_total_ms", profileTimer.ElapsedMilliseconds);
            }

            return false;
        }
        catch (ApiException ex)
        {
            var errorCode = NormalizeErrorCode(AppState.LastErrorStage, ex.ErrorCode);
            var checkState = AppState.LastErrorStage is "config_generate" ? "failed" : "success";
            var startState = AppState.LastErrorStage is "sing_box_start" ? "failed" : "unknown";
            ConnectionDiagnosticsState.Update(
                ($"profile_{profileName}_check", checkState),
                ($"profile_{profileName}_start", startState),
                ($"profile_{profileName}_blocker", errorCode),
                ("final_blocker", errorCode));
            profileTimer.Stop();
            if (profileName == "B")
            {
                SafeLogger.Performance("tun_profile_B_total_ms", profileTimer.ElapsedMilliseconds);
            }

            throw;
        }

    }

    private Task HandleStartedProfileFailureAsync(string stage, string errorCode, bool isFinalProfile)
    {
        SafeLogger.Diagnostic(stage, errorCode, _singBoxService.GetOutputSummary());
        StageFailed(stage, errorCode);
        SetStatus("网络异常");
        MessageTextBlock.Text = "VPN 已启动，正在诊断网络";

        if (isFinalProfile)
        {
            ConnectionDiagnosticsState.Update(("final_blocker", errorCode));
            MessageTextBlock.Text = ToUserMessage(errorCode, "");
            _ = PreserveAndStopAsync(errorCode);
        }
        else
        {
            _singBoxService.Stop();
        }

        return Task.CompletedTask;
    }

    private async Task PreserveStartedFailureAsync(string errorCode)
    {
        await WindowsRuntimeDiagnostics.CaptureWindowAsync(_singBoxService, errorCode, TimeSpan.FromSeconds(90));
    }

    private async Task PreserveAndStopAsync(string errorCode)
    {
        try
        {
            await PreserveStartedFailureAsync(errorCode);
        }
        finally
        {
            _singBoxService.Stop();
        }
    }

    private static string GetProfileName(VpnConfigProfile profile) => profile switch
    {
        VpnConfigProfile.StrictRoute => "A",
        VpnConfigProfile.RelaxedRoute => "B",
        _ => "C"
    };

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
            "sing_box_exited" => "sing_box_exited",
            "tun_adapter_missing" => "tun_adapter_missing",
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
        "hysteria2_outbound_failed" => "当前线路不可达，请切换线路重试",
        "config_generate_failed" => "VPN 配置生成失败，请联系客服",
        "sing_box_missing" => "缺少 VPN 核心文件，请联系客服",
        "sing_box_config_invalid" => "VPN 配置无效，请联系客服",
        "sing_box_exited" => "VPN 核心启动后退出，请切换线路重试",
        "sing_box_start_failed" => "线路连接失败，请切换线路重试",
        "not_admin" => "请以管理员身份运行闪连 VPN",
        "tun_permission_failed" => "VPN 权限不足，请以管理员身份运行",
        "tun_adapter_missing" => "VPN 虚拟网卡启动失败，请重新安装客户端",
        "dns_failed" => "VPN 已启动，但网络解析异常",
        "internet_check_failed" => "VPN 已启动，但网络不可用",
        "server_unreachable" => "服务器不可达，请切换线路重试",
        "handshake_failed" => "当前线路不可达，请切换线路重试",
        "auth_password_wrong" => "节点认证失败，请联系客服",
        "tls_or_sni_failed" => "节点安全连接失败，请联系客服",
        "route_failed" => "VPN 路由启动失败，请重启电脑后重试",
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
        var render = Stopwatch.StartNew();
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
            render.Stop();
            SafeLogger.Performance("home_render_ms", render.ElapsedMilliseconds);
            return;
        }

        CircleButton.Content = _isConnected ? "断开" : "连接";
        SecondaryConnectButton.Content = _isConnected ? "断开连接" : "连接";
        render.Stop();
        SafeLogger.Performance("home_render_ms", render.ElapsedMilliseconds);
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
        var nav = Stopwatch.StartNew();
        ContentFrame.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        UpdateHome();
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void ShowNodes()
    {
        var nav = Stopwatch.StartNew();
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new NodesPage(() =>
        {
            UpdateHome();
            ShowHome();
        });
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void ShowSubscription()
    {
        var nav = Stopwatch.StartNew();
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new SubscriptionPage();
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void NodesNav_Click(object sender, RoutedEventArgs e) => ShowNodes();
    private void SubscriptionNav_Click(object sender, RoutedEventArgs e) => ShowSubscription();

    private void AccountNav_Click(object sender, RoutedEventArgs e)
    {
        var nav = Stopwatch.StartNew();
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new AccountPage(() =>
        {
            Disconnect();
            _authService.Logout();
            new LoginWindow().Show();
            Close();
        });
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void ShowMessageBoxOnce(string code, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastMessageCode == code && now - _lastMessageShownAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastMessageCode = code;
        _lastMessageShownAt = now;
        System.Windows.MessageBox.Show(message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
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


