using System.Diagnostics;
using System.Text;

namespace ShanlianVpn.Windows.Services;

public static class WindowsRuntimeDiagnostics
{
    public static async Task CaptureWindowAsync(SingBoxService singBox, string errorCode, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        SafeLogger.Diagnostic("network_diagnostics_start", errorCode, BuildProcessSummary(singBox));

        while (DateTimeOffset.UtcNow - started < duration)
        {
            await CaptureOnceAsync(singBox, errorCode, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }

        SafeLogger.Diagnostic("network_diagnostics_end", errorCode, BuildProcessSummary(singBox));
    }

    private static async Task CaptureOnceAsync(SingBoxService singBox, string errorCode, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append(BuildProcessSummary(singBox));
        builder.Append(" tun=");
        builder.Append(await RunPowerShellAsync("Get-NetAdapter | Where-Object { $_.Name -like '*Shanlian*' -or $_.InterfaceDescription -match 'Wintun|sing-box|Tunnel|Meta|utun' -or $_.Name -match 'Wintun|sing-box|Tunnel|Meta|utun' } | Select-Object Name,Status,ifIndex,InterfaceDescription | Format-Table -HideTableHeaders | Out-String", cancellationToken));
        builder.Append(" route=");
        builder.Append(await RunPowerShellAsync("Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object ifIndex,InterfaceAlias,NextHop,RouteMetric | Format-Table -HideTableHeaders | Out-String", cancellationToken));
        builder.Append(" dns=");
        builder.Append(await RunPowerShellAsync("Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceAlias,ServerAddresses | Format-Table -HideTableHeaders | Out-String", cancellationToken));
        builder.Append(" ping=");
        builder.Append(await RunPowerShellAsync("Test-Connection 1.1.1.1 -Count 2 -Quiet", cancellationToken));
        builder.Append(" nslookup=");
        builder.Append(await RunCommandAsync("nslookup", "example.com", cancellationToken));
        builder.Append(" https=");
        builder.Append(await RunPowerShellAsync("try { $r=Invoke-WebRequest -Uri 'https://www.google.com/generate_204' -TimeoutSec 10 -UseBasicParsing; 'HTTP '+$r.StatusCode } catch { try { $r=Invoke-WebRequest -Uri 'https://cloudflare.com/cdn-cgi/trace' -TimeoutSec 10 -UseBasicParsing; 'HTTP '+$r.StatusCode } catch { 'https_failed' } }", cancellationToken));
        builder.Append(" sing_box=");
        builder.Append(singBox.GetOutputSummary());

        SafeLogger.Diagnostic("network_diagnostics", errorCode, builder.ToString());
    }

    private static string BuildProcessSummary(SingBoxService singBox) =>
        $"pid={singBox.ProcessId?.ToString() ?? "--"} running={singBox.IsRunning} exit_code={singBox.ExitCode?.ToString() ?? "--"}";

    private static Task<string> RunPowerShellAsync(string command, CancellationToken cancellationToken) =>
        RunCommandAsync("powershell.exe", $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"", cancellationToken);

    private static async Task<string> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
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
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort timeout cleanup.
                }
            }

            return output.ToString();
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || output.Length > 2000)
        {
            return;
        }

        output.Append(line).Append(' ');
    }
}
