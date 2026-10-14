using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Tests;

public class TestIsolationTests : IDisposable
{
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(TestIsolation.StateRootVariable);
    private readonly string? _savedProvider = Environment.GetEnvironmentVariable(TestIsolation.ForceProviderVariable);

    public TestIsolationTests()
    {
        Environment.SetEnvironmentVariable(TestIsolation.StateRootVariable, null);
        Environment.SetEnvironmentVariable(TestIsolation.ForceProviderVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestIsolation.StateRootVariable, _savedRoot);
        Environment.SetEnvironmentVariable(TestIsolation.ForceProviderVariable, _savedProvider);
    }

    [Fact]
    public void ProductionShape_DoesNotForceProvider()
    {
        Assert.False(TestIsolation.TryResolveStateRoot(out _, out _));
        Assert.False(TestIsolation.TryGetForcedProvider(out _));
    }

    [Fact]
    public void ForceProvider_WithoutIsolatedRoot_IsIgnored()
    {
        Environment.SetEnvironmentVariable(TestIsolation.ForceProviderVariable, "fixture");

        Assert.False(TestIsolation.TryGetForcedProvider(out var provider));
        Assert.Equal(string.Empty, provider);
    }

    [Theory]
    [InlineData(@"\\server\share\state")]
    [InlineData("//server/share/state")]
    [InlineData(@"C:\Windows\System32\caishenfolio-test")]
    public void StateRoot_RejectsUncAndOutsideAllowedRoots(string raw)
    {
        var ok = TestIsolation.TryNormalizeStateRoot(raw, out _, out var reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void StateRoot_ResolvedPathMustStayInsideAnAllowedRoot()
    {
        // ".." is collapsed by GetFullPath. Acceptance is the resolved location:
        // a path that remains under temp is kept, and the ".." spelling is gone.
        var inside = Path.Combine(Path.GetTempPath(), "caishenfolio-isolation", "..", "still-temp");
        var ok = TestIsolation.TryNormalizeStateRoot(inside, out var resolved, out var reason);

        Assert.True(ok, reason);
        Assert.DoesNotContain("..", resolved);
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()),
            resolved,
            StringComparison.OrdinalIgnoreCase);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var escaped = TestIsolation.TryNormalizeStateRoot(
            Path.Combine(profile, "caishenfolio-not-allowed"),
            out _,
            out var escapedReason);
        Assert.False(escaped);
        Assert.False(string.IsNullOrWhiteSpace(escapedReason));
    }

    [Fact]
    public void StateRoot_RejectsProductionDirectory()
    {
        var production = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio");

        var ok = TestIsolation.TryNormalizeStateRoot(production, out _, out var reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void InvalidExplicitRoot_DoesNotResolveAsProduction()
    {
        Environment.SetEnvironmentVariable(TestIsolation.StateRootVariable, "relative-test-root");

        var decision = TestIsolation.Resolve();

        Assert.Equal(TestIsolationMode.Invalid, decision.Mode);
        Assert.Equal(string.Empty, decision.StateRoot);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Theory]
    [InlineData(@"C:child")]
    [InlineData(@"\child")]
    [InlineData("child")]
    public void StateRoot_RejectsNonFullyQualifiedPath(string raw)
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
        try
        {
            var ok = TestIsolation.TryNormalizeStateRoot(raw, out _, out var reason);

            Assert.False(ok);
            Assert.Contains("完整绝对路径", reason);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void StateRoot_RejectsRelativePath()
    {
        var ok = TestIsolation.TryNormalizeStateRoot("caishenfolio-test", out _, out var reason);

        Assert.False(ok);
        Assert.Contains("绝对路径", reason);
    }

    [Fact]
    public void StateRoot_AcceptsUniqueTempDirectory()
    {
        var raw = Path.Combine(Path.GetTempPath(), "caishenfolio-isolation", Guid.NewGuid().ToString("N"));

        var ok = TestIsolation.TryNormalizeStateRoot(raw, out var resolved, out var reason);

        Assert.True(ok, reason);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("akshare")]
    [InlineData("cache+upstream")]
    [InlineData("")]
    public void NonFixtureProvider_IsNotForcedEvenWithIsolation(string requested)
    {
        var root = Path.Combine(Path.GetTempPath(), "caishenfolio-isolation", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(TestIsolation.StateRootVariable, root);
        Environment.SetEnvironmentVariable(TestIsolation.ForceProviderVariable, requested);

        Assert.False(TestIsolation.TryGetForcedProvider(out _));
    }

    [Fact]
    public void Fixture_IsForcedOnlyTogetherWithAnAcceptedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "caishenfolio-isolation", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(TestIsolation.StateRootVariable, root);
        Environment.SetEnvironmentVariable(TestIsolation.ForceProviderVariable, " Fixture ");

        Assert.True(TestIsolation.TryGetForcedProvider(out var provider));
        Assert.Equal("fixture", provider);
    }
}
