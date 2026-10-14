namespace Caishenfolio.Desktop.Wealth;

/// <summary>
/// One plain-language explanation of a finance term. Written for someone who has never kept
/// a ledger before: what the number means, in words, and the one caveat most likely to bite.
/// </summary>
/// <param name="Key">Stable lookup key, e.g. <c>xirr</c>.</param>
/// <param name="Term">Display name shown next to the number, e.g. <c>资金年化收益 XIRR</c>.</param>
/// <param name="Plain">What it means, in one or two sentences, no jargon.</param>
/// <param name="Caveat">The honest limitation — what a novice might over-read into the number.</param>
public sealed record TermExplanation(string Key, string Term, string Plain, string Caveat);

/// <summary>
/// In-place plain-language explanations (plan §6: 术语就地白话解释). Static, UI-agnostic copy
/// so every page shares one source of truth — no page invents its own wording for the same term.
/// </summary>
public static class TermGlossary
{
    public static readonly IReadOnlyDictionary<string, TermExplanation> Terms =
        new Dictionary<string, TermExplanation>(StringComparer.OrdinalIgnoreCase)
        {
            ["xirr"] = new(
                "xirr",
                "资金年化收益 XIRR",
                "把每一笔入金、出金的时间和金额都考虑进去后折算出的年化收益率。它回答的是：我的钱整体上以什么速度在增值？",
                "中途大额加减资金会显著改变这个数字；持有期很短时年化数字会被放大，不能当作未来收益的预期。"),
            ["twr"] = new(
                "twr",
                "时间加权收益 TWR",
                "剔除了资金进出影响后的收益率，衡量“投资本身”的表现，方便与基准指数比较。",
                "它不是你实际拿到手的收益率——若你在上涨前入金，实际收益会与它明显不同。"),
            ["drawdown"] = new(
                "drawdown",
                "最大回撤",
                "从历史最高点跌到之后最低点的最大跌幅。“历史最大回撤 30%”表示账面价值曾经从顶部缩水三成。",
                "这只是历史上发生过的深度，未来完全可能更深；它也不是亏损已经实现的意思。"),
            ["percentile"] = new(
                "percentile",
                "估值分位",
                "当前估值在历史区间里所处的位置：80% 分位表示历史上 80% 的时间比现在便宜。",
                "分位只描述位置，不预测未来；低分位不等于该买，高分位不等于该卖——低位可能更低。"),
            ["carry"] = new(
                "carry",
                "利差 carry",
                "两种货币政策利率之差。持有高利率货币期间能赚取利差，类似存款利息差。",
                "汇率一天的波动常常大于一整年的利差；利差不构成换汇或持有的理由。"),
            ["adjustment"] = new(
                "adjustment",
                "复权（前复权/后复权）",
                "把分红、拆股造成的价格跳空调整回去，让 K 线前后可比。前复权以最新价为基准，后复权以最早价为基准。",
                "两种复权方式算出的涨跌幅不同，同一张图里不要混用；不复权价格会在除权日出现假跳水。"),
            ["slippage"] = new(
                "slippage",
                "滑点",
                "你想成交的价格与实际成交价格之间的差，来自下单到成交之间市场的变动。",
                "真实成交价通常比回测里假设的更差；频繁交易的策略受滑点影响最大，会侵蚀纸面利润。"),
            ["unpriced"] = new(
                "unpriced",
                "未定价",
                "这只持仓暂时取不到市场价，它的市值没有计入总资产合计（按 0 计入会是错的）。",
                "只要存在未定价持仓，总资产、集中度等结论都是不完整的，界面上会明确标出。"),
            ["reversal"] = new(
                "reversal",
                "冲正",
                "记错账时的更正方式：原流水原样保留，再补一条等额反向的冲正记录，两者一起看时这笔业务等于没发生。",
                "冲正不是删除——历史永远留痕；冲正后现金、持仓和收益会回到记账之前。"),
            ["drift"] = new(
                "drift",
                "配置偏离",
                "实际持仓比例与你自己设的目标比例之间的差距，软件只报告差距的算术事实。",
                "目标是你自己定的，不是软件的建议；偏离本身不要求你立刻交易。"),
        };

    /// <summary>Returns the explanation for a key, or null — never a fabricated definition.</summary>
    public static TermExplanation? Find(string key) =>
        Terms.TryGetValue(key ?? "", out var term) ? term : null;

    /// <summary>Compact tooltip text: term, plain meaning, caveat. Null when the key is unknown.</summary>
    public static string? Tooltip(string key) =>
        Find(key) is { } term
            ? $"{term.Plain}\n\n注意：{term.Caveat}"
            : null;
}
