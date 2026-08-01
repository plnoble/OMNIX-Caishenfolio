using System.Security.Cryptography;

namespace Caishenfolio.Host.Release;

/// <summary>
/// Verifies that an installer was produced by whoever holds this project's release key.
///
/// This is the difference between an auto-updater that is safe and one that is a liability. A
/// SHA-256 checksum only proves the file arrived intact — but the checksum is published next to
/// the file, so anyone who can replace one can replace the other. A signature cannot be forged
/// without the private key, which never leaves the release pipeline, so a tampered installer is
/// rejected even if the release itself was altered.
///
/// ECDSA over P-256 rather than Ed25519 only because Ed25519 is not in .NET 8; the guarantee is
/// the same. The public key is compiled into the app on purpose: a key fetched at runtime could
/// be swapped by whoever swapped the installer.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>
    /// Base64 SubjectPublicKeyInfo for the release key, or empty until one is configured.
    ///
    /// While empty, <see cref="Verify"/> reports <see cref="SignatureStatus.NotConfigured"/> and
    /// the updater falls back to checksum-only verification, which is what the manual download
    /// path already offered. Setting this raises the bar for every future release.
    /// </summary>
    public const string PublicKeyBase64 = "";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKeyBase64);

    /// <summary>Checks <paramref name="signature"/> over the bytes of the file at <paramref name="filePath"/>.</summary>
    public static SignatureStatus Verify(string filePath, byte[]? signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!IsConfigured)
        {
            return SignatureStatus.NotConfigured;
        }

        if (signature is null || signature.Length == 0)
        {
            // A signed project that ships an unsigned artifact is the exact case worth refusing.
            return SignatureStatus.Missing;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);

            using var stream = File.OpenRead(filePath);
            return ecdsa.VerifyData(stream, signature, HashAlgorithmName.SHA256)
                ? SignatureStatus.Valid
                : SignatureStatus.Invalid;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            return SignatureStatus.Invalid;
        }
    }
}

public enum SignatureStatus
{
    /// <summary>No release key is compiled in yet; checksum verification stands alone.</summary>
    NotConfigured,

    /// <summary>Signed by the release key.</summary>
    Valid,

    /// <summary>A key is configured but the release carried no signature.</summary>
    Missing,

    /// <summary>The signature does not match — the file must not be run.</summary>
    Invalid,
}
