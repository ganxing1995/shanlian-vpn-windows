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

    public string BuildRuntimeConfig(NodeConfig nodeConfig)
    {
        AppPaths.EnsureDirectories();

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

        if (nodeConfig.FallbackPorts.Count > 0)
        {
            hysteriaOutbound["server_ports"] = nodeConfig.FallbackPorts.Select(port => port.ToString()).ToArray();
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
                    new Dictionary<string, object?> { ["tag"] = "cloudflare", ["address"] = "https://1.1.1.1/dns-query" },
                    new Dictionary<string, object?> { ["tag"] = "local", ["address"] = "local" }
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
                    ["strict_route"] = true,
                    ["stack"] = "system",
                    ["sniff"] = true
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
                ["auto_detect_interface"] = true,
                ["final"] = "proxy"
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(AppPaths.RuntimeConfigPath, json);
        SafeLogger.Info("config_generated");
        return AppPaths.RuntimeConfigPath;
    }
}
