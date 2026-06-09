using System.Diagnostics;
using System.Text;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class Hysteria2PreflightService
{
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

        var configPath = _configBuilder.BuildProxyPreflightConfig(nodeConfig);
        await _singBoxService.CheckConfigAsync(configPath);
        SafeLogger.Info("proxy_preflight_check_success");

        try
        {
            await _singBoxService.StartAsync(configPath, mode: "proxy-preflight", profile: "preflight");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            if (await CurlViaProxyAsync("https://www.google.com/generate_204", cancellationToken)
                || await CurlViaProxyAsync("https://cloudflare.com/cdn-cgi/trace", cancellationToken))
            {
                SafeLogger.Info("proxy_preflight_success");
                ConnectionDiagnosticsState.Update(
                    ("proxy_preflight_success", true),
                    ("proxy_preflight_blocker", "none"),
                    ("proxy_preflight_summary", _singBoxService.GetOutputSummary()));
                return;
            }

            var detail = Classify(_singBoxService.GetOutputSummary());
            SafeLogger.Info("proxy_preflight_failed");
            SafeLogger.Error("hysteria2_outbound_failed");
            SafeLogger.Diagnostic("proxy_preflight", detail, _singBoxService.GetOutputSummary());
            ConnectionDiagnosticsState.Update(
                ("proxy_preflight_success", false),
                ("proxy_preflight_blocker", "hysteria2_outbound_failed"),
                ("proxy_preflight_detail", detail),
                ("proxy_preflight_summary", _singBoxService.GetOutputSummary()));
            throw new ApiException("当前线路不可达，请切换线路重试", errorCode: "hysteria2_outbound_failed");
        }
        catch (ApiException ex) when (ex.ErrorCode is not "sing_box_missing" and not "sing_box_config_invalid")
        {
            var detail = Classify($"{ex.ErrorCode} {_singBoxService.GetOutputSummary()}");
            SafeLogger.Info("proxy_preflight_failed");
            SafeLogger.Error("hysteria2_outbound_failed");
            SafeLogger.Diagnostic("proxy_preflight", detail, _singBoxService.GetOutputSummary());
            ConnectionDiagnosticsState.Update(
                ("proxy_preflight_success", false),
                ("proxy_preflight_blocker", "hysteria2_outbound_failed"),
                ("proxy_preflight_detail", detail),
                ("proxy_preflight_summary", _singBoxService.GetOutputSummary()),
                ("final_blocker", "hysteria2_outbound_failed"));
            throw new ApiException("当前线路不可达，请切换线路重试", errorCode: "hysteria2_outbound_failed");
        }
        finally
        {
            _singBoxService.Stop();
        }
    }

    private static async Task<bool> CurlViaProxyAsync(string url, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"--max-time 20 -L -sS -o NUL -w \"%{{http_code}}\" -x socks5h://127.0.0.1:20808 \"{url}\"",
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
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
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
        if (lower.Contains("authentication failed") || lower.Contains("unauthorized"))
        {
            return "auth_password_wrong";
        }

        if (lower.Contains("tls handshake") || lower.Contains("certificate") || lower.Contains("server name") || lower.Contains("sni"))
        {
            return "tls_or_sni_failed";
        }

        if (lower.Contains("handshake failed"))
        {
            return "handshake_failed";
        }

        if (lower.Contains("timeout") || lower.Contains("unreachable") || lower.Contains("context deadline exceeded") || lower.Contains("no route to host"))
        {
            return "server_unreachable";
        }

        return "hysteria2_outbound_failed";
    }

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
