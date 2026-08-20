using Caishenfolio.Host.Release;

namespace Caishenfolio.Host.Tests;

public class UpdateCheckerTests
{
    private sealed class StubFeed(ReleaseInfo? release, Exception? failure = null) : IReleaseFeed
    {
        public Task<ReleaseInfo?> TryGetLatestAsync(CancellationToken cancellationToken = default) =>
            failure is not null ? Task.FromException<ReleaseInfo?>(failure) : Task.FromResult(release);
    }

    [Fact]
    public async Task ReportsAnAvailableUpdate()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.11.0", "https://example.invalid/releases/v0.11.0", "新增日股")));

        var result = await checker.CheckAsync("0.10.0");

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.True(result.HasUpdate);
        Assert.Equal("0.11.0", result.LatestVersion);
        Assert.Equal("https://example.invalid/releases/v0.11.0", result.ReleaseUrl);
        // No installer attached to this release, so the message must not offer an in-app update.
        Assert.False(result.CanInstallInPlace);
        Assert.Contains("发布页", result.Message);
    }

    private static ReleaseInfo WithInstaller(string tag) => new(
        tag,
        "https://example.invalid/releases/" + tag,
        null,
        [
            new ReleaseAsset("OMNIX-Caishenfolio.msi", "https://example.invalid/app.msi", 1600000),
            new ReleaseAsset("checksums.sha256", "https://example.invalid/sums", 90),
        ]);

    [Fact]
    public async Task AReleaseWithAnInstallerOffersAnInAppUpdate()
    {
        var checker = new UpdateChecker(new StubFeed(WithInstaller("v0.13.2")));

        var result = await checker.CheckAsync("0.13.1");

        Assert.True(result.CanInstallInPlace);
        Assert.Contains("立即更新", result.Message);
        Assert.Contains("账本数据不受影响", result.Message);
    }

    [Fact]
    public async Task AReleaseWithoutAnInstallerSendsTheUserToTheReleasePage()
    {
        // Promising an in-app update here left the user reading "可直接在应用内更新" with no
        // button to press, because the UI only shows one when an installer actually exists.
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.13.2", "https://example.invalid", null, [])));

        var result = await checker.CheckAsync("0.13.1");

        Assert.False(result.CanInstallInPlace);
        Assert.Contains("发布页", result.Message);
        Assert.DoesNotContain("立即更新", result.Message);
    }

    [Fact]
    public async Task AssetsAreCarriedThroughForTheInstaller()
    {
        var checker = new UpdateChecker(new StubFeed(WithInstaller("v0.13.2")));

        var result = await checker.CheckAsync("0.13.1");

        Assert.Equal(2, result.Assets.Count);
        Assert.NotNull(ReleaseAsset.FindInstaller(result.Assets));
        Assert.NotNull(ReleaseAsset.FindChecksums(result.Assets));
    }

    [Fact]
    public async Task AnUpToDateCheckNeverOffersToInstall()
    {
        var checker = new UpdateChecker(new StubFeed(WithInstaller("v0.13.2")));

        var result = await checker.CheckAsync("0.13.2");

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.False(result.CanInstallInPlace);
    }

    [Fact]
    public async Task ReportsUpToDate()
    {
        var checker = new UpdateChecker(new StubFeed(new ReleaseInfo("v0.10.0", "https://example.invalid", null)));

        var result = await checker.CheckAsync("0.10.0");

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task ALocalBuildAheadOfTheFeedIsNotAnUpdate()
    {
        var checker = new UpdateChecker(new StubFeed(new ReleaseInfo("v0.9.0", "https://example.invalid", null)));

        var result = await checker.CheckAsync("0.10.0");

        Assert.Equal(UpdateStatus.Ahead, result.Status);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task NoPublishedReleaseIsNotAFailure()
    {
        var result = await new UpdateChecker(new StubFeed(null)).CheckAsync("0.10.0");

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Contains("还没有发布过 Release", result.Message);
    }

    [Fact]
    public async Task NetworkFailureIsReportedWithoutThrowing()
    {
        var checker = new UpdateChecker(
            new StubFeed(null, new HttpRequestException("no route to host")));

        var result = await checker.CheckAsync("0.10.0");

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("不影响使用", result.Message);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task AnUnparseableTagFailsClosedRatherThanGuessing()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("nightly-2026-07-31", "https://example.invalid", null)));

        var result = await checker.CheckAsync("0.10.0");

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("无法比较版本号", result.Message);
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V0.10.0", 0, 10, 0)]
    public void ParsesSemanticTags(string raw, int major, int minor, int patch)
    {
        Assert.True(UpdateChecker.TryParse(raw, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("release-1.2.3")]
    public void RejectsTagsItCannotCompare(string? raw)
    {
        Assert.False(UpdateChecker.TryParse(raw, out _));
    }

    [Fact]
    public void ComparesNumericallyNotAlphabetically()
    {
        // "0.9.0" sorts after "0.10.0" as text; as versions it comes first.
        Assert.True(UpdateChecker.TryParse("0.9.0", out var older));
        Assert.True(UpdateChecker.TryParse("0.10.0", out var newer));
        Assert.True(older < newer);
    }
}
