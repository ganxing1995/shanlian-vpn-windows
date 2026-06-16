using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class NodeService
{
    private static readonly TimeSpan NodesCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim NodesCacheLock = new(1, 1);
    private static IReadOnlyList<VpnNode>? _cachedNodes;
    private static DateTimeOffset _nodesCachedAt;
    private readonly ApiClient _api = new();

    public static void ClearCache()
    {
        _cachedNodes = null;
        _nodesCachedAt = default;
    }

    public async Task<IReadOnlyList<VpnNode>> GetNodesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedNodes is not null && DateTimeOffset.UtcNow - _nodesCachedAt < NodesCacheDuration)
        {
            return _cachedNodes;
        }

        await NodesCacheLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedNodes is not null && DateTimeOffset.UtcNow - _nodesCachedAt < NodesCacheDuration)
            {
                return _cachedNodes;
            }

        var response = await _api.GetAsync("/api/nodes");
        var array = response.ValueKind == JsonValueKind.Array
            ? response
            : JsonHelpers.TryGetProperty(response, "nodes", out var nodes) ? nodes : default;

        if (array.ValueKind != JsonValueKind.Array)
        {
                _cachedNodes = [];
                _nodesCachedAt = DateTimeOffset.UtcNow;
                return _cachedNodes;
        }

        var result = new List<VpnNode>();
        foreach (var item in array.EnumerateArray())
        {
            result.Add(new VpnNode
            {
                Id = JsonHelpers.GetString(item, "id", "node_id"),
                Country = JsonHelpers.GetString(item, "country", "country_name", "region"),
                CountryCode = JsonHelpers.GetString(item, "country_code", "region_code"),
                Name = JsonHelpers.GetString(item, "name", "title")
            });
        }

            _cachedNodes = result;
            _nodesCachedAt = DateTimeOffset.UtcNow;
        return _cachedNodes;
        }
        finally
        {
            NodesCacheLock.Release();
        }
    }

    public async Task<NodeConfig> GetNodeConfigAsync(string nodeId)
    {
        var response = await _api.GetAsync($"/api/nodes/{Uri.EscapeDataString(nodeId)}/config");
        var configRoot = JsonHelpers.TryGetProperty(response, "config", out var nestedConfig) ? nestedConfig : response;
        var tls = JsonHelpers.TryGetProperty(configRoot, "tls", out var tlsElement) ? tlsElement : configRoot;
        var obfs = JsonHelpers.TryGetProperty(configRoot, "obfs", out var obfsElement) ? obfsElement : configRoot;

        var config = new NodeConfig
        {
            Server = JsonHelpers.GetString(configRoot, "server", "host", "address"),
            ServerPort = JsonHelpers.GetInt(configRoot, "server_port", "port"),
            Password = JsonHelpers.GetString(configRoot, "password", "auth_password", "auth"),
            TlsServerName = JsonHelpers.GetString(tls, "server_name", "sni", "tls_server_name"),
            TlsInsecure = JsonHelpers.GetBool(tls, false, "insecure", "allow_insecure"),
            FallbackPorts = JsonHelpers.GetStringArray(configRoot, "fallback_ports", "server_ports"),
            ObfsType = JsonHelpers.GetString(obfs, "type", "obfs_type"),
            ObfsPassword = JsonHelpers.GetString(obfs, "password", "obfs_password"),
            UpMbps = JsonHelpers.GetInt(configRoot, "up_mbps", "upMbps", "up"),
            DownMbps = JsonHelpers.GetInt(configRoot, "down_mbps", "downMbps", "down")
        };

        if (!config.IsComplete)
        {
            throw new ApiException("线路连接失败，请切换线路重试", errorCode: "invalid_node_config");
        }

        return config;
    }
}
