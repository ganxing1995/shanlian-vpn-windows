using System.Net;
using System.Net.Http;

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
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("example.com", cancellationToken);
            if (addresses.Length == 0)
            {
                return HealthCheckResult.DnsFailed;
            }
        }
        catch
        {
            SafeLogger.Error("dns_failed");
            return HealthCheckResult.DnsFailed;
        }

        if (await TryHttpAsync("https://www.google.com/generate_204", cancellationToken)
            || await TryHttpAsync("https://cloudflare.com/cdn-cgi/trace", cancellationToken))
        {
            SafeLogger.Info("health_check_success");
            return HealthCheckResult.Success;
        }

        SafeLogger.Error("network_unavailable");
        return HealthCheckResult.NetworkUnavailable;
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
}

