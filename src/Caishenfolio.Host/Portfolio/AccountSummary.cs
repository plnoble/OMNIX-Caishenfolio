using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// The outcome of a return calculation, including whether it is complete and — when it is not —
/// exactly what was missing.
///
/// A metric that cannot be computed must never be reported as a bare null: the caller cannot
/// tell "not enough data" from "zero" from "the code gave up". It must also never be computed
/// from a substituted input such as today's exchange rate standing in for the flow's own day
/// (WP-01 R5). <see cref="MissingInputs"/> names the gap in the user's own terms.
/// </summary>
public sealed record ReturnAssessment
{
    /// <summary>The rate, or null when it could not be computed. Null is only meaningful together
    /// with <see cref="IsComplete"/> = false and a reason.</summary>
    public double? Value { get; init; }

    /// <summary>True when every input the metric needed was available.</summary>
    public required bool IsComplete { get; init; }

    /// <summary>Missing inputs, phrased so a date is visible, e.g. <c>2025-01-02 缺 JPY→CNY 汇率</c>.</summary>
    public IReadOnlyList<string> MissingInputs { get; init; } = [];

    /// <summary>Why the metric is missing, in one sentence, or empty when it is complete.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Every input was there; <paramref name="value"/> may still be null when the series
    /// itself cannot support a rate (e.g. all flows share one sign), which
    /// <paramref name="reason"/> explains.</summary>
    public static ReturnAssessment Resolved(double? value, string reason = "") =>
        new() { Value = value, IsComplete = true, Reason = reason };

    /// <summary>Something was missing. The gap is named, never papered over.</summary>
    public static ReturnAssessment Missing(string reason, IReadOnlyList<string> missingInputs) =>
        new() { IsComplete = false, Reason = reason, MissingInputs = missingInputs };
}

/// <summary>
/// One account's headline numbers (WP-01 目标 3): what it is worth, what was put in, what it
/// earned, and the rates — each of which may honestly be unknown.
/// </summary>
public sealed record AccountSummary
{
    public required string AccountId { get; init; }

    public required string Name { get; init; }

    /// <summary>The account's own currency; every money figure here is already in the base currency.</summary>
    public required string Currency { get; init; }

    public required AccountValuationMode Mode { get; init; }

    /// <summary>
    /// False when a balance-mode account has not reported a total yet (WP-01 D3). Its value is
    /// unknown, which is not the same as zero, so <see cref="TotalValueBase"/> stays null.
    /// </summary>
    public required bool ValueKnown { get; init; }

    /// <summary>Base-currency value, or null when unknown.</summary>
    public Money? TotalValueBase { get; init; }

    /// <summary>Money put in and not yet taken out, in the base currency; null when a flow could
    /// not be converted (so the total would be wrong).</summary>
    public Money? NetInvestedBase { get; init; }

    /// <summary>累计收益 = 总资产 − 净投入. Null whenever either side is unknown.</summary>
    public Money? CumulativePnlBase =>
        TotalValueBase is { } value && NetInvestedBase is { } invested ? value - invested : null;

    /// <summary>资金加权收益率 (XIRR), with the reason spelled out when it is missing.</summary>
    public required ReturnAssessment MoneyWeightedReturn { get; init; }

    /// <summary>
    /// 年化: the cumulative return (累计收益 ÷ 净投入) compounded over the account's holding
    /// span. Distinct from <see cref="MoneyWeightedReturn"/>, which is itself an annual rate.
    /// Null when it cannot be computed.
    /// </summary>
    public double? Annualized { get; init; }

    /// <summary>Account-scoped reasons this account's numbers are incomplete.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}