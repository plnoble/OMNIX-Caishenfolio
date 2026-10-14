using Caishenfolio.Desktop.Wealth;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// P1-R02 + remediation WP5: the user types a percent (20 = 20%), the API speaks fractions
/// (0.2). These tests call the PRODUCTION parser the view uses — no mirror implementation.
/// </summary>
public class MaxLossInputConversionTests
{
    [Theory]
    [InlineData("20", 0.2)]
    [InlineData("0.5", 0.005)]
    [InlineData("100", 1.0)]
    [InlineData("  15.5  ", 0.155)]
    public void PercentInputBecomesAFraction(string input, decimal expected)
    {
        var result = PercentInputParser.ParseRequiredFraction(input, "可承受最大损失");
        Assert.True(result.Ok);
        Assert.Equal(expected, result.Fraction);
    }

    [Theory]
    [InlineData("0", "0 到 100")]
    [InlineData("-5", "0 到 100")]
    [InlineData("101", "0 到 100")]
    [InlineData("abc", "无法解析")]
    [InlineData("", "未填写")]
    public void InvalidInputIsRejectedWithNoGuess(string input, string expectedErrorPart)
    {
        var result = PercentInputParser.ParseRequiredFraction(input, "可承受最大损失");
        Assert.False(result.Ok);
        Assert.Equal(0m, result.Fraction);
        Assert.Contains(expectedErrorPart, result.Error);
    }
}
