using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class LatencyService
{
    private static readonly string[] ProbeUrls =
    [
        "http://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "https://www.google.com/generate_204"
    ];
    private static readonly TimeSpan ProbeStartupTimeout = TimeSpan.FromSeconds(4);

    public async Task<int?> MeasureAsync(NodeConfig config, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AppPaths.SingBoxExePath))
        {
            return null;
        }

        var listenPort = GetFreeTcpPort();
        var configPath = Path.Combine(AppPaths.AppDataDirectory, $"latency-probe-{Guid.NewGuid():N}.json");
        Process? process = null;

        try
        {
            AppPaths.EnsureDirectories();
            await File.WriteAllTextAsync(configPath, BuildProbeConfig(config, listenPort), cancellationToken);

            process = StartSingBoxProbe(configPath);
            if (!await WaitForProxyReadyAsync(listenPort, cancellationToken))
            {
                return null;
            }

            foreach (var url in ProbeUrls)
            {
                await CurlViaProxyAsync(listenPort, url, cancellationToken, includeTiming: false);

                var samples = new List<int>(capacity: 3);
                for (var index = 0; index < 3; index++)
                {
                    var latency = await CurlViaProxyAsync(listenPort, url, cancellationToken, includeTiming: true);
                    if (latency.HasValue)
                    {
                        samples.Add(latency.Value);
                    }
                }

                if (samples.Count > 0)
                {
                    return samples.Min();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
                finally
                {
                    process.Dispose();
                }
            }

            try
            {
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static async Task<int?> CurlViaProxyAsync(
        int listenPort,
        string url,
        CancellationToken cancellationToken,
        bool includeTiming)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = includeTiming
                ? $"--connect-timeout 4 --max-time 8 -L -sS -o NUL -w \"%{{http_code}} %{{time_starttransfer}}\" -x socks5h://127.0.0.1:{listenPort} \"{url}\""
                : $"--connect-timeout 4 --max-time 8 -L -sS -o NUL -w \"%{{http_code}}\" -x socks5h://127.0.0.1:{listenPort} \"{url}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }

        var statusText = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return null;
        }

        if (!includeTiming)
        {
            return int.TryParse(statusText.Trim(), out var warmupStatus) && warmupStatus is >= 200 and < 400
                ? 1
                : null;
        }

        var parts = statusText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var statusCode)
            || statusCode is < 200 or >= 400
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var totalSeconds))
        {
            return null;
        }

        return Math.Max(1, (int)Math.Round(totalSeconds * 1000));
    }

    private static Process StartSingBoxProbe(string configPath)
    {
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

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private static string BuildProbeConfig(NodeConfig nodeConfig, int listenPort)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = nodeConfig.Server,
            ["server_port"] = nodeConfig.ServerPort,
            ["password"] = nodeConfig.Password,
            ["tls"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = nodeConfig.TlsServerName,
                ["insecure"] = nodeConfig.TlsInsecure
            }
        };

        var portRanges = NormalizePortRanges(nodeConfig.FallbackPorts);
        if (portRanges.Count > 0)
        {
            outbound["server_ports"] = portRanges;
        }

        if (!string.IsNullOrWhiteSpace(nodeConfig.ObfsType) && !string.IsNullOrWhiteSpace(nodeConfig.ObfsPassword))
        {
            outbound["obfs"] = new Dictionary<string, object?>
            {
                ["type"] = nodeConfig.ObfsType,
                ["password"] = nodeConfig.ObfsPassword
            };
        }

        if (nodeConfig.UpMbps > 0)
        {
            outbound["up_mbps"] = nodeConfig.UpMbps;
        }

        if (nodeConfig.DownMbps > 0)
        {
            outbound["down_mbps"] = nodeConfig.DownMbps;
        }

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["level"] = "warn",
                ["disabled"] = false,
                ["timestamp"] = false
            },
            ["dns"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "local", ["tag"] = "local" }
                },
                ["final"] = "local",
                ["strategy"] = "prefer_ipv4"
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = listenPort
                }
            },
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" },
                new Dictionary<string, object?> { ["type"] = "block", ["tag"] = "block" }
            },
            ["route"] = new Dictionary<string, object?>
            {
                ["default_domain_resolver"] = "local",
                ["final"] = "proxy"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<string> NormalizePortRanges(IReadOnlyList<string> ports)
    {
        var result = new List<string>();
        foreach (var rawPort in ports)
        {
            var value = rawPort.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            value = value.Replace('-', ':');
            var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out var singlePort))
            {
                result.Add($"{singlePort}:{singlePort}");
                continue;
            }

            if (parts.Length == 2
                && int.TryParse(parts[0], out var start)
                && int.TryParse(parts[1], out var end)
                && start > 0
                && end > 0
                && start <= end)
            {
                result.Add($"{start}:{end}");
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForProxyReadyAsync(int listenPort, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ProbeStartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromMilliseconds(300));
                await client.ConnectAsync(IPAddress.Loopback, listenPort, waitCts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort timeout cleanup.
        }
    }
}
