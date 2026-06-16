using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;

namespace ShanlianVpn.Windows.Services;

public sealed record NetworkEnvironmentReport(
    bool DevProxyDetected,
    IReadOnlyList<int> DevProxyPorts,
    bool SystemProxyEnabled,
    bool TunConflictDetected,
    bool VirtualAdapterConflict,
    bool RouteConflict,
    bool DnsConflict,
    bool OtherSingBoxTunDetected)
{
    public bool NetworkConflict =>
        TunConflictDetected || VirtualAdapterConflict || RouteConflict || DnsConflict || OtherSingBoxTunDetected;
}

public sealed class NetworkEnvironmentService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static NetworkEnvironmentReport? _cachedReport;
    private static DateTimeOffset _cachedAt;
    private static readonly int[] DevProxyPorts = [7890, 7897, 10808, 10809, 20808];
    private static readonly string[] DevProxyProcesses = ["clash", "clash verge", "mihomo", "v2rayn", "v2ray", "sing-box"];
    private static readonly string[] ConflictNeedles =
    [
        "wintun",
        "wireguard",
        "warp",
        "openvpn",
        "tailscale",
        "anyconnect",
        "tap",
        "tun",
        "clash",
        "mihomo",
        "v2ray",
        "sing-box",
        "zerotier"
    ];

    public async Task<NetworkEnvironmentReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedReport is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
        {
            Log(_cachedReport);
            return _cachedReport;
        }

        await CacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedReport is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
            {
                Log(_cachedReport);
                return _cachedReport;
            }

            var ports = GetListeningDevProxyPorts();
            var devProxyDetected = ports.Count > 0 || DevProxyProcesses.Any(IsProcessRunning);
            var systemProxyEnabled = IsSystemProxyEnabled();
            var virtualAdapterConflict = HasVirtualAdapterConflict();
            var routeConflict = await HasRouteConflictAsync(cancellationToken);
            var dnsConflict = await HasDnsConflictAsync(cancellationToken);
            var otherSingBoxTun = IsProcessRunning("sing-box") && (virtualAdapterConflict || routeConflict);
            var tunConflict = virtualAdapterConflict || routeConflict || otherSingBoxTun;

            var report = new NetworkEnvironmentReport(
                devProxyDetected,
                ports,
                systemProxyEnabled,
                tunConflict,
                virtualAdapterConflict,
                routeConflict,
                dnsConflict,
                otherSingBoxTun);

            _cachedReport = report;
            _cachedAt = DateTimeOffset.UtcNow;
            Log(report);
            return report;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static IReadOnlyList<int> GetListeningDevProxyPorts()
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners
                .Where(endpoint => IPAddress.IsLoopback(endpoint.Address) && DevProxyPorts.Contains(endpoint.Port))
                .Select(endpoint => endpoint.Port)
                .Distinct()
                .Order()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool HasVirtualAdapterConflict()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    return false;
                }

                var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
                return !IsShanlian(text) && ConflictNeedles.Any(text.Contains);
            });
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasRouteConflictAsync(CancellationToken cancellationToken)
    {
        var output = await RunPowerShellAsync(
            "Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Select-Object InterfaceAlias,NextHop,RouteMetric | ConvertTo-Csv -NoTypeInformation",
            cancellationToken);
        return HasConflictNeedle(output);
    }

    private static async Task<bool> HasDnsConflictAsync(CancellationToken cancellationToken)
    {
        var output = await RunPowerShellAsync(
            "Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | Select-Object InterfaceAlias,ServerAddresses | ConvertTo-Csv -NoTypeInformation",
            cancellationToken);
        return HasConflictNeedle(output);
    }

    private static bool HasConflictNeedle(string value)
    {
        var lines = value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Any(line =>
        {
            var lower = line.ToLowerInvariant();
            return !IsShanlian(lower) && ConflictNeedles.Any(lower.Contains);
        });
    }

    private static bool IsShanlian(string value) =>
        value.Contains("shanlian") || value.Contains("闪连");

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcesses().Any(process =>
                process.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSystemProxyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            return key?.GetValue("ProxyEnable") is int enabled && enabled != 0;
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

    private static void Log(NetworkEnvironmentReport report)
    {
        SafeLogger.Info(report.DevProxyDetected ? "dev_proxy_detected" : "dev_proxy_not_detected");
        SafeLogger.Info(report.NetworkConflict ? "network_conflict_detected" : "network_conflict_not_detected");
        ConnectionDiagnosticsState.Update(
            ("dev_proxy_detected", report.DevProxyDetected),
            ("dev_proxy_ports", string.Join(",", report.DevProxyPorts)),
            ("system_proxy_enabled", report.SystemProxyEnabled),
            ("tun_conflict_detected", report.TunConflictDetected),
            ("virtual_adapter_conflict", report.VirtualAdapterConflict),
            ("route_conflict", report.RouteConflict),
            ("dns_conflict", report.DnsConflict),
            ("network_conflict", report.NetworkConflict));
    }
}
