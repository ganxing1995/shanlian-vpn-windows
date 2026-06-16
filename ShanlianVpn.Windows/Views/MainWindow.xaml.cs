using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly NetworkEnvironmentService _networkEnvironmentService = new();
    private readonly WindowsDeviceIdProvider _deviceIdProvider = new();
    private readonly ConfigBuilder _configBuilder = new();
    private readonly SingBoxService _singBoxService = new();
    private readonly ConnectivityHealthCheck _healthCheck = new();
    private readonly Stopwatch _windowLifetime = Stopwatch.StartNew();
    private Forms.NotifyIcon? _notifyIcon;
    private bool _allowExit;
    private bool _isConnected;
    private bool _isConnecting;
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
        var lightCheck = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(AppState.StableDeviceId))
        {
            AppState.StableDeviceId = _deviceIdProvider.GetStableDeviceId();
        }

        MessageTextBlock.Text = TokenStore.HasToken() ? "网络已准备" : "请先登录";
        lightCheck.Stop();
        SafeLogger.Performance("startup_light_check_ms", lightCheck.ElapsedMilliseconds);
        loaded.Stop();
        SafeLogger.Performance("main_window_loaded_ms", loaded.ElapsedMilliseconds);
        _ = RefreshRemoteStateAsync(forceRefresh: false);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        SafeLogger.Performance("first_window_render_ms", _windowLifetime.ElapsedMilliseconds);
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

            if (SubscriptionGate.CanConnect(AppState.Subscription))
            {
                var nodes = Stopwatch.StartNew();
                AppState.Nodes = await _nodeService.GetNodesAsync(forceRefresh);
                AppState.SelectedNode ??= GetDefaultNode(AppState.Nodes);
                nodes.Stop();
                SafeLogger.Performance("nodes_fetch_ms", nodes.ElapsedMilliseconds);
            }
            else
            {
                NodeService.ClearCache();
                AppState.Nodes = [];
                AppState.SelectedNode = null;
                AppState.NodeLatencies.Clear();
            }

            MessageTextBlock.Text = !SubscriptionGate.CanConnect(AppState.Subscription)
                ? SubscriptionGate.BlockerMessage(AppState.Subscription)
                : !IsDeviceLimitExceeded()
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
            AppState.Subscription = null;
            NodeService.ClearCache();
            AppState.Nodes = [];
            AppState.SelectedNode = null;
            AppState.NodeLatencies.Clear();
            MessageTextBlock.Text = SubscriptionGate.UnverifiedMessage;
            SetStatus("网络异常");
        }
        catch
        {
            AppState.Subscription = null;
            NodeService.ClearCache();
            AppState.Nodes = [];
            AppState.SelectedNode = null;
            AppState.NodeLatencies.Clear();
            MessageTextBlock.Text = SubscriptionGate.UnverifiedMessage;
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

        if (_isConnecting)
        {
            return;
        }

        if (_isConnected)
        {
            await DisconnectAsync();
            return;
        }

        _isConnecting = true;
        AdminRestartButton.Visibility = Visibility.Collapsed;
        SetStatus("正在连接");
        MessageTextBlock.Text = "正在建立安全连接";
        SetHealthSummary("待检查");
        CircleButton.Content = "正在连接...";
        SecondaryConnectButton.Content = "正在连接...";
        ToggleConnectButtons(false);
        feedback.Stop();
        SafeLogger.Performance("connect_click_to_ui_feedback_ms", feedback.ElapsedMilliseconds);

        try
        {
            BeginStage("auth_check");
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

            BeginStage("subscription_check");
            var subscription = Stopwatch.StartNew();
            AppState.Subscription = await _subscriptionService.GetSubscriptionAsync(forceRefresh: true);
            subscription.Stop();
            SafeLogger.Performance("subscription_fetch_ms", subscription.ElapsedMilliseconds);
            if (!SubscriptionGate.CanConnect(AppState.Subscription))
            {
                var blockerCode = SubscriptionGate.BlockerCode(AppState.Subscription);
                StageFailed("subscription_check", blockerCode);
                NodeService.ClearCache();
                AppState.Nodes = [];
                AppState.SelectedNode = null;
                AppState.NodeLatencies.Clear();
                ShowSubscription();
                Fail("subscription_check", blockerCode, SubscriptionGate.BlockerMessage(AppState.Subscription));
            }

            StageSuccess("subscription_check");

            if (string.IsNullOrWhiteSpace(AppState.StableDeviceId))
            {
                AppState.StableDeviceId = _deviceIdProvider.GetStableDeviceId();
            }

            BeginStage("device_register");
            var deviceResult = await _deviceService.RegisterWindowsDeviceAsync(AppState.StableDeviceId);
            AppState.DeviceAllowed = deviceResult.IsAllowed;
            if (!deviceResult.IsAllowed)
            {
                StageFailed("device_register", "device_limit_reached");
                Fail("device_register", "device_limit_reached", "设备数量已达上限，请先移除旧设备或联系客服");
            }

            StageSuccess("device_register");

            BeginStage("devices_fetch");
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

            BeginStage("nodes_fetch");
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
                AppState.SelectedNode = GetDefaultNode(AppState.Nodes);
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

            BeginStage("network_environment_check");
            var networkEnvironment = await _networkEnvironmentService.InspectAsync();
            if (networkEnvironment.DevProxyDetected && !networkEnvironment.NetworkConflict)
            {
                SafeLogger.Info("dev_proxy_allowed");
            }

            if (networkEnvironment.NetworkConflict && !ConfirmNetworkConflict())
            {
                StageFailed("network_environment_check", "network_conflict");
                Fail("network_environment_check", "network_conflict", "检测到其他 VPN 或 TUN 模式正在运行，请关闭其他 VPN/TUN 后再连接闪连 VPN");
            }

            StageSuccess("network_environment_check");

            BeginStage("node_config");
            var nodeConfig = await _nodeService.GetNodeConfigAsync(selectedNode!.Id);
            StageSuccess("node_config");

            BeginStage("proxy_preflight");
            var preflightTimer = Stopwatch.StartNew();
            var preflight = new Hysteria2PreflightService(_configBuilder, _singBoxService);
            await preflight.RunAsync(nodeConfig);
            preflightTimer.Stop();
            SafeLogger.Performance("preflight_ms", preflightTimer.ElapsedMilliseconds);
            SafeLogger.Performance("preflight_total_ms", preflightTimer.ElapsedMilliseconds);
            StageSuccess("proxy_preflight");

            var profiles = new[] { VpnConfigProfile.RelaxedRoute, VpnConfigProfile.StrictRoute, VpnConfigProfile.SimpleDns };
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
            if (stage == "subscription_check")
            {
                if (errorCode == "subscription_unverified")
                {
                    AppState.Subscription = null;
                }

                NodeService.ClearCache();
                AppState.Nodes = [];
                AppState.SelectedNode = null;
                AppState.NodeLatencies.Clear();
            }

            ConnectionDiagnosticsState.Update(("final_blocker", errorCode));
            StageFailed(stage, errorCode);
            SetStatus(errorCode is "dns_failed" or "internet_check_failed" ? "网络异常" : "未连接");
            MessageTextBlock.Text = ToUserMessage(errorCode, ex.Message);
            await _singBoxService.StopAsync();
        }
        catch (Exception)
        {
            ConnectionDiagnosticsState.Update(("final_blocker", "unknown_error"));
            StageFailed("unknown", "unknown_error");
            SetStatus("未连接");
            MessageTextBlock.Text = "连接失败，请切换线路重试";
            await _singBoxService.StopAsync();
        }
        finally
        {
            connectTotal.Stop();
            SafeLogger.Performance("connect_total_ms", connectTotal.ElapsedMilliseconds);
            _isConnecting = false;
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
            BeginStage("config_generate");
            var configPath = _configBuilder.BuildRuntimeConfig(nodeConfig, profile);
            StageSuccess("config_generate");

            var checkTimer = Stopwatch.StartNew();
            await _singBoxService.CheckConfigAsync(configPath);
            checkTimer.Stop();
            SafeLogger.Performance("sing_box_check_ms", checkTimer.ElapsedMilliseconds);
            SafeLogger.Info("profile_check_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_check", "success"));
            BeginStage("sing_box_start");
            var startTimer = Stopwatch.StartNew();
            await _singBoxService.StartAsync(configPath, mode: "tun", profile: profileName);
            startTimer.Stop();
            SafeLogger.Performance("sing_box_start_ms", startTimer.ElapsedMilliseconds);
            SafeLogger.Info("profile_start_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_start", "success"));
            StageSuccess("sing_box_start");

            MessageTextBlock.Text = "正在等待 VPN 网络";
            BeginStage("tun_check");
            var tunTimer = Stopwatch.StartNew();
            if (!await _healthCheck.WaitForTunAdapterAsync())
            {
                tunTimer.Stop();
                SafeLogger.Performance("tun_wait_ms", tunTimer.ElapsedMilliseconds);
                SafeLogger.Info("tun_detect_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_tun", "failed"));
                await HandleStartedProfileFailureAsync("tun_check", "tun_adapter_missing", isFinalProfile);
                return false;
            }

            tunTimer.Stop();
            SafeLogger.Performance("tun_wait_ms", tunTimer.ElapsedMilliseconds);
            SafeLogger.Info("tun_detect_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_tun", "success"));
            TunHealthTextBlock.Text = "正常";
            StageSuccess("tun_check");
            MessageTextBlock.Text = "正在等待系统路由";
            BeginStage("route_check");
            var routeTimer = Stopwatch.StartNew();
            if (!await _healthCheck.WaitForRouteAsync())
            {
                routeTimer.Stop();
                SafeLogger.Performance("route_check_ms", routeTimer.ElapsedMilliseconds);
                SafeLogger.Info("route_detect_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_route", "failed"));
                await HandleStartedProfileFailureAsync("route_check", "route_failed", isFinalProfile);
                return false;
            }

            routeTimer.Stop();
            SafeLogger.Performance("route_check_ms", routeTimer.ElapsedMilliseconds);
            SafeLogger.Info("route_detect_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_route", "success"));
            RouteHealthTextBlock.Text = "正常";
            StageSuccess("route_check");
            var healthTimer = Stopwatch.StartNew();
            await _healthCheck.WaitForRouteAndDnsSettleAsync();

            MessageTextBlock.Text = "正在确认网络";
            BeginStage("dns_check");
            var dnsTimer = Stopwatch.StartNew();
            if (!await _healthCheck.CheckDnsAsync())
            {
                dnsTimer.Stop();
                SafeLogger.Performance("dns_check_ms", dnsTimer.ElapsedMilliseconds);
                SafeLogger.Info("profile_dns_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_dns", "failed"));
                await HandleStartedProfileFailureAsync("dns_check", "dns_failed", isFinalProfile);
                return false;
            }

            dnsTimer.Stop();
            SafeLogger.Performance("dns_check_ms", dnsTimer.ElapsedMilliseconds);
            SafeLogger.Info("profile_dns_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_dns", "success"));
            DnsHealthTextBlock.Text = "正常";
            StageSuccess("dns_check");

            BeginStage("internet_check");
            var httpsTimer = Stopwatch.StartNew();
            if (!await _healthCheck.CheckInternetAsync())
            {
                httpsTimer.Stop();
                SafeLogger.Performance("https_check_ms", httpsTimer.ElapsedMilliseconds);
                SafeLogger.Info("https_check_failed");
                ConnectionDiagnosticsState.Update(($"profile_{profileName}_https", "failed"));
                await HandleStartedProfileFailureAsync("internet_check", "internet_check_failed", isFinalProfile);
                return false;
            }

            httpsTimer.Stop();
            SafeLogger.Performance("https_check_ms", httpsTimer.ElapsedMilliseconds);
            SafeLogger.Info("https_check_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_https", "success"));
            HttpsHealthTextBlock.Text = "正常";
            StageSuccess("internet_check");
            SafeLogger.Performance("country_check_ms", 0);
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

    private void BeginStage(string stage)
    {
        AppState.LastErrorStage = stage;
        SafeLogger.Info($"{stage}_start");
        MessageTextBlock.Text = stage switch
        {
            "auth_check" => "正在检查登录状态",
            "subscription_check" => "正在检查订阅状态",
            "device_register" => "正在确认设备授权",
            "devices_fetch" => "正在同步设备状态",
            "nodes_fetch" => "正在刷新线路",
            "network_environment_check" => "正在检查网络环境",
            "node_config" => "正在准备线路配置",
            "proxy_preflight" => "正在预检线路",
            "config_generate" => "正在生成 VPN 配置",
            "sing_box_start" => "正在启动 VPN 核心",
            "tun_check" => "正在等待 VPN 网络",
            "route_check" => "正在等待系统路由",
            "dns_check" => "正在检查 DNS",
            "internet_check" => "正在检查 HTTPS",
            _ => MessageTextBlock.Text
        };
        switch (stage)
        {
            case "tun_check":
                TunHealthTextBlock.Text = "检查中";
                break;
            case "route_check":
                RouteHealthTextBlock.Text = "检查中";
                break;
            case "dns_check":
                DnsHealthTextBlock.Text = "检查中";
                break;
            case "internet_check":
                HttpsHealthTextBlock.Text = "检查中";
                break;
        }
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
                "subscription_check" => "subscription_unverified",
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
        "subscription_required" => SubscriptionGate.RequiredMessage,
        "subscription_expired" => SubscriptionGate.RequiredMessage,
        "subscription_unverified" => SubscriptionGate.UnverifiedMessage,
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
        "network_conflict" => "检测到其他 VPN 或 TUN 模式正在运行，请关闭其他 VPN/TUN 后再连接闪连 VPN",
        _ => string.IsNullOrWhiteSpace(fallback) || fallback == "网络错误，请稍后重试"
            ? "连接失败，请切换线路重试"
            : fallback
    };

    private async Task DisconnectAsync()
    {
        var disconnect = Stopwatch.StartNew();
        ToggleConnectButtons(false);
        MessageTextBlock.Text = "正在断开连接";
        await _singBoxService.StopAsync();
        _isConnected = false;
        SetStatus("未连接");
        MessageTextBlock.Text = "网络已准备";
        SetHealthSummary("已准备");
        disconnect.Stop();
        SafeLogger.Performance("disconnect_restore_ms", disconnect.ElapsedMilliseconds);
        ToggleConnectButtons(true);
        UpdateHome();
    }

    private void Disconnect()
    {
        _ = DisconnectAsync();
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

        if (!_isConnected && !_isConnecting && !SubscriptionGate.CanConnect(AppState.Subscription))
        {
            var buttonText = SubscriptionGate.ConnectButtonText(AppState.Subscription);
            CircleButton.Content = buttonText;
            SecondaryConnectButton.Content = buttonText;
            CircleButton.Background = BrushFromResource("WarningBrush");
            SecondaryConnectButton.Background = BrushFromResource("WarningBrush");
            CircleButton.IsEnabled = false;
            SecondaryConnectButton.IsEnabled = false;
            render.Stop();
            SafeLogger.Performance("home_render_ms", render.ElapsedMilliseconds);
            return;
        }

        CircleButton.Content = _isConnected ? "断开" : "连接";
        SecondaryConnectButton.Content = _isConnected ? "断开连接" : "连接";
        CircleButton.IsEnabled = true;
        SecondaryConnectButton.IsEnabled = true;
        CircleButton.Background = _isConnected ? BrushFromResource("ConnectedBrush") : BrushFromResource("AccentBrush");
        SecondaryConnectButton.Background = _isConnected ? BrushFromResource("ConnectedBrush") : BrushFromResource("AccentBrush");
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

    private static VpnNode? GetDefaultNode(IReadOnlyList<VpnNode> nodes) =>
        nodes.FirstOrDefault(IsUnitedStatesNode) ?? nodes.FirstOrDefault();

    private static bool IsUnitedStatesNode(VpnNode node)
    {
        var countryCode = node.CountryCode.ToUpperInvariant();
        return countryCode is "US" or "USA"
            || node.Country.Contains("United States", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("United States", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("USA", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string status)
    {
        AppState.ConnectionStatus = status;
        StatusTextBlock.Text = status;
        TopStatusTextBlock.Text = status;
        TopStatusTextBlock.Foreground = status == "已连接"
            ? BrushFromResource("ConnectedBrush")
            : BrushFromResource("BrightTextBrush");
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

    private void SetHealthSummary(string value)
    {
        TunHealthTextBlock.Text = value;
        RouteHealthTextBlock.Text = value;
        DnsHealthTextBlock.Text = value;
        HttpsHealthTextBlock.Text = value;
    }

    private SolidColorBrush BrushFromResource(string key) =>
        TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 166, 255));

    private void SetActiveNav(System.Windows.Controls.Button activeButton)
    {
        var activeBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 48, 79));
        var inactiveBackground = System.Windows.Media.Brushes.Transparent;
        var activeForeground = BrushFromResource("BrightTextBrush");
        var inactiveForeground = BrushFromResource("SoftTextBrush");

        foreach (var button in new[]
                 {
                     HomeNavButton,
                     NodesNavButton,
                     SubscriptionNavButton,
                     AccountNavButton,
                     SettingsNavButton,
                     DiagnosticsNavButton
                 })
        {
            button.Background = ReferenceEquals(button, activeButton) ? activeBackground : inactiveBackground;
            button.Foreground = ReferenceEquals(button, activeButton) ? activeForeground : inactiveForeground;
        }
    }

    private void ShowHome()
    {
        var nav = Stopwatch.StartNew();
        SetActiveNav(HomeNavButton);
        ContentFrame.Content = null;
        ContentFrame.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        UpdateHome();
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void ShowNodes()
    {
        var nav = Stopwatch.StartNew();
        SetActiveNav(NodesNavButton);
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
        SetActiveNav(SubscriptionNavButton);
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new SubscriptionPage();
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void NodesNav_Click(object sender, RoutedEventArgs e) => ShowNodes();
    private void SubscriptionNav_Click(object sender, RoutedEventArgs e) => ShowSubscription();
    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        var nav = Stopwatch.StartNew();
        SetActiveNav(SettingsNavButton);
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new SettingsPage();
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void DiagnosticsNav_Click(object sender, RoutedEventArgs e)
    {
        var nav = Stopwatch.StartNew();
        SetActiveNav(DiagnosticsNavButton);
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new DiagnosticsPage();
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void AccountNav_Click(object sender, RoutedEventArgs e)
    {
        var nav = Stopwatch.StartNew();
        SetActiveNav(AccountNavButton);
        HomePanel.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        ContentFrame.Content = new AccountPage(() =>
        {
            _ = DisconnectAsync();
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

    private static bool ConfirmNetworkConflict()
    {
        var result = System.Windows.MessageBox.Show(
            "检测到其他 VPN 或 TUN 模式正在运行，请关闭其他 VPN/TUN 后再连接闪连 VPN。\n\n选择“确定”继续尝试，选择“取消”取消连接。",
            "闪连 VPN",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
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
        _notifyIcon.ContextMenuStrip.Items.Add("断开", null, (_, _) => Dispatcher.Invoke(() => _ = DisconnectAsync()));
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
        _ = DisconnectAsync();
        Close();
    }
}
