using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// The novice home's task list is the "今天有什么需要处理" answer (plan §6). Its contract:
/// setup before analytics, unknown data shown as urgent and never as calm, and an empty
/// portfolio produces a first step instead of a blank page.
/// </summary>
public class NoviceTaskListTests
{
    [Fact]
    public void ABrandNewUserSeesProfileThenAccountCreation()
    {
        var tasks = NoviceTaskList.Derive(
            onboardingCompleted: false,
            accountCount: 0,
            positionCount: 0,
            unpricedCount: 0,
            alertCount: 0,
            valuationComplete: true,
            valuationUnknown: false);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("完善你的档案", tasks[0].Title);
        Assert.Equal("创建第一个账户", tasks[1].Title);
        Assert.Equal("ledger", tasks[1].TargetPage);
    }

    [Fact]
    public void AnAccountWithoutHoldingsIsPromptedToRecordItsFirstAsset()
    {
        var tasks = NoviceTaskList.Derive(
            onboardingCompleted: true,
            accountCount: 1,
            positionCount: 0,
            unpricedCount: 0,
            alertCount: 0,
            valuationComplete: true,
            valuationUnknown: false);

        var task = Assert.Single(tasks);
        Assert.Contains("第一笔资产", task.Title);
        Assert.Equal("ledger", task.TargetPage);
        // The promise matches the SymbolResolver wiring: no internal codes needed.
        Assert.Contains("不需要", task.Detail);
    }

    [Fact]
    public void AnUnknownValuationIsUrgentAndExplainsWhyNumbersAreMissing()
    {
        var tasks = NoviceTaskList.Derive(
            onboardingCompleted: true,
            accountCount: 1,
            positionCount: 3,
            unpricedCount: 3,
            alertCount: 0,
            valuationComplete: false,
            valuationUnknown: true);

        var task = Assert.Single(tasks);
        Assert.True(task.IsUrgent);
        Assert.Contains("不会按 0 计算", task.Detail);
    }

    [Fact]
    public void PartiallyPricedHoldingsAreUrgentWithACount()
    {
        var tasks = NoviceTaskList.Derive(
            onboardingCompleted: true,
            accountCount: 1,
            positionCount: 5,
            unpricedCount: 2,
            alertCount: 0,
            valuationComplete: false,
            valuationUnknown: false);

        var task = Assert.Single(tasks);
        Assert.True(task.IsUrgent);
        Assert.Contains("2 个持仓缺价格", task.Title);
    }

    [Fact]
    public void AlertsAreAttentionItemsNotUrgentOnes()
    {
        var tasks = NoviceTaskList.Derive(
            onboardingCompleted: true,
            accountCount: 1,
            positionCount: 3,
            unpricedCount: 0,
            alertCount: 4,
            valuationComplete: true,
            valuationUnknown: false);

        var task = Assert.Single(tasks);
        Assert.False(task.IsUrgent);
        Assert.Contains("4 条提醒", task.Title);
        Assert.Contains("不构成买卖建议", task.Detail);
    }

    [Fact]
    public void ACompleteQuietPortfolioSaysSoInsteadOfShowingNothing()
    {
        var tasks = NoviceTaskList.Derive(
            onboardingCompleted: true,
            accountCount: 1,
            positionCount: 3,
            unpricedCount: 0,
            alertCount: 0,
            valuationComplete: true,
            valuationUnknown: false);

        var task = Assert.Single(tasks);
        Assert.Equal("数据看起来都齐了", task.Title);
        Assert.False(task.IsUrgent);
    }

    [Fact]
    public void ProfileLinesOmitWhatWasNeverStated()
    {
        var lines = NoviceTaskList.ProfileLines(new PortfolioSettings
        {
            Goal = "十年内攒够首付",
            HorizonYears = 10m,
        });

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("目标："));
        Assert.Contains(lines, l => l.StartsWith("资金期限："));
        Assert.DoesNotContain(lines, l => l.StartsWith("流动性："));
    }

    [Fact]
    public void ProfileLinesRenderLiquidityEmergencyFundAndDrawdown()
    {
        var lines = NoviceTaskList.ProfileLines(new PortfolioSettings
        {
            LiquidityNeed = "anytime",
            EmergencyFundAmount = 60_000m,
            MaxDrawdownTolerance = 0.3m,
            WatchMarkets = ["cn", "jp"],
        });

        Assert.Contains(lines, l => l.Contains("随时可能要用"));
        Assert.Contains(lines, l => l.Contains("应急金") && l.Contains("60,000"));
        Assert.Contains(lines, l => l.Contains("30%"));
        Assert.Contains(lines, l => l.Contains("A股") && l.Contains("日股"));
    }
}
