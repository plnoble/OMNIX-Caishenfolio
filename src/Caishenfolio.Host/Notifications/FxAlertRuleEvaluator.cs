using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Notifications;

public enum FxAlertRuleKind
{
    /// <summary>Rate entered a band the user cares about. Facts only — never "good time to convert".</summary>
    RateBand,
    /// <summary>Held-currency path advantage over fresh-money path reached a threshold (gross).</summary>
    HeldAdvantageAbove,
    /// <summary>Net arrival of converting the held currency reached a target amount.</summary>
    NetArrivalAbove,
    /// <summary>The held path crossed its breakeven: it is no longer behind fresh money (gross).</summary>
    BreakevenCrossed,
}

/// <summary>
/// One user-defined FX watch rule (plan §7 N2.4). Storage lives in
/// <see cref="NotificationSettings"/> because that record is already encrypted at rest;
/// the rules themselves carry no secrets.
/// </summary>
public sealed record FxAlertRule
{
    public required FxAlertRuleKind Kind { get; init; }

    /// <summary>RateBand: the pair to watch, e.g. <c>USD/CNY</c> meaning 1 USD = x CNY.</summary>
    public string Pair { get; init; } = "";

    /// <summary>RateBand lower bound (units of quote per base); null = no lower bound.</summary>
    public decimal? Lower { get; init; }

    /// <summary>RateBand upper bound; null = no upper bound.</summary>
    public decimal? Upper { get; init; }

    /// <summary>
    /// HeldAdvantageAbove / NetArrivalAbove / BreakevenCrossed: base, target and held
    /// currencies, e.g. CNY / USD / JPY — "does my held JPY convert to more USD than
    /// fresh CNY does?"
    /// </summary>
    public string BaseCurrency { get; init; } = "";

    public string TargetCurrency { get; init; } = "";

    public string HeldCurrency { get; init; } = "";

    /// <summary>HeldAdvantageAbove: threshold in percent, e.g. 3 means +3%.</summary>
    public decimal? ThresholdPct { get; init; }

    /// <summary>NetArrivalAbove: target amount in the target currency, e.g. 1000 USD.</summary>
    public decimal? ThresholdAmount { get; init; }

    /// <summary>
    /// The cost model the net-arrival rule is evaluated against (P1-06): percentage spread
    /// plus a fixed fee, in the target currency. Required for NetArrivalAbove — without it
    /// the rule cannot fire and is reported as not evaluated rather than priced as free.
    /// </summary>
    public decimal? SpreadPct { get; init; }

    public decimal? FixedFee { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>True when this rule has the cost inputs its kind needs.</summary>
    public bool HasCostModel => SpreadPct is not null || FixedFee is not null;

    public string Describe() => Kind switch
    {
        FxAlertRuleKind.RateBand =>
            $"{Normalize(Pair)} 进入区间 [{Lower?.ToString("0.######") ?? "—"}, {Upper?.ToString("0.######") ?? "—"}]",
        FxAlertRuleKind.HeldAdvantageAbove =>
            $"已持有 {HeldCurrency} 换 {TargetCurrency} 相对 {BaseCurrency} 直换的优势 ≥ {ThresholdPct:0.###}%",
        FxAlertRuleKind.NetArrivalAbove =>
            $"已持有 {HeldCurrency} 换 {TargetCurrency} 的净到账 ≥ {ThresholdAmount:#,0.##} {TargetCurrency}",
        FxAlertRuleKind.BreakevenCrossed =>
            $"已持有 {HeldCurrency} 换 {TargetCurrency} 越过盈亏平衡（相对 {BaseCurrency} 直换不再吃亏）",
        _ => "未知规则",
    };

    internal static string Normalize(string code) =>
        string.IsNullOrWhiteSpace(code) ? "?" : code.Trim().ToUpperInvariant();
}

/// <summary>
/// What the path comparison knows about one base/target/held triple. Every field is
/// optional: the evaluator reports what it can and says what it cannot — never guesses.
/// </summary>
public sealed record FxPathFacts
{
    /// <summary>Held-path gross advantage over the fresh-money path, in percent (+3.011 means +3.011%).</summary>
    public decimal? AdvantagePct { get; init; }

    /// <summary>Net arrival of converting the whole held pot into the target currency.</summary>
    public decimal? NetArrival { get; init; }

    /// <summary>Historical cost in held units per 1 base (what the user actually paid).</summary>
    public decimal? HeldCostRate { get; init; }

