using System.Security.Cryptography;
using System.Text;

namespace Pidge.Services;

/// <summary>Encrypts account passwords with Windows DPAPI, tied to the current Windows user.</summary>
public static class Secrets
{
    // Kept from when the app was called QuickMail so existing saved passwords still decrypt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickMail.v1.credentials");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
