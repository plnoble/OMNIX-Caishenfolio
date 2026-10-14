using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>
/// One item on the novice home page's "今天有什么需要处理" list.
/// </summary>
/// <param name="Title">Short imperative, e.g. 创建第一个账户.</param>
/// <param name="Detail">Why it matters, in plain language.</param>
/// <param name="TargetPage">Page key to navigate to; null for an in-place action.</param>
/// <param name="IsUrgent">Red styling — data problems a novice must not miss.</param>
public sealed record NoviceTask(string Title, string Detail, string? TargetPage, bool IsUrgent);

/// <summary>
/// Derives the novice home task list (plan §6) from plain counts, so the logic is a pure
/// function the tests can pin down without a UI. Ordered: setup first, then data problems,
/// then attention items — a novice who has not finished setup should never be distracted by
/// analytics.
/// </summary>
public static class NoviceTaskList
{
    public static IReadOnlyList<NoviceTask> Derive(
        bool onboardingCompleted,
        int accountCount,
        int positionCount,
        int unpricedCount,
        int alertCount,
        bool valuationComplete,
        bool valuationUnknown)
    {
        var tasks = new List<NoviceTask>();

        if (!onboardingCompleted)
        {
            tasks.Add(new NoviceTask(
                "完善你的档案",
                "回答几个问题（本位币、目标、能承受的波动），风险提示会围绕它们解释。",
                TargetPage: null,
                IsUrgent: false));
        }

        if (accountCount == 0)
        {
            tasks.Add(new NoviceTask(
                "创建第一个账户",
                "先建账户（如：某券商、某银行），现金和持仓都挂在账户上。",
                "ledger",
                IsUrgent: false));
        }
        else if (positionCount == 0)
        {
            tasks.Add(new NoviceTask(
                "录入第一笔资产",
                "可以直接输代码（如 600000）或名称，软件会自动识别市场，不需要你记内部代码。",
                "ledger",
                IsUrgent: false));
        }

        // Data problems outrank everything else a novice might stare at: an unknown number
        // must never look like a calm portfolio.
        if (valuationUnknown)
        {
            tasks.Add(new NoviceTask(
                "所有持仓都缺价格",
                "目前无法估值：行情还没就绪或数据源失败。合计与收益显示为未知，不会按 0 计算。",
                "overview",
                IsUrgent: true));
        }
        else if (unpricedCount > 0)
        {
            tasks.Add(new NoviceTask(
                $"有 {unpricedCount} 个持仓缺价格",
                "这些持仓没有计入总资产合计（按 0 计入会是错的）。可以点开总览看是哪几个。",
                "overview",
                IsUrgent: true));
        }

        if (alertCount > 0)
        {
            tasks.Add(new NoviceTask(
                $"查看 {alertCount} 条提醒",
                "包括价格提醒、集中度提示与配置偏离。提醒只陈述事实，不构成买卖建议。",
                "overview",
                IsUrgent: false));
        }

        if (tasks.Count == 0 && valuationComplete)
        {
            tasks.Add(new NoviceTask(
                "数据看起来都齐了",
                "资产已全部定价。可以看看下面的配置摘要，或进入研究页做比较和复盘。",
                null,
                IsUrgent: false));
        }

        return tasks;
    }

    /// <summary>
    /// Renders the novice profile (from onboarding) as display lines; empty parts are omitted
    /// rather than shown as blank placeholders.
    /// </summary>
    public static IReadOnlyList<string> ProfileLines(PortfolioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var lines = new List<string>();
        if (settings.Goal.Length > 0)
        {
            lines.Add($"目标：{settings.Goal}");
        }

        if (settings.HorizonYears is { } years)
        {
            lines.Add($"资金期限：{years:0.##} 年");
        }

        if (LiquidityNeeds.Label(settings.LiquidityNeed) is { } liquidity && liquidity.Length > 0)
        {
            lines.Add($"流动性：{liquidity}");
        }

        if (settings.EmergencyFundAmount > 0m)
        {
            lines.Add($"应急金：{settings.EmergencyFundAmount:#,0.##} {settings.BaseCurrency}（不按闲置现金提醒）");
        }

        if (settings.MaxDrawdownTolerance > 0m)
        {
            lines.Add($"可承受回撤：{(settings.MaxDrawdownTolerance * 100m):0}%");
        }

        if (settings.WatchMarkets.Count > 0)
        {
            lines.Add($"关注市场：{string.Join("、", settings.WatchMarkets.Select(MarketLabel))}");
        }

        return lines;
    }

    private static string MarketLabel(string code) => code.ToLowerInvariant() switch
    {
        "cn" => "A股",
        "hk" => "港股",
        "us" => "美股",
        "jp" => "日股",
        "global" => "全球",
        _ => code,
    };
}
