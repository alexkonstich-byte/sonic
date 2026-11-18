using System.Security.Cryptography;
using System.Text;

namespace Sonic.Core.Helpers;

/// <summary>Encrypts small secrets for the current Windows user with DPAPI.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = "Sonic.Secrets.v1"u8.ToArray();

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
