namespace Caishenfolio.Desktop.Wealth;

/// <summary>One lesson: what the term means, in one or two sentences, and where in the app it applies.</summary>
public sealed record Lesson(string Title, string Body, string Where);

/// <summary>One learning tier and its lessons.</summary>
public sealed record LessonTier(string Tier, string Intro, IReadOnlyList<Lesson> Lessons);

/// <summary>
/// Original, in-app financial education (plan §11 N6): three tiers from first-account basics to
/// the biases that bite quant work, embedded where the tasks happen rather than as a separate
/// reading room. Every line is written for this app — no copied PDF content, no external
/// material, and nothing that tells the user what to do with their money.
/// </summary>
public static class FinanceLessons
{
    public static IReadOnlyList<LessonTier> Tiers { get; } =
    [
        new("小白", "刚建账本时最需要的几个概念——每一个都对应软件里的一个真实位置。",
        [
            new Lesson(
                "账户与现金",
                "账本只记录事实：每一笔入金、买入、卖出都会改变现金或持仓。错了不删，用冲正更正。",
                "账本页"),
            new Lesson(
                "持仓成本（移动加权平均）",
                "多次买入会抬高或摊低平均成本；卖出按当时的平均成本结转。卖出费税算进已实现盈亏，买入费税计入成本。",
                "持仓页 · 平均成本列"),
            new Lesson(
                "汇率方向",
                "本软件统一显示「1 单位 FROM = X 单位 TO」。银行挂牌方向相反时，先看清方向再比较，别直接比数字。",
                "汇率页 · 每条报价都标方向"),
            new Lesson(
                "分红与利息",
                "分红、票息、存款利息计入收益，不冲减成本；送股增加份额但不改变总成本，平均成本因此下降。",
                "账本页 · 类型列"),
            new Lesson(
                "分散与集中",
                "单一持仓、市场、货币占比的上限是你自己定的；超过上限只是提醒，不是操作指令。",
                "总览页 · 提醒区"),
            new Lesson(
                "未定价 ≠ 零",
                "取不到价格或汇率的持仓被排除在合计之外并明确标出——绝不按 0 计算，估值也因此显示「不完整」。",
                "总览页 · 顶部横幅"),
        ]),
        new("进阶", "开始问「这个数字怎么来的」之后，会遇到这些概念。",
        [
            new Lesson(
                "XIRR（资金加权年化）",
                "把每一笔出入金的时点都算进来的年化收益率。现金流混了多种货币时显示「—」——那不是出错，是不猜。",
                "总览页 · KPI"),
            new Lesson(
                "TWR（时间加权收益）",
                "剔除出入金的影响，衡量「这段资产本身」的表现，适合和 XIRR 对照看：两者差得远，说明择时动作影响大。",
                "总览页 · KPI"),
            new Lesson(
                "最大回撤",
                "历史净值从峰值到谷值最大的跌幅。「风险与模拟」页能把它归因到具体资产和汇率。",
                "风险与模拟页"),
            new Lesson(
                "再平衡的两条路",
                "卖出超配，或用新增资金补低配。只靠新增资金只能稀释超配、不能精确回到目标——风险页会把两条路并排算给你。",
                "风险与模拟页"),
            new Lesson(
                "换汇路径比较",
                "已持有的外币、新资金直换、绕道第三种货币，比的是**净到账**：费用足以让结论反转，所以费用要如实填。",
                "汇率页 · 路径比较"),
            new Lesson(
                "应急金",
                "在设置里标记过的应急钱不算「闲置现金」，闲置现金提醒会先把它扣掉再判断。",
                "设置页 · 引导第⑤问"),
        ]),
        new("高阶", "这些概念决定一份回测或一张证据卡值不值得信。",
        [
            new Lesson(
                "样本外检验",
                "把历史对半切开：前半段总结出的「规律」，在后半段还成立吗？证据卡的历史检验内置了这种前后半拆分。",
                "证据卡 · 历史检验"),
            new Lesson(
                "参数敏感性",
                "把可比样本从 20 放宽到 10/30，结论方向就翻转的话，说明结论依赖参数而不是事实——警惕过拟合。",
                "证据卡 · 历史检验"),
            new Lesson(
                "回测偏差",
                "回测好看不等于未来有效。先看样本外是否仍成立、扣成本后是否还有超额、最长连亏是否可承受——回测报告页把这三样都列出。",
                "回测报告页"),
            new Lesson(
                "相关性与共同回撤",
                "多个资产同时下跌时，分散会失效。本软件在数据质量足够之前不显示行业相关性——宁缺毋假。",
                "（规划中）"),
            new Lesson(
                "复权方式",
                "前复权、后复权、不复权会改变历史价格的形态；跨窗口混用会失真。证据卡的数据质量段会标注本次用的口径。",
                "证据卡 · 数据质量"),
        ]),
    ];
}
