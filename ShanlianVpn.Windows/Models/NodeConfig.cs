namespace ShanlianVpn.Windows.Models;

public sealed class NodeConfig
{
    public string Server { get; set; } = "";
    public int ServerPort { get; set; }
    public string Password { get; set; } = "";
    public string TlsServerName { get; set; } = "";
    public bool TlsInsecure { get; set; }
    public IReadOnlyList<int> FallbackPorts { get; set; } = Array.Empty<int>();

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Server)
        && ServerPort > 0
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(TlsServerName);
}

