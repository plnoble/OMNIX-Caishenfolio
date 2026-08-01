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

    private async Task<UpdateDownload> RunAsync(StubHttpHandler handler, UpdateCheckResult update)
    {
        using var http = new HttpClient(handler);
        var result = await new UpdateInstaller(http).DownloadAsync(update);
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

public class ReleaseSignatureTests
{
    private static (string PublicKey, byte[] Signature) SignSample(byte[] content)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            ecdsa.SignData(content, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void WithNoKeyConfiguredVerificationIsSkippedRatherThanFailing()
    {
        // Until a release key exists, checksum verification stands alone; the updater must not
        // be bricked by a check that cannot yet be performed.
        Assert.Equal(SignatureStatus.NotConfigured, ReleaseSignature.Verify("anything.msi", null));
        Assert.False(ReleaseSignature.IsConfigured);
    }

    [Fact]
    public void TheProductionKeyConstantIsWellFormedIfPresent()
    {
        if (!ReleaseSignature.IsConfigured)
        {
            return;
        }

        // A malformed key would silently turn every update into "signature invalid".
        using var ecdsa = ECDsa.Create();
        var exception = Record.Exception(() =>
            ecdsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(ReleaseSignature.PublicKeyBase64), out _));
        Assert.Null(exception);
    }

    [Fact]
    public void AValidSignatureVerifiesAndATamperedFileDoesNot()
    {
        // Exercises the same primitives the release key uses, without needing that key here.
        var content = Encoding.UTF8.GetBytes("installer bytes");
        var (publicKey, signature) = SignSample(content);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

        Assert.True(ecdsa.VerifyData(content, signature, HashAlgorithmName.SHA256));
        Assert.False(ecdsa.VerifyData(
            Encoding.UTF8.GetBytes("installer bytes!"), signature, HashAlgorithmName.SHA256));
    }
}
