using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ShanlianVpn.Windows.Services;

public sealed class WindowsDeviceIdProvider
{
    public string GetStableDeviceId()
    {
        var raw = ReadMachineGuid();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}";
        }

        var input = Encoding.UTF8.GetBytes($"ShanlianVPN.Windows.v1|{raw}");
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

