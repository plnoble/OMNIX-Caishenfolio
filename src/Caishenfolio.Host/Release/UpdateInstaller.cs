using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;

namespace Caishenfolio.Host.Release;

/// <summary>Why an update was refused, or that it is ready to run.</summary>
public enum UpdateDownloadStatus
{
    Ready,
    NoInstaller,
    DownloadFailed,
    ChecksumMissing,
    ChecksumMismatch,
    SignatureMissing,
    SignatureInvalid,
}

public sealed record UpdateDownload
{
    public required UpdateDownloadStatus Status { get; init; }
    public string? InstallerPath { get; init; }
    public required string Message { get; init; }
    public SignatureStatus Signature { get; init; } = SignatureStatus.NotConfigured;

    public bool Ok => Status == UpdateDownloadStatus.Ready;
}

/// <summary>
/// Fetches a release installer and refuses to run anything it could not verify.
///
/// The app updates itself rather than sending the user to a browser, because a download the user
/// performs by hand is not actually safer — nobody compares a SHA-256 by eye. Moving it in-app
/// means the check always happens.
///
/// Order matters: the file is downloaded to a private temp directory, verified there, and only
/// then handed to msiexec. Nothing is executed before both the checksum and (once a release key
/// is configured) the signature pass. A failure deletes the file rather than leaving an
/// unverified installer on disk for someone to double-click later.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly HttpClient _http;
    private readonly string? _publicKeyBase64;

    /// <param name="publicKeyBase64">
    /// Overrides the compiled-in release key. Only tests pass this — see
    /// <see cref="ReleaseSignature.Verify"/> for why the seam exists.
    /// </param>
    public UpdateInstaller(HttpClient http, string? publicKeyBase64 = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _publicKeyBase64 = publicKeyBase64;
    }

    public async Task<UpdateDownload> DownloadAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var installer = ReleaseAsset.FindInstaller(update.Assets);
        if (installer is null)
        {
            return new UpdateDownload
            {
                Status = UpdateDownloadStatus.NoInstaller,
                Message = "这个版本没有附带安装包，请到发布页手动下载。",
            };
        }

        var directory = Path.Combine(
            Path.GetTempPath(), "Caishenfolio", "update", update.LatestVersion ?? "latest");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, installer.Name);

        try
        {
            await DownloadFileAsync(installer, target, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Discard(target);
            return new UpdateDownload
            {
                Status = UpdateDownloadStatus.DownloadFailed,
                Message = $"下载失败：{ex.Message}",
            };
        }

        var verified = await VerifyAsync(update, installer, target, cancellationToken).ConfigureAwait(false);
        if (!verified.Ok)
        {
            // An installer that failed verification must not be left lying around.
            Discard(target);
        }

        return verified;
    }

    private async Task<UpdateDownload> VerifyAsync(
        UpdateCheckResult update,
        ReleaseAsset installer,
        string path,
        CancellationToken cancellationToken)
    {
        var expected = await TryReadChecksumAsync(update, installer.Name, cancellationToken)
            .ConfigureAwait(false);
        if (expected is null)
        {
            return new UpdateDownload
            {
                Status = UpdateDownloadStatus.ChecksumMissing,
                Message = "发布里没有校验和，无法确认安装包完整，已放弃。请到发布页手动下载。",
            };
        }

        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateDownload
            {
                Status = UpdateDownloadStatus.ChecksumMismatch,
                Message = $"校验和不匹配，安装包可能损坏或被篡改，已删除。期望 {expected[..16]}…，实际 {actual[..16]}…",
            };
        }

        var signature = await TryReadSignatureAsync(update, cancellationToken).ConfigureAwait(false);
        var status = ReleaseSignature.Verify(path, signature, _publicKeyBase64);
        return status switch
        {
            SignatureStatus.Invalid => new UpdateDownload
            {
                Status = UpdateDownloadStatus.SignatureInvalid,
                Signature = status,
                Message = "签名校验未通过，安装包不是本项目发布的，已删除。",
            },
            SignatureStatus.Missing => new UpdateDownload
            {
                Status = UpdateDownloadStatus.SignatureMissing,
                Signature = status,
                Message = "本项目的发布应带签名，但这个版本没有，已放弃安装。",
            },
            _ => new UpdateDownload
            {
                Status = UpdateDownloadStatus.Ready,
                Signature = status,
                InstallerPath = path,
                Message = status == SignatureStatus.Valid
                    ? "已下载并通过校验和与签名验证，可以安装。"
                    : "已下载并通过校验和验证（此版本尚未启用发布签名）。",
            },
        };
    }

    private async Task DownloadFileAsync(
        ReleaseAsset asset,
        string target,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (total > 0)
            {
                progress?.Report(Math.Min(1.0, (double)copied / total));
            }
        }
    }

    private async Task<string?> TryReadChecksumAsync(
        UpdateCheckResult update, string installerName, CancellationToken cancellationToken)
    {
        var asset = ReleaseAsset.FindChecksums(update.Assets);
        if (asset is null)
        {
            return null;
        }

        string text;
        try
        {
            text = await _http.GetStringAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return ParseChecksum(text, installerName);
    }

    /// <summary>Reads a <c>sha256sum</c>-style file: <c>HASH  filename</c>, one per line.</summary>
    internal static string? ParseChecksum(string text, string installerName)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length != 64)
            {
                continue;
            }

            // The name may carry a leading '*' (binary mode) or a path.
            var name = Path.GetFileName(parts[^1].TrimStart('*'));
            if (string.Equals(name, installerName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }

    private async Task<byte[]?> TryReadSignatureAsync(
        UpdateCheckResult update, CancellationToken cancellationToken)
    {
        var asset = ReleaseAsset.FindSignature(update.Assets);
        if (asset is null)
        {
            return null;
        }

        try
        {
            var text = await _http.GetStringAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            return Convert.FromBase64String(text.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a temp file behind is not worth failing the report over.
        }
    }

    /// <summary>
    /// Hands the verified installer to Windows and reports whether it started.
    ///
    /// The caller closes the app immediately afterwards: an MSI cannot replace files that the
    /// running process holds open, so staying alive would make the upgrade fail halfway.
    /// </summary>
    public static bool Launch(string installerPath, bool silent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (!File.Exists(installerPath))
        {
            return false;
        }

        var arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"/i \"{installerPath}\" {(silent ? "/quiet" : "/passive")} /norestart");

        try
        {
            var process = Process.Start(new ProcessStartInfo("msiexec", arguments)
            {
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
