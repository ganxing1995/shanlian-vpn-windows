using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
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
    private bool _autoConnectAttempted;
    private DateTimeOffset _lastMessageShownAt = DateTimeOffset.MinValue;
    private string _lastMessageCode = "";
    private readonly DispatcherTimer _connectingVisualTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    private int _connectingVisualFrame;

    public MainWindow()
    {
        InitializeComponent();
        _connectingVisualTimer.Tick += ConnectingVisualTimer_Tick;
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

        MessageTextBlock.Text = TokenStore.HasToken() ? "可以开始安全连接" : "请先登录";
        UpdateModeDisplay();
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
                ? "可以开始安全连接"
                : "设备数量已达上限，请先在手机端或后台移除旧设备。";
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            ShowMessageBoxOnce("login_expired", "登录已过期，请重新登录");
            await LogoutAndShowLoginAsync();
        }
        catch (ApiException)
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
            _ = MaybeAutoConnectAsync();
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

        if (!SubscriptionGate.CanConnect(AppState.Subscription))
        {
            ShowSubscription();
            return;
        }

        _isConnecting = true;
        AdminRestartButton.Visibility = Visibility.Collapsed;
        SetStatus("正在连接");
        MessageTextBlock.Text = "正在建立安全连接";
        SetHealthSummary("待检查");
        StartConnectingVisuals();
        ToggleConnectButtons(false);
        feedback.Stop();
        SafeLogger.Performance("connect_click_to_ui_feedback_ms", feedback.ElapsedMilliseconds);

        try
        {
            BeginStage("auth_check");
            SystemProxyService.Restore();
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
                Fail("nodes_fetch", "nodes_fetch_failed", "暂无可用地区，请稍后重试");
            }

            if (AppState.SelectedNode is null || AppState.Nodes.All(node => node.Id != AppState.SelectedNode.Id))
            {
                AppState.SelectedNode = GetDefaultNode(AppState.Nodes);
            }

            StageSuccess("nodes_fetch");

            if (AppState.SelectedNode is null)
            {
                Fail("node_select", "node_not_selected", "请先选择地区");
            }

            var selectedNode = AppState.SelectedNode;
            if (selectedNode is null)
            {
                Fail("node_select", "node_not_selected", "请先选择地区");
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

            if (!SingBoxService.IsAdministrator())
            {
                if (await TryConnectSystemProxyFallbackAsync(nodeConfig))
                {
                    return;
                }

                Fail("system_proxy_fallback", "internet_check_failed", "网络连接失败，请稍后重试");
            }

            BeginStage("proxy_preflight");
            var preflightTimer = Stopwatch.StartNew();
            var preflight = new Hysteria2PreflightService(_configBuilder, _singBoxService);
            var preflightPassed = false;
            try
            {
                await preflight.RunAsync(nodeConfig);
                preflightPassed = true;
            }
            catch (ApiException ex) when (ShouldContinueAfterPreflightFailure(ex.ErrorCode))
            {
                SafeLogger.Info("proxy_preflight_soft_failed");
                SafeLogger.Error(ex.ErrorCode ?? "proxy_preflight_soft_failed");
                ConnectionDiagnosticsState.Update(
                    ("proxy_preflight_soft_failed", true),
                    ("proxy_preflight_soft_failure_code", ex.ErrorCode ?? "unknown"));
                MessageTextBlock.Text = "正在继续尝试建立安全连接";
            }
            preflightTimer.Stop();
            SafeLogger.Performance("preflight_ms", preflightTimer.ElapsedMilliseconds);
            SafeLogger.Performance("preflight_total_ms", preflightTimer.ElapsedMilliseconds);
            if (preflightPassed)
            {
                StageSuccess("proxy_preflight");
            }

            var profiles = new[] { VpnConfigProfile.RelaxedRoute, VpnConfigProfile.StrictRoute, VpnConfigProfile.SimpleDns };
            for (var index = 0; index < profiles.Length; index++)
            {
                if (await TryConnectProfileAsync(nodeConfig, profiles[index], false))
                {
                    return;
                }

                if (index < profiles.Length - 1)
                {
                    MessageTextBlock.Text = "正在尝试其他可用连接方式";
                    await Task.Delay(TimeSpan.FromMilliseconds(350));
                }
            }

            if (await TryConnectSystemProxyFallbackAsync(nodeConfig))
            {
                return;
            }

            throw new ApiException("网络连接失败，请稍后重试", errorCode: "internet_check_failed");
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
            SystemProxyService.Restore();
            await _singBoxService.StopAsync();
        }
        catch (Exception)
        {
            ConnectionDiagnosticsState.Update(("final_blocker", "unknown_error"));
            StageFailed("unknown", "unknown_error");
            SetStatus("未连接");
            MessageTextBlock.Text = "网络连接失败，请稍后重试";
            SystemProxyService.Restore();
            await _singBoxService.StopAsync();
        }
        finally
        {
            connectTotal.Stop();
            SafeLogger.Performance("connect_total_ms", connectTotal.ElapsedMilliseconds);
            StopConnectingVisuals();
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
            var configPath = _configBuilder.BuildRuntimeConfig(nodeConfig, profile, UserSettingsService.Current.ConnectionMode);
            StageSuccess("config_generate");

            var checkTimer = Stopwatch.StartNew();
            await _singBoxService.CheckConfigAsync(configPath);
            checkTimer.Stop();
            SafeLogger.Performance("sing_box_check_ms", checkTimer.ElapsedMilliseconds);
            SafeLogger.Info("profile_check_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_check", "success"));
            BeginStage("sing_box_start");
            var startTimer = Stopwatch.StartNew();
            await _singBoxService.StartAsync(
                configPath,
                mode: UserSettingsService.Current.ConnectionMode == ConnectionMode.Speed ? "tun-speed" : "tun-global",
                profile: profileName);
            startTimer.Stop();
            SafeLogger.Performance("sing_box_start_ms", startTimer.ElapsedMilliseconds);
            SafeLogger.Info("profile_start_success");
            ConnectionDiagnosticsState.Update(($"profile_{profileName}_start", "success"));
            StageSuccess("sing_box_start");

            MessageTextBlock.Text = "正在建立安全连接";
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
            UpdateSummaryCards();
            StageSuccess("tun_check");
            MessageTextBlock.Text = "正在确认网络状态";
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
            UpdateSummaryCards();
            StageSuccess("route_check");
            var healthTimer = Stopwatch.StartNew();
            MessageTextBlock.Text = "正在确认网络状态";
            BeginStage("internet_check");
            var httpsTimer = Stopwatch.StartNew();
            if (!await _healthCheck.CheckInternetAsync())
            {
                httpsTimer.Stop();
                SafeLogger.Performance("https_check_ms", httpsTimer.ElapsedMilliseconds);
                SafeLogger.Info("https_check_failed");

                BeginStage("dns_check");
                var dnsTimer = Stopwatch.StartNew();
                if (!await _healthCheck.CheckDnsAsync())
                {
                    dnsTimer.Stop();
                    SafeLogger.Performance("dns_check_ms", dnsTimer.ElapsedMilliseconds);
                    SafeLogger.Info("profile_dns_failed");
                    ConnectionDiagnosticsState.Update(
                        ($"profile_{profileName}_dns", "failed"),
                        ($"profile_{profileName}_https", "failed"));
                    await HandleStartedProfileFailureAsync("dns_check", "dns_failed", isFinalProfile);
                    return false;
                }

                dnsTimer.Stop();
                SafeLogger.Performance("dns_check_ms", dnsTimer.ElapsedMilliseconds);
                SafeLogger.Info("profile_dns_success");
                ConnectionDiagnosticsState.Update(
                    ($"profile_{profileName}_dns", "success"),
                    ($"profile_{profileName}_https", "failed"));
                StageSuccess("dns_check");
                UpdateSummaryCards();
                await HandleStartedProfileFailureAsync("internet_check", "internet_check_failed", isFinalProfile);
                return false;
            }

            httpsTimer.Stop();
            SafeLogger.Performance("https_check_ms", httpsTimer.ElapsedMilliseconds);
            SafeLogger.Info("https_check_success");
            ConnectionDiagnosticsState.Update(
                ($"profile_{profileName}_dns", "success"),
                ($"profile_{profileName}_https", "success"),
                ("dns_check_success", true),
                ("dns_check_domain", "https-endpoint"),
                ("dns_check_attempt", 1));
            SafeLogger.Info("profile_dns_success");
            UpdateSummaryCards();
            StageSuccess("dns_check");
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
            MessageTextBlock.Text = "已安全连接";
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

    private async Task<bool> TryConnectSystemProxyFallbackAsync(NodeConfig nodeConfig)
    {
        const int proxyPort = 20809;
        BeginStage("system_proxy_fallback");
        MessageTextBlock.Text = "正在切换可用连接方式";
        SafeLogger.Info("system_proxy_fallback_start");

        await _singBoxService.StopAsync();
        var configPath = _configBuilder.BuildSystemProxyRuntimeConfig(nodeConfig, proxyPort);
        await _singBoxService.CheckConfigAsync(configPath);
        await _singBoxService.StartAsync(
            configPath,
            mode: "system-proxy",
            profile: "proxy",
            requiresAdministrator: false);

        if (!await _healthCheck.CheckLocalProxyAsync(proxyPort))
        {
            SafeLogger.Info("system_proxy_fallback_failed");
            SystemProxyService.Restore();
            await _singBoxService.StopAsync();
            StageFailed("system_proxy_fallback", "internet_check_failed");
            return false;
        }

        SystemProxyService.EnableLocalProxy(proxyPort);
        _isConnected = true;
        SafeLogger.Info("connect_success");
        SafeLogger.Info("system_proxy_fallback_success");
        SetStatus("已连接");
        MessageTextBlock.Text = "已安全连接";
        ConnectionDiagnosticsState.Update(
            ("tun_success", false),
            ("system_proxy_fallback", true),
            ("successful_profile", "system-proxy"),
            ("final_blocker", "none"));
        StageSuccess("system_proxy_fallback");
        return true;
    }

    private Task HandleStartedProfileFailureAsync(string stage, string errorCode, bool isFinalProfile)
    {
        SafeLogger.Diagnostic(stage, errorCode, _singBoxService.GetOutputSummary());
        StageFailed(stage, errorCode);

        if (isFinalProfile)
        {
            SetStatus("网络异常");
            ConnectionDiagnosticsState.Update(("final_blocker", errorCode));
            MessageTextBlock.Text = ToUserMessage(errorCode, "");
            _ = PreserveAndStopAsync(errorCode);
        }
        else
        {
            SetStatus("正在连接");
            MessageTextBlock.Text = "正在尝试其他可用连接方式";
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
            "auth_check" => "正在准备连接",
            "subscription_check" => "正在检查订阅状态",
            "device_register" => "正在准备连接",
            "devices_fetch" => "正在准备连接",
            "nodes_fetch" => "正在刷新可用地区",
            "network_environment_check" => "正在检查网络环境",
            "node_config" => "正在建立安全连接",
            "proxy_preflight" => "正在建立安全连接",
            "config_generate" => "正在建立安全连接",
            "sing_box_start" => "正在建立安全连接",
            "tun_check" => "正在确认网络状态",
            "route_check" => "正在确认网络状态",
            "dns_check" => "正在确认网络状态",
            "internet_check" => "正在确认网络状态",
            _ => MessageTextBlock.Text
        };
        UpdateSummaryCards();
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
                "auth_check" => "api_unreachable",
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

    private static bool ShouldContinueAfterPreflightFailure(string? errorCode) =>
        errorCode is "server_unreachable" or "hysteria2_outbound_failed" or "handshake_failed";

    private static string ToUserMessage(string errorCode, string fallback) => errorCode switch
    {
        "login_required" => "请先登录",
        "subscription_required" => SubscriptionGate.RequiredMessage,
        "subscription_expired" => SubscriptionGate.RequiredMessage,
        "subscription_unverified" => SubscriptionGate.UnverifiedMessage,
        "device_limit_reached" => "设备数量已达上限，请先移除旧设备或联系客服",
        "api_unreachable" => "客户端暂时无法连接服务，请检查本机网络或系统代理设置",
        "device_register_failed" => "设备注册失败，请稍后重试",
        "devices_fetch_failed" => "设备状态获取失败，请稍后重试",
        "nodes_fetch_failed" => "可用地区获取失败，请稍后重试",
        "node_not_selected" => "请先选择地区",
        "node_config_failed" => "当前地区暂时不可用，请切换地区重试",
        "hysteria2_outbound_failed" => "当前地区暂时不可用，请切换地区重试",
        "config_generate_failed" => "连接准备失败，请稍后重试",
        "sing_box_missing" => "客户端组件异常，请联系客服",
        "sing_box_config_invalid" => "连接准备失败，请稍后重试",
        "sing_box_exited" => "连接失败，请稍后重试",
        "sing_box_start_failed" => "连接失败，请稍后重试",
        "not_admin" => "开启安全连接需要 Windows 系统授权",
        "tun_permission_failed" => "系统授权未完成，请重新连接",
        "tun_adapter_missing" => "连接启动失败，请重新打开应用后重试",
        "dns_failed" => "网络连接失败，请稍后重试",
        "internet_check_failed" => "当前网络不可用，请检查 Wi-Fi 后重试",
        "server_unreachable" => "网络连接失败，请稍后重试",
        "handshake_failed" => "当前地区暂时不可用，请切换地区重试",
        "auth_password_wrong" => "连接失败，请稍后重试",
        "tls_or_sni_failed" => "连接失败，请稍后重试",
        "route_failed" => "连接启动失败，请稍后重试",
        "network_conflict" => "检测到其他代理或 VPN 正在运行，请关闭后重试",
        _ => string.IsNullOrWhiteSpace(fallback) || fallback == "网络错误，请稍后重试"
            ? "网络连接失败，请稍后重试"
            : fallback
    };

    private async Task DisconnectAsync()
    {
        var disconnect = Stopwatch.StartNew();
        ToggleConnectButtons(false);
        MessageTextBlock.Text = "正在断开连接";
        SystemProxyService.Restore();
        await _singBoxService.StopAsync();
        _isConnected = false;
        SetStatus("未连接");
        MessageTextBlock.Text = "连接已断开，网络已恢复";
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
        SelectedNodeFlagBadge.Child = CreateRegionFlagVisual(AppState.SelectedNode);
        SelectedNodeTextBlock.Text = GetRegionDisplayName(AppState.SelectedNode);
        LatencyTextBlock.Text = GetLatencyDisplay();
        UpdateModeDisplay();
        UpdateSummaryCards();

        if (!_isConnected && !_isConnecting && !SubscriptionGate.CanConnect(AppState.Subscription))
        {
            CircleButton.Content = string.Empty;
            SecondaryConnectButton.Content = "查看套餐";
            CircleButton.Background = BrushFromResource("WarningBrush");
            SecondaryConnectButton.Background = BrushFromResource("WarningBrush");
            CircleButton.IsEnabled = false;
            SecondaryConnectButton.IsEnabled = true;
            render.Stop();
            SafeLogger.Performance("home_render_ms", render.ElapsedMilliseconds);
            return;
        }

        CircleButton.Content = string.Empty;
        SecondaryConnectButton.Content = _isConnected ? "点击断开" : "点击连接";
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
            _allowExit = true;
            Close();
            return;
        }

        MessageTextBlock.Text = "无法发起系统授权，请稍后重试";
    }

    private bool PromptForElevationAndRestart()
    {
        var result = System.Windows.MessageBox.Show(
            "开启安全连接需要 Windows 系统授权，用于创建虚拟网卡并切换系统路由与 DNS。\n\n继续后会弹出 Windows 授权确认。",
            "闪连 VPN",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK);

        if (result != MessageBoxResult.OK)
        {
            MessageTextBlock.Text = "已取消连接";
            return false;
        }

        if (WindowsElevationService.RestartAsAdministrator())
        {
            _allowExit = true;
            Close();
            return true;
        }

        MessageTextBlock.Text = "无法发起系统授权，请稍后重试";
        return false;
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

        UpdateSummaryCards();
    }

    private void ToggleConnectButtons(bool enabled)
    {
        CircleButton.IsEnabled = true;
        SecondaryConnectButton.IsEnabled = enabled;
    }

    private void StartConnectingVisuals()
    {
        _connectingVisualFrame = 0;
        CircleButton.Background = BrushFromResource("AccentBrush");
        TopStatusTextBlock.Text = "正在连接";
        SecondaryConnectButton.Content = "正在连接";

        if (CircleButton.RenderTransform is ScaleTransform circleScale)
        {
            var pulse = CreateLoopAnimation(1.0, 1.035, 760);
            circleScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            circleScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        CircleButton.ApplyTemplate();

        if (CircleButton.Template.FindName("CircleGlow", CircleButton) is Ellipse glow)
        {
            glow.BeginAnimation(UIElement.OpacityProperty, CreateLoopAnimation(0.24, 0.82, 700));
        }

        if (CircleButton.Template.FindName("CircleSpinner", CircleButton) is Ellipse spinner)
        {
            spinner.RenderTransform = new RotateTransform(0);
            spinner.BeginAnimation(UIElement.OpacityProperty, CreateLoopAnimation(0.3, 1.0, 420));
            if (spinner.RenderTransform is RotateTransform rotateTransform)
            {
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, CreateSpinAnimation(1100));
            }
        }

        if (CircleButton.Template.FindName("CircleOuterRing", CircleButton) is Ellipse ring)
        {
            ring.BeginAnimation(UIElement.OpacityProperty, CreateLoopAnimation(0.82, 1.0, 760));
        }

        if (CircleButton.Template.FindName("CircleBoltWrap", CircleButton) is Viewbox boltWrap)
        {
            boltWrap.RenderTransform = new ScaleTransform(1, 1);
            if (boltWrap.RenderTransform is ScaleTransform boltScale)
            {
                var boltPulse = CreateLoopAnimation(1.0, 1.08, 620);
                boltScale.BeginAnimation(ScaleTransform.ScaleXProperty, boltPulse);
                boltScale.BeginAnimation(ScaleTransform.ScaleYProperty, boltPulse);
            }
        }

        _connectingVisualTimer.Start();
    }

    private void StopConnectingVisuals()
    {
        _connectingVisualTimer.Stop();
        _connectingVisualFrame = 0;

        if (CircleButton.RenderTransform is ScaleTransform circleScale)
        {
            circleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            circleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            circleScale.ScaleX = 1;
            circleScale.ScaleY = 1;
        }

        CircleButton.ApplyTemplate();

        if (CircleButton.Template.FindName("CircleGlow", CircleButton) is Ellipse glow)
        {
            glow.BeginAnimation(UIElement.OpacityProperty, null);
            glow.Opacity = 0.55;
        }

        if (CircleButton.Template.FindName("CircleSpinner", CircleButton) is Ellipse spinner)
        {
            spinner.RenderTransform = new RotateTransform(0);
            spinner.BeginAnimation(UIElement.OpacityProperty, null);
            spinner.Opacity = 0;
            if (spinner.RenderTransform is RotateTransform rotateTransform)
            {
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                rotateTransform.Angle = 0;
            }
        }

        if (CircleButton.Template.FindName("CircleOuterRing", CircleButton) is Ellipse ring)
        {
            ring.BeginAnimation(UIElement.OpacityProperty, null);
            ring.Opacity = 1;
        }

        if (CircleButton.Template.FindName("CircleBoltWrap", CircleButton) is Viewbox boltWrap)
        {
            boltWrap.RenderTransform = new ScaleTransform(1, 1);
            if (boltWrap.RenderTransform is ScaleTransform boltScale)
            {
                boltScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                boltScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                boltScale.ScaleX = 1;
                boltScale.ScaleY = 1;
            }
        }
    }

    private void ConnectingVisualTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isConnecting)
        {
            return;
        }

        _connectingVisualFrame = (_connectingVisualFrame + 1) % 3;
        var dots = new string('.', _connectingVisualFrame + 1);
        TopStatusTextBlock.Text = $"正在连接{dots}";
        SecondaryConnectButton.Content = $"正在连接{dots}";
    }

    private static DoubleAnimation CreateLoopAnimation(double from, double to, int milliseconds) =>
        new()
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

    private static DoubleAnimation CreateSpinAnimation(int milliseconds) =>
        new()
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            RepeatBehavior = RepeatBehavior.Forever
        };

    private void SetHealthSummary(string value)
    {
        UpdateSummaryCards();
    }

    private void UpdateSummaryCards()
    {
        if (TunHealthTextBlock is null)
        {
            return;
        }

        TunHealthTextBlock.Text = GetProtectionSummary();
        RouteHealthTextBlock.Text = GetConnectionQualitySummary();
        DnsHealthTextBlock.Text = GetSubscriptionSummary();
        HttpsHealthTextBlock.Text = GetCurrentModeLabel();
    }

    private string GetLatencyDisplay()
    {
        if (_isConnecting)
        {
            return "正在检测网络质量";
        }

        if (AppState.SelectedNode is not null
            && AppState.NodeLatencies.TryGetValue(AppState.SelectedNode.Id, out var latency)
            && latency.HasValue)
        {
            return _isConnected ? $"延迟 {latency.Value} ms" : $"预计延迟 {latency.Value} ms";
        }

        if (!SubscriptionGate.CanConnect(AppState.Subscription))
        {
            return "开通订阅后即可开始连接";
        }

        return _isConnected ? "已安全连接，网络状态稳定" : "连接前将自动检测网络质量";
    }

    private string GetProtectionSummary()
    {
        if (_isConnected)
        {
            return "已保护";
        }

        if (_isConnecting)
        {
            return "连接中";
        }

        if (AppState.ConnectionStatus == "网络异常")
        {
            return "异常";
        }

        return SubscriptionGate.CanConnect(AppState.Subscription) ? "未开启" : "待开通";
    }

    private string GetConnectionQualitySummary()
    {
        if (_isConnecting)
        {
            return "检测中";
        }

        if (!_isConnected)
        {
            return SubscriptionGate.CanConnect(AppState.Subscription) ? "待连接" : "待开通";
        }

        if (AppState.SelectedNode is not null
            && AppState.NodeLatencies.TryGetValue(AppState.SelectedNode.Id, out var latency)
            && latency.HasValue)
        {
            return latency.Value switch
            {
                <= 120 => "优秀",
                <= 200 => "良好",
                <= 320 => "稳定",
                _ => "较慢"
            };
        }

        return "检测中";
    }

    private static string GetSubscriptionSummary() => AppState.Subscription?.AccessState switch
    {
        SubscriptionAccessState.Active => "已订阅",
        SubscriptionAccessState.Expired => "已过期",
        SubscriptionAccessState.None => "未订阅",
        _ => "待验证"
    };

    private static string GetRegionDisplayName(VpnNode? node)
    {
        var countryCode = GetCountryCode(node);
        if (countryCode == "US")
        {
            return "美国";
        }

        if (countryCode == "JP")
        {
            return "日本";
        }

        if (countryCode == "SG")
        {
            return "新加坡";
        }

        if (countryCode == "HK")
        {
            return "香港";
        }

        if (countryCode == "KR")
        {
            return "韩国";
        }

        if (countryCode == "TW")
        {
            return "台湾";
        }

        var region = node?.DisplayCountry;
        if (string.IsNullOrWhiteSpace(region))
        {
            return "自动优选";
        }

        if (region.Contains("United States", StringComparison.OrdinalIgnoreCase) || region.Contains("USA", StringComparison.OrdinalIgnoreCase))
        {
            return "美国";
        }

        if (region.Contains("Japan", StringComparison.OrdinalIgnoreCase))
        {
            return "日本";
        }

        if (region.Contains("Singapore", StringComparison.OrdinalIgnoreCase))
        {
            return "新加坡";
        }

        if (region.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase))
        {
            return "香港";
        }

        if (region.Contains("Korea", StringComparison.OrdinalIgnoreCase))
        {
            return "韩国";
        }

        if (region.Contains("Taiwan", StringComparison.OrdinalIgnoreCase))
        {
            return "台湾";
        }

        region = region.Replace("US ", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("USA ", "", StringComparison.OrdinalIgnoreCase)
                       .Trim();
        return region;
    }

    private static UIElement CreateRegionFlagVisual(VpnNode? node)
    {
        var countryCode = GetCountryCode(node);
        return countryCode switch
        {
            "US" => CreateUsFlag(),
            "JP" => CreateJapanFlag(),
            "SG" => CreateSingaporeFlag(),
            "HK" => CreateHongKongFlag(),
            "KR" => CreateKoreaFlag(),
            "TW" => CreateTaiwanFlag(),
            "CA" => CreateCanadaFlag(),
            "GB" => CreateUkFlag(),
            "DE" => CreateGermanyFlag(),
            "FR" => CreateFranceFlag(),
            "NL" => CreateNetherlandsFlag(),
            "AU" => CreateAustraliaFlag(),
            "NZ" => CreateNewZealandFlag(),
            "IN" => CreateIndiaFlag(),
            "AE" => CreateUaeFlag(),
            "BR" => CreateBrazilFlag(),
            _ => CreateFallbackFlag(countryCode)
        };
    }

    private static string GetCountryCode(VpnNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(node.CountryCode))
        {
            var code = node.CountryCode.Trim().ToUpperInvariant();
            if (code.Length >= 2)
            {
                return code[..2];
            }
        }

        var region = node.DisplayCountry ?? node.Country ?? node.Name ?? string.Empty;
        return region switch
        {
            var text when text.Contains("United States", StringComparison.OrdinalIgnoreCase) || text.Contains("USA", StringComparison.OrdinalIgnoreCase) || text.Contains("美国", StringComparison.OrdinalIgnoreCase) => "US",
            var text when text.Contains("Japan", StringComparison.OrdinalIgnoreCase) || text.Contains("日本", StringComparison.OrdinalIgnoreCase) => "JP",
            var text when text.Contains("Singapore", StringComparison.OrdinalIgnoreCase) || text.Contains("新加坡", StringComparison.OrdinalIgnoreCase) => "SG",
            var text when text.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase) || text.Contains("香港", StringComparison.OrdinalIgnoreCase) => "HK",
            var text when text.Contains("Korea", StringComparison.OrdinalIgnoreCase) || text.Contains("韩国", StringComparison.OrdinalIgnoreCase) => "KR",
            var text when text.Contains("Taiwan", StringComparison.OrdinalIgnoreCase) || text.Contains("台湾", StringComparison.OrdinalIgnoreCase) => "TW",
            var text when text.Contains("Canada", StringComparison.OrdinalIgnoreCase) || text.Contains("加拿大", StringComparison.OrdinalIgnoreCase) => "CA",
            var text when text.Contains("United Kingdom", StringComparison.OrdinalIgnoreCase) || text.Contains("Britain", StringComparison.OrdinalIgnoreCase) || text.Contains("英国", StringComparison.OrdinalIgnoreCase) => "GB",
            var text when text.Contains("Germany", StringComparison.OrdinalIgnoreCase) || text.Contains("德国", StringComparison.OrdinalIgnoreCase) => "DE",
            var text when text.Contains("France", StringComparison.OrdinalIgnoreCase) || text.Contains("法国", StringComparison.OrdinalIgnoreCase) => "FR",
            var text when text.Contains("Netherlands", StringComparison.OrdinalIgnoreCase) || text.Contains("荷兰", StringComparison.OrdinalIgnoreCase) => "NL",
            var text when text.Contains("Australia", StringComparison.OrdinalIgnoreCase) || text.Contains("澳大利亚", StringComparison.OrdinalIgnoreCase) => "AU",
            var text when text.Contains("New Zealand", StringComparison.OrdinalIgnoreCase) || text.Contains("新西兰", StringComparison.OrdinalIgnoreCase) => "NZ",
            var text when text.Contains("India", StringComparison.OrdinalIgnoreCase) || text.Contains("印度", StringComparison.OrdinalIgnoreCase) => "IN",
            var text when text.Contains("UAE", StringComparison.OrdinalIgnoreCase) || text.Contains("Emirates", StringComparison.OrdinalIgnoreCase) || text.Contains("阿联酋", StringComparison.OrdinalIgnoreCase) => "AE",
            var text when text.Contains("Brazil", StringComparison.OrdinalIgnoreCase) || text.Contains("巴西", StringComparison.OrdinalIgnoreCase) => "BR",
            _ => string.Empty
        };
    }

    private static UIElement CreateUsFlag()
    {
        var grid = CreateHorizontalFlag(
            Brush(191, 10, 48),
            Brush(255, 255, 255),
            Brush(191, 10, 48),
            Brush(255, 255, 255),
            Brush(191, 10, 48),
            Brush(255, 255, 255),
            Brush(191, 10, 48));

        grid.Children.Add(new Border
        {
            Width = 13,
            Height = 11,
            Background = Brush(0, 40, 104),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        });

        grid.Children.Add(new Ellipse
        {
            Width = 2.2,
            Height = 2.2,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(3, 2, 0, 0)
        });

        grid.Children.Add(new Ellipse
        {
            Width = 2.2,
            Height = 2.2,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(7, 2, 0, 0)
        });

        grid.Children.Add(new Ellipse
        {
            Width = 2.2,
            Height = 2.2,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(5, 5, 0, 0)
        });

        return grid;
    }

    private static UIElement CreateJapanFlag()
    {
        var grid = new Grid { Background = Brush(255, 255, 255) };
        grid.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(188, 0, 45),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateSingaporeFlag()
    {
        var grid = CreateHorizontalFlag(Brush(220, 38, 38), Brush(255, 255, 255));
        grid.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(4, 2, 0, 0)
        });
        grid.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brush(220, 38, 38),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(5.5, 3, 0, 0)
        });
        return grid;
    }

    private static UIElement CreateHongKongFlag()
    {
        var grid = new Grid { Background = Brush(220, 38, 38) };
        grid.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateKoreaFlag()
    {
        var grid = new Grid { Background = Brush(255, 255, 255) };
        grid.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(220, 38, 38),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0)
        });
        grid.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(37, 99, 235),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return grid;
    }

    private static UIElement CreateTaiwanFlag()
    {
        var grid = new Grid { Background = Brush(220, 38, 38) };
        grid.Children.Add(new Border
        {
            Width = 14,
            Height = 9,
            Background = Brush(30, 64, 175),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        });
        return grid;
    }

    private static UIElement CreateCanadaFlag() => CreateVerticalFlag(Brush(220, 38, 38), Brush(255, 255, 255), Brush(220, 38, 38));
    private static UIElement CreateGermanyFlag() => CreateHorizontalFlag(Brush(17, 24, 39), Brush(220, 38, 38), Brush(245, 158, 11));
    private static UIElement CreateFranceFlag() => CreateVerticalFlag(Brush(37, 99, 235), Brush(255, 255, 255), Brush(220, 38, 38));
    private static UIElement CreateNetherlandsFlag() => CreateHorizontalFlag(Brush(220, 38, 38), Brush(255, 255, 255), Brush(37, 99, 235));

    private static UIElement CreateUkFlag()
    {
        var grid = new Grid { Background = Brush(30, 64, 175) };
        grid.Children.Add(new Border
        {
            Width = 4,
            Background = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        grid.Children.Add(new Border
        {
            Height = 4,
            Background = Brush(255, 255, 255),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        grid.Children.Add(new Border
        {
            Width = 2,
            Background = Brush(220, 38, 38),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        grid.Children.Add(new Border
        {
            Height = 2,
            Background = Brush(220, 38, 38),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateAustraliaFlag()
    {
        var grid = new Grid { Background = Brush(30, 64, 175) };
        grid.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateNewZealandFlag()
    {
        var grid = new Grid { Background = Brush(30, 64, 175) };
        grid.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = Brush(239, 68, 68),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateIndiaFlag()
    {
        var grid = CreateHorizontalFlag(Brush(245, 158, 11), Brush(255, 255, 255), Brush(34, 197, 94));
        grid.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Stroke = Brush(37, 99, 235),
            StrokeThickness = 1,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateUaeFlag()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });

        var left = new Border { Background = Brush(220, 38, 38) };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = CreateHorizontalFlag(Brush(34, 197, 94), Brush(255, 255, 255), Brush(17, 24, 39));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static UIElement CreateBrazilFlag()
    {
        var grid = new Grid { Background = Brush(22, 163, 74) };
        grid.Children.Add(new Polygon
        {
            Fill = Brush(245, 158, 11),
            Points = new PointCollection
            {
                new System.Windows.Point(17, 3),
                new System.Windows.Point(28, 11),
                new System.Windows.Point(17, 19),
                new System.Windows.Point(6, 11)
            }
        });
        grid.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brush(37, 99, 235),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateFallbackFlag(string countryCode)
    {
        var grid = new Grid { Background = Brush(241, 245, 249) };
        grid.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(countryCode) ? "?" : countryCode,
            Foreground = Brush(15, 23, 42),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        return grid;
    }

    private static Grid CreateHorizontalFlag(params SolidColorBrush[] colors)
    {
        var grid = new Grid();
        foreach (var _ in colors)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < colors.Length; index++)
        {
            var stripe = new Border { Background = colors[index] };
            Grid.SetRow(stripe, index);
            grid.Children.Add(stripe);
        }

        return grid;
    }

    private static Grid CreateVerticalFlag(params SolidColorBrush[] colors)
    {
        var grid = new Grid();
        foreach (var _ in colors)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < colors.Length; index++)
        {
            var stripe = new Border { Background = colors[index] };
            Grid.SetColumn(stripe, index);
            grid.Children.Add(stripe);
        }

        return grid;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(System.Windows.Media.Color.FromRgb(r, g, b));

    private void UpdateModeDisplay()
    {
        if (ModeTextBlock is null || ModeBadgeTextBlock is null)
        {
            return;
        }

        var label = GetCurrentModeLabel();
        var description = UserSettingsService.Current.ConnectionMode == ConnectionMode.Speed
            ? "当前模式：极速模式（推荐）"
            : "当前模式：全局模式";

        ModeTextBlock.Text = description;
        ModeBadgeTextBlock.Text = label;
    }

    private static string GetCurrentModeLabel() =>
        UserSettingsService.Current.ConnectionMode == ConnectionMode.Speed ? "极速模式" : "全局模式";

    private Task MaybeAutoConnectAsync()
    {
        if (_autoConnectAttempted || !UserSettingsService.Current.AutoConnect)
        {
            return Task.CompletedTask;
        }

        if (!TokenStore.HasToken() || _isConnected || _isConnecting || !SubscriptionGate.CanConnect(AppState.Subscription))
        {
            return Task.CompletedTask;
        }

        _autoConnectAttempted = true;
        ConnectButton_Click(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }

    private SolidColorBrush BrushFromResource(string key) =>
        TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 166, 255));

    private void SetActiveNav(System.Windows.Controls.Button activeButton)
    {
        var activeBackground = BrushFromResource("NavActiveBrush");
        var inactiveBackground = System.Windows.Media.Brushes.Transparent;
        var activeForeground = System.Windows.Media.Brushes.White;
        var inactiveForeground = BrushFromResource("SoftTextBrush");

        foreach (var button in new[]
                 {
                     HomeNavButton,
                     NodesNavButton,
                     SubscriptionNavButton,
                     AccountNavButton,
                     SettingsNavButton
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
        ContentFrame.Content = new SettingsPage(UpdateHome);
        nav.Stop();
        SafeLogger.Performance("navigation_switch_ms", nav.ElapsedMilliseconds);
    }

    private void DiagnosticsNav_Click(object sender, RoutedEventArgs e)
    {
        var nav = Stopwatch.StartNew();
        SetActiveNav(SettingsNavButton);
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
        ContentFrame.Content = new AccountPage(
            logout: () => _ = LogoutAndShowLoginAsync(),
            loginAnother: () => _ = LogoutAndShowLoginAsync());
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
            "检测到其他代理或 VPN 正在运行，请先关闭后再连接闪连 VPN。\n\n选择“确定”继续尝试，选择“取消”取消连接。",
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
            Icon = LoadTrayIcon(),
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

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app-icon.ico"))?.Stream;
            return iconStream is null
                ? System.Drawing.SystemIcons.Shield
                : new System.Drawing.Icon(iconStream);
        }
        catch
        {
            return System.Drawing.SystemIcons.Shield;
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RestoreFromExternalActivation()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Topmost = false;
        Activate();
        Focus();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _ = DisconnectAsync();
        Close();
    }

    private async Task LogoutAndShowLoginAsync()
    {
        _allowExit = true;

        if (_singBoxService.IsRunning)
        {
            await _singBoxService.StopAsync();
        }

        _isConnected = false;
        _isConnecting = false;
        _authService.Logout();
        NodeService.ClearCache();
        AppState.CurrentUser = null;
        AppState.Subscription = null;
        AppState.Devices = [];
        AppState.Nodes = [];
        AppState.SelectedNode = null;
        AppState.DeviceAllowed = true;
        AppState.NodeLatencies.Clear();
        AppState.ConnectionStatus = "未连接";

        var loginWindow = new LoginWindow();
        System.Windows.Application.Current.MainWindow = loginWindow;
        loginWindow.Show();
        Close();
    }
}
