using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text;

namespace ShanlianVpn.Windows.Services;

public enum HealthCheckResult
{
    Success,
    DnsFailed,
    NetworkUnavailable
}

public sealed class ConnectivityHealthCheck
{
    private static readonly string[] TunAdapterNeedles = ["shanlian", "wintun", "sing-box", "meta", "tunnel"];
    private static readonly string[] DnsNames = ["www.gstatic.com", "www.google.com", "example.com"];
    private static readonly string[] HttpsUrls =
    [
        "http://www.gstatic.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "https://www.google.com/generate_204"
    ];

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await CheckDnsAsync(cancellationToken))
        {
            return HealthCheckResult.DnsFailed;
        }

        if (await CheckInternetAsync(cancellationToken))
        {
            SafeLogger.Info("health_check_success");
            return HealthCheckResult.Success;
        }

        SafeLogger.Error("network_unavailable");
        return HealthCheckResult.NetworkUnavailable;
    }

    public async Task<bool> CheckDnsAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            foreach (var name in DnsNames)
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken);
                    if (addresses.Length > 0)
                    {
                        SafeLogger.Info("dns_check_success");
                        ConnectionDiagnosticsState.Update(
                            ("dns_check_success", true),
                            ("dns_check_domain", name),
                            ("dns_check_attempt", attempt + 1));
                        return true;
                    }
                }
                catch
                {
                    // Try the next resolver attempt.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        SafeLogger.Info("dns_check_failed");
        ConnectionDiagnosticsState.Update(("dns_check_success", false));
        return false;
    }

    public async Task<bool> WaitForTunAdapterAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (HasTunAdapter())
            {
                ConnectionDiagnosticsState.Update(("tun_adapter_exists", true));
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        }

        ConnectionDiagnosticsState.Update(("tun_adapter_exists", false));
        return false;
    }

    public async Task WaitForRouteAndDnsSettleAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await HasVpnDefaultRouteAsync(cancellationToken) && await HasDnsServersAsync(cancellationToken))
            {
                ConnectionDiagnosticsState.Update(("route_dns_settled", true));
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        ConnectionDiagnosticsState.Update(("route_dns_settled", false));
    }

    public async Task<bool> WaitForRouteAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await HasVpnDefaultRouteAsync(cancellationToken))
            {
                ConnectionDiagnosticsState.Update(("route_detect_success", true));
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        }

        ConnectionDiagnosticsState.Update(("route_detect_success", false));
        return false;
    }

    public async Task<bool> CheckInternetAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var url in HttpsUrls)
            {
                if (await TryHttpAsync(url, cancellationToken))
                {
                    SafeLogger.Info("https_check_success");
                    ConnectionDiagnosticsState.Update(
                        ("https_check_success", true),
                        ("https_check_url", url));
                    return true;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        SafeLogger.Info("https_check_failed");
        ConnectionDiagnosticsState.Update(("https_check_success", false));
        return false;
    }

    public async Task<bool> CheckLocalProxyAsync(int port, CancellationToken cancellationToken = default)
    {
        foreach (var url in HttpsUrls)
        {
            if (await TryProxyCurlAsync(url, port, cancellationToken))
            {
                SafeLogger.Info("local_proxy_check_success");
                ConnectionDiagnosticsState.Update(
                    ("local_proxy_check_success", true),
                    ("local_proxy_check_url", url),
                    ("local_proxy_port", port));
                return true;
            }
        }

        SafeLogger.Info("local_proxy_check_failed");
        ConnectionDiagnosticsState.Update(
            ("local_proxy_check_success", false),
            ("local_proxy_port", port));
        return false;
    }

    private static async Task<bool> TryHttpAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 204)
            {
                return true;
            }
        }
        catch
        {
            // Fall back to curl below; it matches the QA path and Windows proxy/TUN behavior better.
        }

        return await TryCurlAsync(url, cancellationToken);
    }

    private static async Task<bool> TryCurlAsync(string url, CancellationToken cancellationToken)
    {
        return await TryCurlProcessAsync(
            $"--noproxy \"*\" -4 --connect-timeout 2 --max-time 4 --retry 0 -L -sS -o NUL -w \"%{{http_code}}\" \"{url}\"",
            cancellationToken);
    }

    private static async Task<bool> TryProxyCurlAsync(string url, int port, CancellationToken cancellationToken)
    {
        return await TryCurlProcessAsync(
            $"-x \"http://127.0.0.1:{port}\" --connect-timeout 2 --max-time 5 --retry 0 -L -sS -o NUL -w \"%{{http_code}}\" \"{url}\"",
            cancellationToken);
    }

    private static async Task<bool> TryCurlProcessAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "curl.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, args) => Append(output, args.Data);
            process.ErrorDataReceived += (_, args) => Append(output, args.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup.
                }

                return false;
            }

            var text = output.ToString().Trim();
            return process.ExitCode == 0
                && int.TryParse(text, out var statusCode)
                && statusCode is >= 200 and < 400;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasTunAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            {
                var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
                return TunAdapterNeedles.Any(text.Contains);
            });
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasVpnDefaultRouteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunPowerShellAsync(
                "Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 8 DestinationPrefix,ifIndex,InterfaceAlias,NextHop,RouteMetric | ConvertTo-Csv -NoTypeInformation",
                cancellationToken);
            var lower = output.ToLowerInvariant();
            ConnectionDiagnosticsState.Update(("default_route_summary", output));
            return TunAdapterNeedles.Any(lower.Contains);
        }
        catch
        {
            return HasTunAdapter();
        }
    }

    private static async Task<bool> HasDnsServersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunPowerShellAsync(
                "Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | Select-Object InterfaceAlias,ServerAddresses | ConvertTo-Csv -NoTypeInformation",
                cancellationToken);
            ConnectionDiagnosticsState.Update(("dns_server_summary", output));
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        return output.ToString();
    }

    private static void Append(StringBuilder output, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && output.Length < 2000)
        {
            output.Append(value).Append(' ');
        }
    }
}
