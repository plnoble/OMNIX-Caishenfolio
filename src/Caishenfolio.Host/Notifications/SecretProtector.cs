using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Caishenfolio.Host.Notifications;

/// <summary>
/// Encrypts webhook tokens and mail passwords before they touch the ledger file.
///
/// These are credentials: a Feishu bot URL is enough to post into someone's group, and a mail
/// password is usually reused. The ledger is an ordinary SQLite file the user may copy to a
/// backup drive or a sync folder, so storing them as plain text there puts them wherever that
/// file ends up.
///
/// Windows DPAPI ties the ciphertext to the current user account, which is the right scope: the
/// secrets are only ever needed by this user on this machine, and a copied file is useless
/// elsewhere. On any other platform the value is stored as-is and reported as unprotected
/// rather than pretending to be encrypted.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    /// <summary>True when secrets written now will actually be encrypted.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Encrypts a secret for storage. Empty in, empty out.</summary>
    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (!OperatingSystem.IsWindows())
        {
            return value;
        }

        try
        {
            return Prefix + Convert.ToBase64String(Encrypt(value));
        }
        catch (CryptographicException)
        {
            // Better to store it readable than to lose the user's configuration silently.
            return value;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Encrypt(string value) => ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Decrypt(string payload) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(
            Convert.FromBase64String(payload), optionalEntropy: null, DataProtectionScope.CurrentUser));

    /// <summary>Decrypts a stored secret, tolerating values written before protection existed.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Written by an older build, or on a platform without DPAPI.
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Ciphertext written on Windows cannot be opened here.
            return "";
        }

        try
        {
            return Decrypt(stored[Prefix.Length..]);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            // Ciphertext from another user or machine cannot be read here. Returning the raw
            // string would send gibberish as a token, so this reports "no secret" instead.
            return "";
        }
    }

    /// <summary>Masks a secret for display, keeping just enough to recognise which one it is.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= 8 ? new string('•', value.Length) : value[..4] + "…" + value[^4..];
    }
}