    /// <summary>Today's implied held units per 1 base; the two paths tie when these are equal.</summary>
    public decimal? BreakevenRate { get; init; }
}

/// <summary>What one rule evaluation had available; lets a missing input be said, not faked.</summary>
public sealed record FxRuleEvaluationResult
{
    public required IReadOnlyList<PortfolioAlert> Alerts { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// One entry per enabled rule, saying whether the condition was met, was not met, or could
    /// not be judged at all (WP-02 R6). Without this a missing quote is indistinguishable from
    /// "condition false", which is what let a recovery look like a fresh re-entry.
    /// </summary>
    public IReadOnlyList<FxRuleOutcome> Outcomes { get; init; } = [];

    /// <summary>The alert keys whose condition could not be judged this run.</summary>
    public IReadOnlySet<string> UnknownAlertKeys =>
        Outcomes
            .Where(outcome => outcome.State == FxRuleState.Unknown)
            .Select(outcome => outcome.AlertKey)
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>The three states a rule can be in after one evaluation (R6/A10).</summary>
public enum FxRuleState
{
    /// <summary>The condition holds now.</summary>
    Met,
    /// <summary>The condition was judged and does not hold.</summary>
    NotMet,
    /// <summary>It could not be judged (quote missing, cost unknown): neither met nor false.</summary>
    Unknown,
}

/// <summary>
/// The outcome for one rule, carrying the alert identity it would have used when met, so the
/// gatekeeper can tell "left the band" apart from "could not look" (R6/A11).
/// </summary>
public sealed record FxRuleOutcome
{
    /// <summary>Same identity <see cref="AlertGatekeeper.KeyOf"/> gives the alert when it fires.</summary>
    public required string AlertKey { get; init; }

    public required FxRuleState State { get; init; }

    /// <summary>Why it is UNKNOWN — the failing input, in the user's words. Empty otherwise.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Evaluates FX watch rules (plan §7 N2.4). Pure: rates and path facts arrive as injected
/// lookups, so thresholds and boundaries are unit-testable while the production wiring
/// fetches through the analytics core. The wording states trigger facts only — "进入你设定
/// 的区间", never "最佳换汇时机" (plan §8 forbids that phrasing).
/// </summary>
public static class FxAlertRuleEvaluator
{
    public static FxRuleEvaluationResult Evaluate(
        IReadOnlyList<FxAlertRule> rules,
        Func<FxAlertRule, (decimal Rate, string Source)?> rateOf,
        Func<FxAlertRule, FxPathFacts?> factsOf)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rateOf);
        ArgumentNullException.ThrowIfNull(factsOf);

        var alerts = new List<PortfolioAlert>();
        var notes = new List<string>();
        var outcomes = new List<FxRuleOutcome>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            var (state, reason, alert, note) = rule.Kind switch
            {
                FxAlertRuleKind.RateBand => EvaluateBand(rule, rateOf),
                FxAlertRuleKind.HeldAdvantageAbove => EvaluateAdvantage(rule, factsOf),
                FxAlertRuleKind.NetArrivalAbove => EvaluateNetArrival(rule, factsOf),
                FxAlertRuleKind.BreakevenCrossed => EvaluateBreakeven(rule, factsOf),
                _ => (FxRuleState.Unknown, $"未知的规则类型 {rule.Kind}，未评估。", (PortfolioAlert?)null, (string?)null),
            };

            if (alert is not null)
            {
                alerts.Add(alert);
            }

            if (note is not null)
            {
                notes.Add(note);
            }

            outcomes.Add(new FxRuleOutcome
            {
                AlertKey = AlertGatekeeper.KeyOf(Probe(rule)),
                State = state,
                Reason = reason,
            });
        }

        return new FxRuleEvaluationResult { Alerts = alerts, Notes = notes, Outcomes = outcomes };
    }

    /// <summary>
    /// The identity this rule's alert has when it fires — kind, symbol and title only, no live
    /// message. The outcome uses it so the gatekeeper can match an UNKNOWN evaluation to the
    /// alert it might have produced (R6).
    /// </summary>
    private static PortfolioAlert Probe(FxAlertRule rule) => rule.Kind switch
    {
        FxAlertRuleKind.RateBand => new PortfolioAlert
        {
            Kind = AlertKind.FxBand,
            Severity = AlertSeverity.Info,
            Symbol = FxAlertRule.Normalize(rule.Pair),
            Title = "汇率进入你设定的区间",
            Message = "",
        },
        FxAlertRuleKind.HeldAdvantageAbove => new PortfolioAlert
        {
            Kind = AlertKind.FxAdvantage,
            Severity = AlertSeverity.Info,
            Symbol = $"{rule.HeldCurrency}/{rule.TargetCurrency}",
            Title = "已持有路径优势达到阈值",
            Message = "",
        },
        FxAlertRuleKind.NetArrivalAbove => new PortfolioAlert
        {
            Kind = AlertKind.FxNetArrival,
            Severity = AlertSeverity.Info,
            Symbol = $"{rule.HeldCurrency}/{rule.TargetCurrency}",
            Title = "净到账达到你设定的目标",
            Message = "",
        },
        FxAlertRuleKind.BreakevenCrossed => new PortfolioAlert
        {
            Kind = AlertKind.FxBreakeven,
            Severity = AlertSeverity.Info,
            Symbol = $"{rule.HeldCurrency}/{rule.TargetCurrency}",
            Title = "已持有路径越过盈亏平衡",
            Message = "",
        },
        _ => new PortfolioAlert
        {
            Kind = AlertKind.FxBand,
            Severity = AlertSeverity.Info,
            Symbol = "",
            Title = "未知规则",
            Message = "",
        },
    };

