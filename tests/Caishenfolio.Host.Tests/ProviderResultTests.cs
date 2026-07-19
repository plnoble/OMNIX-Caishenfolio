using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Tests;

public class ProviderResultTests
{
    [Fact]
    public void Failure_IsFailClosed()
    {
        var result = ProviderResult<IReadOnlyList<OhlcvBar>>.Failure("fixture", "provider unavailable");
        Assert.False(result.Ok);
        Assert.Null(result.Data);
        Assert.Equal("provider unavailable", result.Error);
        Assert.Equal("fixture", result.Provider);
    }

    [Fact]
    public void Success_KeepsWarnings()
    {
        var bars = new[]
        {
            new OhlcvBar(
                DateTimeOffset.UtcNow,
                1, 2, 0.5m, 1.5m,
                1000,
                "CNY",
                Adjustment.Raw,
                "fixture"),
        };

        var result = ProviderResult<IReadOnlyList<OhlcvBar>>.Success(
            "fixture",
            bars,
            warnings: new[] { "delayed" });

        Assert.True(result.Ok);
        Assert.Single(result.Data!);
        Assert.Contains("delayed", result.Warnings);
    }
}
