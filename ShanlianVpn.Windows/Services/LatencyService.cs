using System.Diagnostics;
using System.Net.Sockets;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class LatencyService
{
    public async Task<int?> MeasureAsync(NodeConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            using var tcpClient = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await tcpClient.ConnectAsync(config.Server, config.ServerPort, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            stopwatch.Stop();
            return (int)Math.Max(1, stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }
}