    private static (FxRuleState State, string Reason, PortfolioAlert? Alert, string? Note) EvaluateBand(
        FxAlertRule rule,
        Func<FxAlertRule, (decimal Rate, string Source)?> rateOf)
    {
        var pair = FxAlertRule.Normalize(rule.Pair);
        if (pair == "?")
        {
            return (FxRuleState.Unknown, "这条汇率区间规则没有货币对，无法判断。", null,
                "一条汇率区间规则没有货币对，已跳过。");
        }

        if (rule.Lower is null && rule.Upper is null)
        {
            return (FxRuleState.Unknown, $"规则 {pair} 的区间上下限都为空，无法判断。", null,
                $"规则 {pair}：区间上下限都为空，已跳过。");
        }

        if (rateOf(rule) is not { } quote)
        {
            return (FxRuleState.Unknown, $"本次未取到 {pair} 的报价：条件是否成立未知，不是条件不成立。", null,
                $"规则 {pair}：本次未取到该货币对的报价，未评估。");
        }

        var inBand = (rule.Lower is null || quote.Rate >= rule.Lower)
                     && (rule.Upper is null || quote.Rate <= rule.Upper);
        if (!inBand)
        {
            return (FxRuleState.NotMet, "", null, null);
        }

        return (FxRuleState.Met, "", new PortfolioAlert
        {
            Kind = AlertKind.FxBand,
            Severity = AlertSeverity.Info,
            Symbol = pair,
            Title = "汇率进入你设定的区间",
            Message = $"{pair} 当前 {quote.Rate:0.######}，在你设定的区间 " +
                      $"[{rule.Lower?.ToString("0.######") ?? "—"}, {rule.Upper?.ToString("0.######") ?? "—"}] 内" +
                      $"（{quote.Source}）。这只是你设定的事实被触发，不代表现在是换汇的好时机。",
        }, null);
    }

    private static (FxRuleState State, string Reason, PortfolioAlert? Alert, string? Note) EvaluateAdvantage(
        FxAlertRule rule,
        Func<FxAlertRule, FxPathFacts?> factsOf)
    {
        if (rule.ThresholdPct is not { } threshold)
        {
            return (FxRuleState.Unknown, "这条优势阈值规则没有阈值，无法判断。", null,
                "一条优势阈值规则没有阈值，已跳过。");
        }

        if (factsOf(rule)?.AdvantagePct is not { } advantage)
        {
            return (FxRuleState.Unknown,
                $"未能计算相对优势（缺报价，或账本里没有 {rule.HeldCurrency} 的持仓成本），条件是否成立未知。", null,
                $"规则（{rule.BaseCurrency}→{rule.HeldCurrency}→{rule.TargetCurrency}）：" +
                "本次未能计算相对优势（缺报价，或账本里没有该外币的持仓成本），未评估。");
        }

        if (advantage < threshold)
        {
            return (FxRuleState.NotMet, "", null, null);
        }

        return (FxRuleState.Met, "", new PortfolioAlert
        {
            Kind = AlertKind.FxAdvantage,
            Severity = AlertSeverity.Info,
            Symbol = $"{rule.HeldCurrency}/{rule.TargetCurrency}",
            Title = "已持有路径优势达到阈值",
            Message = $"已持有 {rule.HeldCurrency} 换 {rule.TargetCurrency}，相对新 {rule.BaseCurrency} 直换的" +
                      $"毛额优势为 {advantage:+0.###;-0.###}%，达到你设定的阈值 {threshold:0.###}%。" +
                      "毛额未计点差与手续费；实际到手以银行报价为准，软件不判断换汇时点。",
        }, null);
    }

