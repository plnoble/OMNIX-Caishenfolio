using System.Net;
using System.Security.Cryptography;
using System.Text;
using Caishenfolio.Host.Release;

namespace Caishenfolio.Host.Tests;

/// <summary>Serves canned bytes per URL so download and verification run without network.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public HashSet<string> Requested { get; } = new(StringComparer.Ordinal);

    public StubHttpHandler Serve(string url, byte[] content)
    {
        _files[url] = content;
        return this;
    }

    public StubHttpHandler Serve(string url, string content) => Serve(url, Encoding.UTF8.GetBytes(content));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        return Task.FromResult(_files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Throwaway key pairs, so both the accept and reject paths can be exercised.</summary>
internal static class TestKeys
{
    /// <summary>An empty key means "no release key", which selects checksum-only verification.</summary>
    public const string Unconfigured = "";

    public static (string PublicKey, byte[] Signature) Sign(byte[] content)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            ecdsa.SignData(content, HashAlgorithmName.SHA256));
    }
}

public class UpdateInstallerTests : IDisposable
{
    private const string MsiUrl = "https://example.test/OMNIX-Caishenfolio.msi";
    private const string SumUrl = "https://example.test/checksums.sha256";
    private const string SigUrl = "https://example.test/OMNIX-Caishenfolio.msi.sig";

    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend this is an MSI");

    private readonly List<string> _tempRoots = [];

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static UpdateCheckResult Update(bool withChecksum = true, bool withSignature = false)
    {
        var assets = new List<ReleaseAsset>
        {
            new("OMNIX-Caishenfolio.msi", MsiUrl, Installer.Length),
        };
        if (withChecksum)
        {
            assets.Add(new ReleaseAsset("checksums.sha256", SumUrl, 90));
        }

        if (withSignature)
        {
            assets.Add(new ReleaseAsset("OMNIX-Caishenfolio.msi.sig", SigUrl, 96));
        }

        return new UpdateCheckResult
        {
            Status = UpdateStatus.UpdateAvailable,
            CurrentVersion = "0.12.0",
            LatestVersion = "0.13.0",
            Message = "",
            Assets = assets,
        };
    }

    private async Task<UpdateDownload> RunAsync(
        StubHttpHandler handler, UpdateCheckResult update, string? publicKey = null)
    {
        using var http = new HttpClient(handler);
        // Without an override the compiled-in release key applies, and a fixture MSI signed by
        // nobody would be refused — correctly, but that would only ever test the reject path.
        var result = await new UpdateInstaller(http, publicKey ?? TestKeys.Unconfigured)
            .DownloadAsync(update);
        if (result.InstallerPath is not null)
        {
            _tempRoots.Add(Path.GetDirectoryName(result.InstallerPath)!);
        }

        return result;
    }

