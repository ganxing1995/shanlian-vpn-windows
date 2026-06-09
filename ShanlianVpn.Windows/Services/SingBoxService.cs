using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class SingBoxService
{
    private Process? _process;
    private readonly StringBuilder _safeOutput = new();

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(string configPath)
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

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _safeOutput.Clear();
        _process.OutputDataReceived += (_, args) => CaptureOutput(args.Data);
        _process.ErrorDataReceived += (_, args) => CaptureOutput(args.Data);

        if (!_process.Start())
        {
            SafeLogger.Info("sing_box_start_failed");
            SafeLogger.Error("sing_box_start_failed");
            throw new ApiException("线路连接失败，请切换线路重试", errorCode: "sing_box_start_failed");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_process.HasExited)
        {
            var summary = GetSafeOutputSummary();
            var errorCode = ClassifyOutput(summary, "sing_box_start_failed");
            SafeLogger.Info("sing_box_start_failed");
            SafeLogger.Error(errorCode);
            SafeLogger.Diagnostic("sing_box_start", errorCode, summary);
            throw new ApiException(ToUserMessage(errorCode), errorCode: errorCode);
        }

        AppState.LastSingBoxSummary = GetSafeOutputSummary();
        SafeLogger.Info("sing_box_start_success");
        SafeLogger.Info("sing_box_started");
    }

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
                _process.WaitForExit(3000);
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            SafeLogger.Info("disconnect_success");
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void CaptureOutput(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var safe = SanitizeOutput(data);
        lock (_safeOutput)
        {
            if (_safeOutput.Length < 4000)
            {
                _safeOutput.AppendLine(safe);
            }
        }
    }

    private string GetSafeOutputSummary()
    {
        lock (_safeOutput)
        {
            return _safeOutput.Length == 0 ? "" : _safeOutput.ToString()[..Math.Min(_safeOutput.Length, 1000)];
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
        if (lower.Contains("permission denied") || lower.Contains("access is denied") || lower.Contains("wintun") || lower.Contains("administrator"))
        {
            return "tun_permission_failed";
        }

        if (lower.Contains("authentication failed") || lower.Contains("unauthorized") || lower.Contains("auth"))
        {
            return "auth_password_wrong";
        }

        if (lower.Contains("tls handshake") || lower.Contains("certificate") || lower.Contains("server name") || lower.Contains("sni"))
        {
            return "tls_or_sni_failed";
        }

        if (lower.Contains("timeout") || lower.Contains("unreachable") || lower.Contains("connection refused") || lower.Contains("no route to host"))
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
