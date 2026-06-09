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
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("example.com", cancellationToken);
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CheckInternetAsync(CancellationToken cancellationToken = default) =>
        await TryHttpAsync("https://www.google.com/generate_204", cancellationToken)
        || await TryHttpAsync("https://cloudflare.com/cdn-cgi/trace", cancellationToken);

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