    [Fact]
    public async Task AGoodDownloadIsReadyToInstall()
    {
        var handler = new StubHttpHandler()
            .Serve(MsiUrl, Installer)
            .Serve(SumUrl, $"{Sha256Of(Installer)}  OMNIX-Caishenfolio.msi");

        var result = await RunAsync(handler, Update());

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(result.InstallerPath));
        Assert.Equal(Installer.Length, new FileInfo(result.InstallerPath!).Length);
    }

    [Fact]
    public async Task ACorrectlySignedInstallerIsAccepted()
    {
        var (publicKey, signature) = TestKeys.Sign(Installer);
        var handler = new StubHttpHandler()
            .Serve(MsiUrl, Installer)
            .Serve(SumUrl, $"{Sha256Of(Installer)}  OMNIX-Caishenfolio.msi")
            .Serve(SigUrl, Convert.ToBase64String(signature));

        var result = await RunAsync(handler, Update(withSignature: true), publicKey);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(SignatureStatus.Valid, result.Signature);
        Assert.Contains("签名", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstallerSignedByTheWrongKeyIsRefused()
    {
        // Correct bytes, correct checksum, signature from somebody else's key. This is the case
        // a checksum cannot catch, because whoever replaces the MSI also replaces the checksum.
        var (_, foreignSignature) = TestKeys.Sign(Installer);
        var (ourPublicKey, _) = TestKeys.Sign(Installer);
        var handler = new StubHttpHandler()
            .Serve(MsiUrl, Installer)
            .Serve(SumUrl, $"{Sha256Of(Installer)}  OMNIX-Caishenfolio.msi")
            .Serve(SigUrl, Convert.ToBase64String(foreignSignature));

        var result = await RunAsync(handler, Update(withSignature: true), ourPublicKey);

        Assert.Equal(UpdateDownloadStatus.SignatureInvalid, result.Status);
        Assert.Null(result.InstallerPath);
    }

    [Fact]
    public async Task AnUnsignedInstallerIsRefusedOnceAKeyIsConfigured()
    {
        var (publicKey, _) = TestKeys.Sign(Installer);
        var handler = new StubHttpHandler()
            .Serve(MsiUrl, Installer)
            .Serve(SumUrl, $"{Sha256Of(Installer)}  OMNIX-Caishenfolio.msi");

        // No .sig asset at all: a project that signs must not accept an unsigned build.
        var result = await RunAsync(handler, Update(), publicKey);

        Assert.Equal(UpdateDownloadStatus.SignatureMissing, result.Status);
        Assert.Null(result.InstallerPath);
    }

    [Fact]
    public async Task ATamperedInstallerIsRefusedAndDeleted()
    {
        // The checksum says one thing, the bytes another — that is the whole point of checking.
        var handler = new StubHttpHandler()
            .Serve(MsiUrl, Encoding.UTF8.GetBytes("malicious payload"))
            .Serve(SumUrl, $"{Sha256Of(Installer)}  OMNIX-Caishenfolio.msi");

        var result = await RunAsync(handler, Update());

        Assert.Equal(UpdateDownloadStatus.ChecksumMismatch, result.Status);
        Assert.Null(result.InstallerPath);
        Assert.Contains("篡改", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANothingIsLeftOnDiskWhenVerificationFails()
    {
        var handler = new StubHttpHandler()
            .Serve(MsiUrl, Encoding.UTF8.GetBytes("malicious payload"))
            .Serve(SumUrl, $"{Sha256Of(Installer)}  OMNIX-Caishenfolio.msi");

        await RunAsync(handler, Update());

        // An unverified installer left in temp is something a user could double-click later.
        var directory = Path.Combine(Path.GetTempPath(), "Caishenfolio", "update", "0.13.0");
        var stale = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.msi")
            : [];
        Assert.Empty(stale);
    }

    [Fact]
    public async Task AReleaseWithoutChecksumsIsRefused()
    {
        var handler = new StubHttpHandler().Serve(MsiUrl, Installer);

        var result = await RunAsync(handler, Update(withChecksum: false));

        Assert.Equal(UpdateDownloadStatus.ChecksumMissing, result.Status);
        Assert.Null(result.InstallerPath);
    }

    [Fact]
    public async Task AReleaseWithNoInstallerReportsSoWithoutDownloading()
    {
        var handler = new StubHttpHandler();
        var update = Update() with { Assets = [new ReleaseAsset("notes.txt", "https://x/n.txt", 1)] };

        var result = await RunAsync(handler, update);

        Assert.Equal(UpdateDownloadStatus.NoInstaller, result.Status);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task AFailedDownloadIsReportedRatherThanThrown()
    {
        var handler = new StubHttpHandler().Serve(SumUrl, "irrelevant");

        var result = await RunAsync(handler, Update());

        Assert.Equal(UpdateDownloadStatus.DownloadFailed, result.Status);
    }

    [Fact]
    public async Task CanInstallInPlaceNeedsAnMsiAsset()
    {
        Assert.True(Update().CanInstallInPlace);
        Assert.False((Update() with { Assets = [] }).CanInstallInPlace);

        // An up-to-date check must never offer to install anything.
        var current = Update() with { Status = UpdateStatus.UpToDate };
        Assert.False(current.CanInstallInPlace);

        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("ABC  OMNIX-Caishenfolio.msi")]
    [InlineData("")]
    [InlineData("not a checksum file at all")]
    public void AMalformedChecksumFileYieldsNothing(string text)
    {
        Assert.Null(UpdateInstaller.ParseChecksum(text, "OMNIX-Caishenfolio.msi"));
    }

    [Fact]
    public void ChecksumParsingHandlesTheCommonFormats()
    {
        var hash = new string('a', 64);

        // Plain, binary-mode star, a path prefix, and extra lines for other files.
        Assert.Equal(hash, UpdateInstaller.ParseChecksum($"{hash}  app.msi", "app.msi"));
        Assert.Equal(hash, UpdateInstaller.ParseChecksum($"{hash} *app.msi", "app.msi"));
        Assert.Equal(hash, UpdateInstaller.ParseChecksum($"{hash}  ./dist/app.msi", "app.msi"));
        Assert.Equal(
            hash,
            UpdateInstaller.ParseChecksum($"{new string('b', 64)}  other.zip\n{hash}  app.msi", "app.msi"));
    }

    [Fact]
    public void ChecksumLookupIsPerFileNotJustTheFirstLine()
    {
        var wanted = new string('c', 64);
        var text = $"{new string('d', 64)}  decoy.msi\n{wanted}  app.msi";

        Assert.Equal(wanted, UpdateInstaller.ParseChecksum(text, "app.msi"));
        Assert.Null(UpdateInstaller.ParseChecksum(text, "absent.msi"));
    }

    [Fact]
    public void LaunchRefusesAPathThatDoesNotExist()
    {
        Assert.False(UpdateInstaller.Launch(Path.Combine(Path.GetTempPath(), "no-such-file.msi")));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var root in _tempRoots.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A locked temp file is not a test failure.
            }
        }
    }
}

public class ReleaseSignatureTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"omnix_sig_{Guid.NewGuid():N}.bin");

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("installer bytes");

    public ReleaseSignatureTests() => File.WriteAllBytes(_file, Content);

    [Fact]
    public void TheProjectShipsAReleaseKey()
    {
        // Losing this constant would silently downgrade every future update to checksum-only.
        Assert.True(ReleaseSignature.IsConfigured);
    }

    [Fact]
    public void TheProductionKeyIsAWellFormedP256PublicKey()
    {
        using var ecdsa = ECDsa.Create();
        var exception = Record.Exception(() =>
            ecdsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(ReleaseSignature.PublicKeyBase64), out _));

        // A malformed key would turn every update into "signature invalid" with no other clue.
        Assert.Null(exception);
        Assert.Equal(256, ecdsa.KeySize);
        // Publishing a private key here would let anyone sign an accepted installer.
        Assert.Null(ecdsa.ExportParameters(false).D);
    }

    [Fact]
    public void AValidSignatureIsAccepted()
    {
        var (publicKey, signature) = TestKeys.Sign(Content);

        Assert.Equal(SignatureStatus.Valid, ReleaseSignature.Verify(_file, signature, publicKey));
    }

    [Fact]
    public void ASignatureOverDifferentBytesIsRejected()
    {
        var (publicKey, signature) = TestKeys.Sign(Encoding.UTF8.GetBytes("other bytes"));

        Assert.Equal(SignatureStatus.Invalid, ReleaseSignature.Verify(_file, signature, publicKey));
    }

    [Fact]
    public void AnotherKeysSignatureIsRejected()
    {
        var (_, foreign) = TestKeys.Sign(Content);
        var (ours, _) = TestKeys.Sign(Content);

        Assert.Equal(SignatureStatus.Invalid, ReleaseSignature.Verify(_file, foreign, ours));
    }

    [Fact]
    public void AMissingSignatureIsDistinctFromAnInvalidOne()
    {
        var (publicKey, _) = TestKeys.Sign(Content);

        // The two mean different things to the user: "not signed" versus "signed by someone else".
        Assert.Equal(SignatureStatus.Missing, ReleaseSignature.Verify(_file, null, publicKey));
        Assert.Equal(SignatureStatus.Missing, ReleaseSignature.Verify(_file, [], publicKey));
    }

    [Fact]
    public void GarbageInsteadOfAKeyFailsClosedRatherThanThrowing()
    {
        var (_, signature) = TestKeys.Sign(Content);

        Assert.Equal(SignatureStatus.Invalid, ReleaseSignature.Verify(_file, signature, "not base64"));
    }

    [Fact]
    public void WithNoKeyVerificationIsSkippedRatherThanFailing()
    {
        // The state this project was in before a key existed: checksum verification stands
        // alone, and the updater is not bricked by a check that cannot be performed.
        Assert.Equal(SignatureStatus.NotConfigured, ReleaseSignature.Verify(_file, null, ""));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            File.Delete(_file);
        }
        catch (IOException)
        {
            // A locked temp file is not a test failure.
        }
    }
}
