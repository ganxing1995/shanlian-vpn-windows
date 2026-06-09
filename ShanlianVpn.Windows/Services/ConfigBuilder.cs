using System.IO;
using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class ConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string BuildRuntimeConfig(NodeConfig nodeConfig, VpnConfigProfile profile = VpnConfigProfile.StrictRoute)
    {
        AppPaths.EnsureDirectories();
        SafeLogger.Info($"config_profile_{GetProfileName(profile)}");

        var hysteriaOutbound = new Dictionary<string, object?>
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

        var serverPorts = NormalizePortRanges(nodeConfig.FallbackPorts);
        if (serverPorts.Count > 0)
        {
            hysteriaOutbound["server_ports"] = serverPorts;
        }

        if (!string.IsNullOrWhiteSpace(nodeConfig.ObfsType) && !string.IsNullOrWhiteSpace(nodeConfig.ObfsPassword))
        {
            hysteriaOutbound["obfs"] = new Dictionary<string, object?>
            {
                ["type"] = nodeConfig.ObfsType,
                ["password"] = nodeConfig.ObfsPassword
            };
        }

        if (nodeConfig.UpMbps > 0)
        {
            hysteriaOutbound["up_mbps"] = nodeConfig.UpMbps;
        }

        if (nodeConfig.DownMbps > 0)
        {
            hysteriaOutbound["down_mbps"] = nodeConfig.DownMbps;
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
                    new Dictionary<string, object?>
                    {
                        ["type"] = "https",
                        ["tag"] = "cloudflare",
                        ["server"] = "1.1.1.1",
                        ["detour"] = "direct"
                    },
                    new Dictionary<string, object?> { ["type"] = "local", ["tag"] = "local" }
                },
                ["final"] = "cloudflare",
                ["strategy"] = "prefer_ipv4"
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "ShanlianVPN",
                    ["address"] = new[] { "172.19.0.1/30" },
                    ["mtu"] = 9000,
                    ["auto_route"] = true,
                    ["strict_route"] = profile == VpnConfigProfile.StrictRoute,
                    ["stack"] = "system"
                }
            },
            ["outbounds"] = new object[]
            {
                hysteriaOutbound,
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" },
                new Dictionary<string, object?> { ["type"] = "block", ["tag"] = "block" }
            },
            ["route"] = new Dictionary<string, object?>
            {
                ["rules"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["protocol"] = "dns",
                        ["action"] = "hijack-dns"
                    }
                },
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
                ["final"] = "proxy"
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(AppPaths.RuntimeConfigPath, json);
        SafeLogger.Info("config_generated");
        return AppPaths.RuntimeConfigPath;
    }

    private static string GetProfileName(VpnConfigProfile profile) =>
        profile == VpnConfigProfile.StrictRoute ? "A" : "B";

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
            if (TryNormalizePortRange(value, out var normalized))
            {
                result.Add(normalized);
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool TryNormalizePortRange(string value, out string normalized)
    {
        normalized = "";
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && TryParsePort(parts[0], out var port))
        {
            normalized = $"{port}:{port}";
            return true;
        }

        if (parts.Length == 2
            && TryParsePort(parts[0], out var start)
            && TryParsePort(parts[1], out var end)
            && start <= end)
        {
            normalized = $"{start}:{end}";
            return true;
        }

        return false;
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, out port) && port is >= 1 and <= 65535;
}
