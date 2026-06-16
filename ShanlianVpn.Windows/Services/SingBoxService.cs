using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class SingBoxService
{
    private Process? _process;
    private readonly StringBuilder _safeStdout = new();
    private readonly StringBuilder _safeStderr = new();
    private string _sessionId = "";
    private string _mode = "";
    private string _profile = "";
    private DateTimeOffset? _startTime;
    private DateTimeOffset? _exitTime;
    private int? _exitCode;

    public bool IsRunning => _process is { HasExited: false };
    public int? ProcessId => _process?.Id;
    public int? ExitCode => _exitCode;

    public async Task StartAsync(string configPath, string mode = "tun", string profile = "")
    {
        if (!File.Exists(AppPaths.SingBoxExePath))
        {
            SafeLogger.Info("sing_box_start_failed");
            SafeLogger.Error("sing_box_missing");
            throw new ApiException("缺少 VPN 核心文件，请联系客服", errorCode: "sing_box_missing");
        }

        if (!IsAdministrator())
        {
            SafeLogger.Info("sing_box_start_failed");
            SafeLogger.Error("not_admin");
            throw new ApiException("请以管理员身份运行闪连 VPN", errorCode: "not_admin");
        }

        if (IsRunning)
        {
            return;
        }

        SafeLogger.Info("sing_box_start_start");

        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.SingBoxExePath,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(AppPaths.SingBoxExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _sessionId = Guid.NewGuid().ToString("N")[..12];
        _mode = mode;
        _profile = profile;
        _startTime = DateTimeOffset.Now;
        _exitTime = null;
        _exitCode = null;
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _safeStdout.Clear();
        _safeStderr.Clear();
        _process.OutputDataReceived += (_, args) => CaptureOutput(args.Data, isError: false);
        _process.ErrorDataReceived += (_, args) => CaptureOutput(args.Data, isError: true);
        _process.Exited += (_, _) => RecordProcessExit();

        if (!_process.Start())
        {
            SafeLogger.Info("sing_box_start_failed");
            SafeLogger.Error("sing_box_start_failed");
            throw new ApiException("线路连接失败，请切换线路重试", errorCode: "sing_box_start_failed");
        }

        SafeLogger.Info($"sing_box_pid_{_process.Id}");
        if (!string.IsNullOrWhiteSpace(profile))
        {
            SafeLogger.Info($"sing_box_profile_{profile}");
        }

        WriteSessionState("running");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _ = PersistStartupSummaryAsync();

        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_process.HasExited)
        {
            var summary = GetSafeOutputSummary();
            var errorCode = ClassifyOutput(summary, "sing_box_exited");
            SafeLogger.Info("sing_box_start_failed");
            SafeLogger.Error(errorCode);
            SafeLogger.Diagnostic("sing_box_start", errorCode, summary);
            throw new ApiException(ToUserMessage(errorCode), errorCode: errorCode);
        }

        AppState.LastSingBoxSummary = GetSafeOutputSummary();
        SafeLogger.Info("sing_box_start_success");
        SafeLogger.Info("sing_box_started");
    }

    public string GetOutputSummary() => GetSafeOutputSummary();
    public string GetStdoutSummary() => GetSafeSummary(_safeStdout);
    public string GetStderrSummary() => GetSafeSummary(_safeStderr);

    public async Task CheckConfigAsync(string configPath)
    {
        SafeLogger.Info("sing_box_check_start");

        if (!File.Exists(AppPaths.SingBoxExePath))
        {
            SafeLogger.Info("sing_box_check_failed");
            SafeLogger.Error("sing_box_missing");
            throw new ApiException("缺少 VPN 核心文件，请联系客服", errorCode: "sing_box_missing");
        }

        if (!File.Exists(configPath))
        {
            SafeLogger.Info("sing_box_check_failed");
            SafeLogger.Error("sing_box_config_invalid");
            throw new ApiException("VPN 配置无效，请联系客服", errorCode: "sing_box_config_invalid");
        }

        var result = await RunSingBoxCommandAsync($"check -c \"{configPath}\"", TimeSpan.FromSeconds(30));
        AppState.LastSingBoxSummary = result.SafeSummary;

        if (result.ExitCode != 0)
        {
            var code = ClassifyOutput(result.SafeSummary, "sing_box_config_invalid");
            SafeLogger.Info("sing_box_check_failed");
            SafeLogger.Error(code);
            SafeLogger.Diagnostic("sing_box_check", code, result.SafeSummary);
            throw new ApiException(ToUserMessage(code), errorCode: code);
        }

        SafeLogger.Info("sing_box_check_success");
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            SafeLogger.Info("disconnect_success");
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch
                {
                    // Disconnect should never freeze the UI while waiting for process teardown.
                }
            }
        }
        finally
        {
            process?.Dispose();
            SafeLogger.Info("disconnect_success");
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void CaptureOutput(string? data, bool isError)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var safe = SanitizeOutput(data);
        var target = isError ? _safeStderr : _safeStdout;
        lock (target)
        {
            if (target.Length < 8000)
            {
                target.AppendLine(safe);
            }
        }
    }

    private string GetSafeOutputSummary()
    {
        var stdout = GetSafeSummary(_safeStdout);
        var stderr = GetSafeSummary(_safeStderr);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return stderr;
        }

        if (string.IsNullOrWhiteSpace(stderr))
        {
            return stdout;
        }

        return $"stdout: {stdout} stderr: {stderr}"[..Math.Min($"stdout: {stdout} stderr: {stderr}".Length, 1000)];
    }

    private static string GetSafeSummary(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.Length == 0 ? "" : builder.ToString()[..Math.Min(builder.Length, 1000)];
        }
    }

    private async Task PersistStartupSummaryAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            var summary = GetSafeOutputSummary();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                var code = ClassifyOutput(summary, "none");
                SafeLogger.Diagnostic("sing_box_runtime", code, summary);
                WriteSessionState(IsRunning ? "running" : "exited");
            }
        }
        catch
        {
            // Background diagnostics must never affect the connection.
        }
    }

    private void RecordProcessExit()
    {
        try
        {
            _exitTime = DateTimeOffset.Now;
            _exitCode = _process?.ExitCode;
            AppState.LastSingBoxSummary = GetSafeOutputSummary();
            SafeLogger.Info("sing_box_exited");
            SafeLogger.Diagnostic("sing_box_exit", ClassifyOutput(AppState.LastSingBoxSummary, "sing_box_exited"), AppState.LastSingBoxSummary);
            WriteSessionState("exited");
        }
        catch
        {
            // Exit diagnostics are best effort.
        }
    }

    private void WriteSessionState(string state)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var payload = new Dictionary<string, object?>
            {
                ["session_id"] = _sessionId,
                ["mode"] = _mode,
                ["profile"] = _profile,
                ["state"] = state,
                ["pid"] = _process?.Id,
                ["start_time"] = _startTime?.ToString("O"),
                ["exit_time"] = _exitTime?.ToString("O"),
                ["exit_code"] = _exitCode,
                ["stdout_summary"] = GetStdoutSummary(),
                ["stderr_summary"] = GetStderrSummary(),
                ["combined_summary"] = GetSafeOutputSummary()
            };

            File.WriteAllText(AppPaths.SingBoxSessionPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            ConnectionDiagnosticsState.Update(
                ("latest_session_id", _sessionId),
                ("latest_mode", _mode),
                ("latest_profile", _profile),
                ("sing_box_state", state),
                ("sing_box_pid", _process?.Id),
                ("sing_box_exit_code", _exitCode),
                ("sing_box_stdout_summary", GetStdoutSummary()),
                ("sing_box_stderr_summary", GetStderrSummary()));
        }
        catch
        {
            // Session diagnostics must never affect the VPN client.
        }
    }

    private static async Task<(int ExitCode, string SafeSummary)> RunSingBoxCommandAsync(string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.SingBoxExePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(AppPaths.SingBoxExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => AppendSafe(output, args.Data);
        process.ErrorDataReceived += (_, args) => AppendSafe(output, args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup for a hung validation command.
            }

            return (-1, "timeout");
        }

        return (process.ExitCode, output.ToString()[..Math.Min(output.Length, 1000)]);
    }

    private static void AppendSafe(StringBuilder output, string? data)
    {
        if (string.IsNullOrWhiteSpace(data) || output.Length >= 4000)
        {
            return;
        }

        output.AppendLine(SanitizeOutput(data));
    }

    private static string SanitizeOutput(string value)
    {
        var sanitized = value;
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "(?i)(password|auth_password|token|authorization)\\s*[:=]\\s*\\S+", "$1=[redacted]");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "[A-Za-z0-9+/=]{32,}", "[redacted]");
        return sanitized;
    }

    private static string ClassifyOutput(string output, string fallback)
    {
        var lower = output.ToLowerInvariant();
        if (lower.Contains("route add failed") || lower.Contains("network unreachable"))
        {
            return "route_failed";
        }

        if (lower.Contains("permission denied") || lower.Contains("access is denied") || lower.Contains("wintun") || lower.Contains("administrator"))
        {
            return "tun_permission_failed";
        }

        if (lower.Contains("authentication failed") || lower.Contains("unauthorized"))
        {
            return "auth_password_wrong";
        }

        if (lower.Contains("handshake failed"))
        {
            return "handshake_failed";
        }

        if (lower.Contains("tls handshake") || lower.Contains("certificate") || lower.Contains("server name") || lower.Contains("sni"))
        {
            return "tls_or_sni_failed";
        }

        if (lower.Contains("timeout")
            || lower.Contains("unreachable")
            || lower.Contains("connection refused")
            || lower.Contains("no route to host")
            || lower.Contains("context deadline exceeded"))
        {
            return "server_unreachable";
        }

        if (lower.Contains("dns"))
        {
            return lower.Contains("invalid") || lower.Contains("legacy") ? "sing_box_config_invalid" : "dns_failed";
        }

        if (lower.Contains("invalid") || lower.Contains("fatal") || lower.Contains("deprecated"))
        {
            return "sing_box_config_invalid";
        }

        return fallback;
    }

    public static string ToUserMessage(string errorCode) => errorCode switch
    {
        "sing_box_missing" => "缺少 VPN 核心文件，请联系客服",
        "sing_box_config_invalid" => "VPN 配置无效，请联系客服",
        "not_admin" => "请以管理员身份运行闪连 VPN",
        "tun_permission_failed" => "请以管理员身份运行闪连 VPN",
        "auth_password_wrong" => "线路认证失败，请切换线路重试",
        "tls_or_sni_failed" => "线路连接失败，请切换线路重试",
        "server_unreachable" => "线路服务器不可达，请切换线路重试",
        "dns_failed" => "VPN 已启动，但网络解析异常",
        _ => "线路连接失败，请切换线路重试"
    };
}
