using Caishenfolio.Desktop.Wealth;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// The glossary is the single source of plain-language copy for finance terms (plan §6).
/// Its contract: every entry explains both what the term means AND its honest caveat, and an
/// unknown key never yields a fabricated definition.
/// </summary>
public class TermGlossaryTests
{
    [Theory]
    [InlineData("xirr")]
    [InlineData("twr")]
    [InlineData("drawdown")]
    [InlineData("percentile")]
    [InlineData("carry")]
    [InlineData("adjustment")]
    [InlineData("slippage")]
    [InlineData("unpriced")]
    [InlineData("reversal")]
    [InlineData("drift")]
    public void EveryPlanTermCarriesAMeaningAndACaveat(string key)
    {
        var term = TermGlossary.Find(key);

        Assert.NotNull(term);
        Assert.Equal(key, term!.Key);
        Assert.False(string.IsNullOrWhiteSpace(term.Term));
        Assert.False(string.IsNullOrWhiteSpace(term.Plain));
        Assert.False(string.IsNullOrWhiteSpace(term.Caveat));
    }

    [Fact]
    public void LookupIsCaseInsensitiveButNeverFabricated()
    {
        Assert.NotNull(TermGlossary.Find("XIRR"));
        Assert.Null(TermGlossary.Find("不存在的术语"));
        Assert.Null(TermGlossary.Find(""));
        Assert.Null(TermGlossary.Find(null!));
    }

    [Fact]
    public void TooltipCombinesMeaningAndCaveat()
    {
        var tooltip = TermGlossary.Tooltip("drawdown");

        Assert.NotNull(tooltip);
        Assert.Contains("从历史最高点", tooltip);
        Assert.Contains("注意：", tooltip);
        Assert.Null(TermGlossary.Tooltip("不存在"));
    }

    [Fact]
    public void ExplanationsStayPlainAndHonest()
    {
        // The caveat is the part novices need most and screens usually skip: every single
        // entry must warn about over-reading the number, not just describe it.
        foreach (var term in TermGlossary.Terms.Values)
        {
            Assert.True(term.Plain.Length >= 10, $"{term.Key}: 解释太短，起不到白话解释的作用。");
            Assert.True(
                term.Caveat.Contains("不") || term.Caveat.Contains("可能") || term.Caveat.Contains("会"),
                $"{term.Key}: 警示部分没有说出数字的局限。");
        }
    }
}
