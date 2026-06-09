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
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
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
        var names = new[] { "example.com", "cloudflare.com", "google.com" };
        for (var attempt = 0; attempt < 10; attempt++)
        {
            foreach (var name in names)
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken);
                    if (addresses.Length > 0)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Try the next resolver attempt.
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }

    public async Task<bool> WaitForTunAdapterAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (HasTunAdapter())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    public async Task WaitForRouteAndDnsSettleAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
    }

    public async Task<bool> WaitForRouteAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await HasVpnDefaultRouteAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    public async Task<bool> CheckInternetAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await TryHttpAsync("https://www.google.com/generate_204", cancellationToken)
                || await TryHttpAsync("https://cloudflare.com/cdn-cgi/trace", cancellationToken))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private static async Task<bool> TryHttpAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode || (int)response.StatusCode == 204;
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
                return text.Contains("shanlian")
                    || text.Contains("wintun")
                    || text.Contains("sing-box")
                    || text.Contains("utun")
                    || text.Contains("meta")
                    || text.Contains("tunnel");
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
                "Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 6 ifIndex,InterfaceAlias,NextHop,RouteMetric | ConvertTo-Csv -NoTypeInformation",
                cancellationToken);
            var lower = output.ToLowerInvariant();
            return (lower.Contains("shanlian")
                    || lower.Contains("wintun")
                    || lower.Contains("sing-box")
                    || lower.Contains("tunnel")
                    || lower.Contains("meta")
                    || lower.Contains("utun"))
                && lower.Contains("0.0.0.0");
        }
        catch
        {
            return HasTunAdapter();
        }
    }

    private static async Task<string> RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"",
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
