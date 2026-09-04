using System.Security.Cryptography;
using System.Text;

namespace Limen;

/// Wraps DPAPI so stored secrets are readable only by the current Windows user
/// on the current machine.
public static class Secret
{
    public static string Protect(string plain)
    {
        if (plain.Length == 0) return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string cipher)
    {
        if (cipher.Length == 0) return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
