using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

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
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
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
}