    private static (FxRuleState State, string Reason, PortfolioAlert? Alert, string? Note) EvaluateNetArrival(
        FxAlertRule rule,
        Func<FxAlertRule, FxPathFacts?> factsOf)
    {
        if (rule.ThresholdAmount is not { } threshold)
        {
            return (FxRuleState.Unknown, "这条净到账目标规则没有目标金额，无法判断。", null,
                "一条净到账目标规则没有目标金额，已跳过。");
        }

        // Remediation WP2: the VARIABLE cost must be explicitly stated — None means unknown,
        // never zero. A lone fixed fee with an unknown spread produces a net that pretends
        // the spread away, so the rule is refused until the user states it (0 counts).
        if (rule.SpreadPct is null)
        {
            return (FxRuleState.Unknown,
                "净到账规则需要显式填写比例成本（点差等，填 0 表示确认无点差）；未填写是未知而不是零，条件是否成立未知。", null,
                $"规则（{rule.BaseCurrency}→{rule.HeldCurrency}→{rule.TargetCurrency}）：" +
                "净到账规则需要显式填写比例成本（点差等，填 0 表示确认无点差）；" +
                "未填写的比例成本是未知而不是零，净额未评估。");
        }

        if (rule.FixedFee is null)
        {
            return (FxRuleState.Unknown,
                "固定手续费未填写（未知≠0），净到账算不出来，条件是否成立未知。", null,
                $"规则（{rule.BaseCurrency}→{rule.HeldCurrency}→{rule.TargetCurrency}）：" +
                "固定手续费未填写（未知≠0）；请填 0 表示确认无固定费，或填写实际金额。");
        }

        if (factsOf(rule)?.NetArrival is not { } net)
        {
            return (FxRuleState.Unknown,
                "未能计算净到账（缺报价、缺成本输入，或账本里没有该外币的持仓），条件是否成立未知。", null,
                $"规则（{rule.BaseCurrency}→{rule.HeldCurrency}→{rule.TargetCurrency}）：" +
                "本次未能计算净到账（缺报价、缺成本输入，或账本里没有该外币的持仓），未评估。");
        }

        if (net < threshold)
        {
            return (FxRuleState.NotMet, "", null, null);
        }

        return (FxRuleState.Met, "", new PortfolioAlert
        {
            Kind = AlertKind.FxNetArrival,
            Severity = AlertSeverity.Info,
            Symbol = $"{rule.HeldCurrency}/{rule.TargetCurrency}",
            Title = "净到账达到你设定的目标",
            Message = $"按你输入的费率模型，已持有 {rule.HeldCurrency} 全部换成 {TargetText(rule)} 的净到账约 " +
                      $"{net:#,0.##} {rule.TargetCurrency}，达到你设定的目标 {threshold:#,0.##} {rule.TargetCurrency}。" +
                      "净额取决于你填写的点差与手续费；实际到手以银行报价为准，软件不判断换汇时点。",
        }, null);
    }

    private static (FxRuleState State, string Reason, PortfolioAlert? Alert, string? Note) EvaluateBreakeven(
        FxAlertRule rule,
        Func<FxAlertRule, FxPathFacts?> factsOf)
    {
        var facts = factsOf(rule);
        if (facts?.AdvantagePct is not { } advantage
            || facts.HeldCostRate is not { } cost
            || facts.BreakevenRate is not { } breakeven)
        {
            return (FxRuleState.Unknown,
                "未能计算盈亏平衡（缺报价，或账本里没有该外币的持仓成本），条件是否成立未知。", null,
                $"规则（{rule.BaseCurrency}→{rule.HeldCurrency}→{rule.TargetCurrency}）：" +
                "本次未能计算盈亏平衡（缺报价，或账本里没有该外币的持仓成本），未评估。");
        }

        // The condition "not behind anymore" is advantage >= 0; re-entry semantics in the
        // gatekeeper make this fire again each time the sign flips back.
        if (advantage < 0m)
        {
            return (FxRuleState.NotMet, "", null, null);
        }

        return (FxRuleState.Met, "", new PortfolioAlert
        {
            Kind = AlertKind.FxBreakeven,
            Severity = AlertSeverity.Info,
            Symbol = $"{rule.HeldCurrency}/{rule.TargetCurrency}",
            Title = "已持有路径越过盈亏平衡",
            Message = $"你的历史成本是 1 {rule.BaseCurrency} = {cost:0.######} {rule.HeldCurrency}；" +
                      $"当前隐含的盈亏平衡价是 1 {rule.BaseCurrency} = {breakeven:0.######} {rule.HeldCurrency}。" +
                      $"按当前参考价，已持有 {rule.HeldCurrency} 换 {TargetText(rule)} 的毛额优势为 {advantage:+0.###;-0.###}%（不再吃亏）。" +
                      "这只说明历史成本与当前参考价的关系，不预测未来汇率，也不构成换汇建议。",
        }, null);
    }

    private static string TargetText(FxAlertRule rule) => rule.TargetCurrency;
}