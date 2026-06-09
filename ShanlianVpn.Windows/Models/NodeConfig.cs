namespace ShanlianVpn.Windows.Models;

public sealed class NodeConfig
{
    public string Server { get; set; } = "";
    public int ServerPort { get; set; }
    public string Password { get; set; } = "";
    public string TlsServerName { get; set; } = "";
    public bool TlsInsecure { get; set; }
    public IReadOnlyList<string> FallbackPorts { get; set; } = Array.Empty<string>();
    public string ObfsType { get; set; } = "";
    public string ObfsPassword { get; set; } = "";
    public int UpMbps { get; set; }
    public int DownMbps { get; set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Server)
        && ServerPort > 0
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(TlsServerName);
}
