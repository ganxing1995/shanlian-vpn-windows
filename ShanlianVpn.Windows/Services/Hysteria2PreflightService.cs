using System.Diagnostics;
using System.Text;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class Hysteria2PreflightService
{
    private static readonly TimeSpan PreflightCacheDuration = TimeSpan.FromMinutes(5);
    private readonly ConfigBuilder _configBuilder;
    private readonly SingBoxService _singBoxService;

    public Hysteria2PreflightService(ConfigBuilder configBuilder, SingBoxService singBoxService)
    {
        _configBuilder = configBuilder;
        _singBoxService = singBoxService;
    }

    public async Task RunAsync(NodeConfig nodeConfig, CancellationToken cancellationToken = default)
    {
        SafeLogger.Info("proxy_preflight_start");
        ConnectionDiagnosticsState.Update(
            ("proxy_preflight_started", true),
            ("proxy_preflight_success", false),
            ("proxy_preflight_blocker", ""));

        var preflightKey = $"{nodeConfig.Server}:{nodeConfig.ServerPort}:{nodeConfig.TlsServerName}";
        if (string.Equals(AppState.LastPreflightKey, preflightKey, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow - AppState.LastPreflightAt < PreflightCacheDuration)
        {
            SafeLogger.Info("proxy_preflight_cache_hit");
            ConnectionDiagnosticsState.Update(
                ("proxy_preflight_success", true),
                ("proxy_preflight_blocker", "none"),
                ("proxy_preflight_summary", "cached"));
            return;
        }

        var configPath = _configBuilder.BuildProxyPreflightConfig(nodeConfig);

        try
        {
            await _singBoxService.CheckConfigAsync(configPath);
            SafeLogger.Info("proxy_preflight_check_success");

            await _singBoxService.StartAsync(
                configPath,
                mode: "proxy-preflight",
                profile: "preflight",
                requiresAdministrator: false);
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);

            if (await CurlViaProxyAsync("https://www.google.com/generate_204", cancellationToken)
                || await CurlViaProxyAsync("https://cloudflare.com/cdn-cgi/trace", cancellationToken))
            {
                SafeLogger.Info("proxy_preflight_success");
                AppState.LastPreflightKey = preflightKey;
                AppState.LastPreflightAt = DateTimeOffset.UtcNow;
                ConnectionDiagnosticsState.Update(
                    ("proxy_preflight_success", true),
                    ("proxy_preflight_blocker", "none"),
                    ("proxy_preflight_summary", _singBoxService.GetOutputSummary()));
                return;
            }

            ThrowPreflightFailure(Classify(_singBoxService.GetOutputSummary()));
        }
        catch (ApiException ex) when (ex.ErrorCode is not "sing_box_missing")
        {
            ThrowPreflightFailure(Classify($"{ex.ErrorCode} {_singBoxService.GetOutputSummary()}"));
        }
        finally
        {
            await _singBoxService.StopAsync();
        }
    }

    private void ThrowPreflightFailure(string blocker)
    {
        SafeLogger.Info("proxy_preflight_failed");
        SafeLogger.Error(blocker);
        SafeLogger.Diagnostic("proxy_preflight", blocker, _singBoxService.GetOutputSummary());
        ConnectionDiagnosticsState.Update(
            ("proxy_preflight_success", false),
            ("proxy_preflight_blocker", blocker),
            ("proxy_preflight_detail", blocker),
            ("proxy_preflight_summary", _singBoxService.GetOutputSummary()),
            ("final_blocker", blocker));
        throw new ApiException(ToUserMessage(blocker), errorCode: blocker);
    }

    private static async Task<bool> CurlViaProxyAsync(string url, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"--max-time 5 -L -sS -o NUL -w \"%{{http_code}}\" -x socks5h://127.0.0.1:20808 \"{url}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort timeout cleanup.
            }

            ConnectionDiagnosticsState.Update(("proxy_preflight_curl_summary", "timeout"));
            return false;
        }

        var summary = Sanitize(output.ToString());
        ConnectionDiagnosticsState.Update(("proxy_preflight_curl_summary", summary));
        SafeLogger.Diagnostic("proxy_preflight_curl", process.ExitCode == 0 ? "none" : "hysteria2_outbound_failed", summary);

        return process.ExitCode == 0
            && int.TryParse(summary.Trim(), out var statusCode)
            && statusCode is >= 200 and < 400;
    }

    private static string Classify(string summary)
    {
        var lower = summary.ToLowerInvariant();
        if (lower.Contains("auth_password_wrong") || lower.Contains("authentication failed") || lower.Contains("unauthorized"))
        {
            return "auth_password_wrong";
        }

        if (lower.Contains("tls_or_sni_failed") || lower.Contains("tls handshake") || lower.Contains("certificate") || lower.Contains("server name") || lower.Contains("sni"))
        {
            return "tls_or_sni_failed";
        }

        if (lower.Contains("handshake_failed") || lower.Contains("handshake failed"))
        {
            return "handshake_failed";
        }

        if (lower.Contains("server_unreachable") || lower.Contains("timeout") || lower.Contains("unreachable") || lower.Contains("context deadline exceeded") || lower.Contains("no route to host"))
        {
            return "server_unreachable";
        }

        return "hysteria2_outbound_failed";
    }

    private static string ToUserMessage(string blocker) => blocker switch
    {
        "auth_password_wrong" => "节点认证失败，请联系客服",
        "tls_or_sni_failed" => "节点安全连接失败，请联系客服",
        "server_unreachable" => "服务器不可达，请切换线路重试",
        "handshake_failed" => "当前线路不可达，请切换线路重试",
        _ => "当前线路不可达，请切换线路重试"
    };

    private static void Append(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || output.Length > 1000)
        {
            return;
        }

        output.Append(line);
    }

    private static string Sanitize(string value)
    {
        var sanitized = value;
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            "(?i)(password|auth_password|token|authorization|configJson|raw_config)\\s*[:=]\\s*\\S+",
            "$1=[redacted]");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "[A-Za-z0-9+/=]{32,}", "[redacted]");
        return sanitized.Trim();
    }
}
