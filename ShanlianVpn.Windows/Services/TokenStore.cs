using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ShanlianVpn.Windows.Services;

public static class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ShanlianVPN.Windows.Token.v1");

    public static bool HasToken() => !string.IsNullOrWhiteSpace(ReadToken());

    public static void SaveToken(string token)
    {
        AppPaths.EnsureDirectories();
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.TokenPath, bytes);
    }

    public static string? ReadToken()
    {
        try
        {
            if (!File.Exists(AppPaths.TokenPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(AppPaths.TokenPath);
            var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        if (File.Exists(AppPaths.TokenPath))
        {
            File.Delete(AppPaths.TokenPath);
        }
    }
}
