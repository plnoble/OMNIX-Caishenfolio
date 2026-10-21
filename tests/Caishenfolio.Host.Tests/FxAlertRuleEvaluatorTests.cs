using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// N2.4 FX watch rules. The contract: the wording states that a fact the user chose was
/// triggered — never that now is a good time to convert (plan §8 forbids that phrasing) —
/// and an input that cannot be fetched is reported as not-evaluated, never guessed.
/// </summary>
public class FxAlertRuleEvaluatorTests
{
    private static FxAlertRule Band(string pair, decimal? lower, decimal? upper) => new()
    {
        Kind = FxAlertRuleKind.RateBand,
        Pair = pair,
        Lower = lower,
        Upper = upper,
    };

    private static FxAlertRule Advantage(string basis = "CNY/USD/JPY", decimal threshold = 3m) => new()
    {
        Kind = FxAlertRuleKind.HeldAdvantageAbove,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        HeldCurrency = "JPY",
        ThresholdPct = threshold,
    };

    private static FxAlertRule NetArrival(decimal threshold = 1000m) => new()
    {
        Kind = FxAlertRuleKind.NetArrivalAbove,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        HeldCurrency = "JPY",
        ThresholdAmount = threshold,
        // Remediation WP2: BOTH cost fields must be explicitly stated (0 = confirmed none);
        // either one missing (unknown) refuses the evaluation.
        SpreadPct = 0.5m,
        FixedFee = 1m,
    };

    private static FxAlertRule Breakeven() => new()
    {
        Kind = FxAlertRuleKind.BreakevenCrossed,
        BaseCurrency = "CNY",
        TargetCurrency = "USD",
        HeldCurrency = "JPY",
    };

    private static FxRuleEvaluationResult Evaluate(
        IReadOnlyList<FxAlertRule> rules,
        decimal? rate = null,
        FxPathFacts? facts = null)
    {
        (decimal Rate, string Source)? RateOf(FxAlertRule rule) => rate is { } r
            ? (r, "参考源，2026-09-05")
            : null;
        FxPathFacts? FactsOf(FxAlertRule rule) => facts;

        return FxAlertRuleEvaluator.Evaluate(rules, RateOf, FactsOf);
    }

    [Fact]
    public void ARateInsideTheBandAlertsWithTheFacts()
    {
        var result = Evaluate([Band("USD/CNY", 6.5m, 7.0m)], rate: 6.726m);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal(AlertKind.FxBand, alert.Kind);
        Assert.Equal("USD/CNY", alert.Symbol);
        Assert.Contains("6.726", alert.Message, StringComparison.Ordinal);
        Assert.Contains("你设定的区间", alert.Message, StringComparison.Ordinal);
        Assert.Contains("参考源", alert.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6.4)]  // below
    [InlineData(7.1)]  // above
    public void ARateOutsideTheBandStaysSilent(decimal rate)
    {
        var result = Evaluate([Band("USD/CNY", 6.5m, 7.0m)], rate: (decimal)rate);

        Assert.Empty(result.Alerts);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void OneSidedBandsWork()
    {
        var above = Evaluate([Band("USD/JPY", 160m, null)], rate: 165m);
        var below = Evaluate([Band("USD/JPY", null, 160m)], rate: 155m);

        Assert.Single(above.Alerts);
        Assert.Single(below.Alerts);
    }

    [Fact]
    public void AMissingQuoteIsANoteNotAGuess()
    {
        var result = Evaluate([Band("USD/CNY", 6.5m, 7.0m)], rate: null);

        Assert.Empty(result.Alerts);
        Assert.Contains(result.Notes, n => n.Contains("未取到", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAdvantageAtTheThresholdAlertsWithGrossCaveat()
    {
        var result = Evaluate(
            [Advantage()],
            facts: new FxPathFacts { AdvantagePct = 3.011m });

        var alert = Assert.Single(result.Alerts);
        Assert.Equal(AlertKind.FxAdvantage, alert.Kind);
        Assert.Contains("+3.011%", alert.Message, StringComparison.Ordinal);
        Assert.Contains("毛额", alert.Message, StringComparison.Ordinal);
        Assert.Contains("不判断换汇时点", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdvantageBelowTheThresholdStaysSilent()
    {
        var result = Evaluate(
            [Advantage()],
            facts: new FxPathFacts { AdvantagePct = 2.9m });

        Assert.Empty(result.Alerts);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void AnAdvantageThatCannotBeComputedIsANoteNotAnAlert()
    {
        var result = Evaluate([Advantage()], facts: null);

        Assert.Empty(result.Alerts);
        Assert.Contains(result.Notes, n => n.Contains("未能计算", StringComparison.Ordinal));
    }

    [Fact]
    public void NetArrivalAtTheTargetAlertsWithTheModelCaveat()
    {
        var result = Evaluate(
            [NetArrival(threshold: 1000m)],
            facts: new FxPathFacts { AdvantagePct = 3.011m, NetArrival = 1531.54m });

        var alert = Assert.Single(result.Alerts);
        Assert.Equal(AlertKind.FxNetArrival, alert.Kind);
        Assert.Contains("1,531.54", alert.Message, StringComparison.Ordinal);
        Assert.Contains("你设定的目标 1,000", alert.Message, StringComparison.Ordinal);
        Assert.Contains("取决于你填写的点差与手续费", alert.Message, StringComparison.Ordinal);
        Assert.Contains("不判断换汇时点", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NetArrivalBelowTheTargetStaysSilent()
    {
        var result = Evaluate(
            [NetArrival(threshold: 1600m)],
            facts: new FxPathFacts { AdvantagePct = 3.011m, NetArrival = 1531.54m });

        Assert.Empty(result.Alerts);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void NetArrivalWithoutACostModelIsANoteNotAGuess()
    {
        // No cost model: the rule is refused up front with a note explaining what to fill.
        var bare = NetArrival() with { SpreadPct = null, FixedFee = null };
        var result = Evaluate([bare], facts: new FxPathFacts { AdvantagePct = 3.011m, NetArrival = null });

        Assert.Empty(result.Alerts);
        Assert.Contains(result.Notes, n => n.Contains("比例成本", StringComparison.Ordinal));

        // With a model but an unknown net (no quote): a different, precise note.
        var withModel = NetArrival();
        var unknown = Evaluate([withModel], facts: new FxPathFacts { AdvantagePct = 3.011m, NetArrival = null });
        Assert.Empty(unknown.Alerts);
        Assert.Contains(unknown.Notes, n => n.Contains("未能计算净到账", StringComparison.Ordinal));
    }

    [Fact]
    public void ABreakevenCrossedAlertStatesBothRates()
    {
        var result = Evaluate(
            [Breakeven()],
            facts: new FxPathFacts
            {
                AdvantagePct = 3.011m,
                HeldCostRate = 24.5m,
                BreakevenRate = 23.783824m,
            });

        var alert = Assert.Single(result.Alerts);
        Assert.Equal(AlertKind.FxBreakeven, alert.Kind);
        Assert.Contains("1 CNY = 24.5", alert.Message, StringComparison.Ordinal);
        Assert.Contains("23.783824", alert.Message, StringComparison.Ordinal);
        Assert.Contains("+3.011%", alert.Message, StringComparison.Ordinal);
        Assert.Contains("不预测未来汇率", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathStillBehindItsBreakevenStaysSilent()
    {
        var result = Evaluate(
            [Breakeven()],
            facts: new FxPathFacts
            {
                AdvantagePct = -1.2m,
                HeldCostRate = 20.0m,
                BreakevenRate = 23.783824m,
            });

        Assert.Empty(result.Alerts);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void ABreakevenWithoutAFactsIsANote()
    {
        var result = Evaluate([Breakeven()], facts: null);

        Assert.Empty(result.Alerts);
        Assert.Contains(result.Notes, n => n.Contains("未能计算盈亏平衡", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledRulesAreSkipped()
    {
        var rules = new[]
        {
            Band("USD/CNY", 6.5m, 7.0m) with { Enabled = false },
        };

        var result = Evaluate(rules, rate: 6.726m);

        Assert.Empty(result.Alerts);
        Assert.Empty(result.Notes);
    }

    /// <summary>
    /// The plan's explicit wording ban (§8 N2.4): no rule's notification may ever claim a
    /// best time to convert. What the messages may do is negate the claim — the band alert
    /// ends with "不代表现在是换汇的好时机". Every rule kind's message goes through here.
    /// </summary>
    [Fact]
    public void NoWordingEverClaimsAGoodTimeToConvert()
    {
        var fullFacts = new FxPathFacts
        {
            AdvantagePct = 5m,
            NetArrival = 1531.54m,
            HeldCostRate = 24.5m,
            BreakevenRate = 23.783824m,
        };
        var messages = new List<string>();
        foreach (var alert in Evaluate(
                     [Band("USD/CNY", 6.5m, 7.0m), Advantage(), NetArrival(), Breakeven()],
                     rate: 6.726m,
                     facts: fullFacts).Alerts)
        {
            messages.Add(alert.Title);
            messages.Add(alert.Message);
        }

        var text = string.Join("\n", messages);
        Assert.Contains("不代表现在是换汇的好时机", text, StringComparison.Ordinal);
        Assert.DoesNotContain("最佳换汇时机", text, StringComparison.Ordinal);
        Assert.DoesNotContain("建议换汇", text, StringComparison.Ordinal);
        Assert.DoesNotContain("该换了", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesRoundTripThroughNotificationSettings()
    {
        var settings = NotificationSettings.Default with
        {
            FxAlertRules =
            [
                Band("USD/CNY", 6.5m, 7.0m),
                Advantage(threshold: 3m),
                NetArrival(threshold: 1000m),
                Breakeven(),
            ],
        };

        var restored = NotificationSettings.FromJson(settings.ToJson());

        Assert.Equal(4, restored.FxAlertRules.Count);
        Assert.Equal(FxAlertRuleKind.RateBand, restored.FxAlertRules[0].Kind);
        Assert.Equal(6.5m, restored.FxAlertRules[0].Lower);
        Assert.Equal(FxAlertRuleKind.HeldAdvantageAbove, restored.FxAlertRules[1].Kind);
        Assert.Equal(3m, restored.FxAlertRules[1].ThresholdPct);
        Assert.Equal(FxAlertRuleKind.NetArrivalAbove, restored.FxAlertRules[2].Kind);
        Assert.Equal(1000m, restored.FxAlertRules[2].ThresholdAmount);
        Assert.Equal(FxAlertRuleKind.BreakevenCrossed, restored.FxAlertRules[3].Kind);
        Assert.Empty(NotificationSettings.Default.FxAlertRules);
    }
}

// --- P1-06 / P1-07：净到账费用模型与外币移动加权成本 ---------------------------------

public class NetArrivalCostModelTests
{
    /// <summary>反例 #7：净到账规则必须带费用模型；阈值上下各一案例。</summary>
    [Fact]
    public void ANetArrivalRuleWithoutACostModelIsNotEvaluated()
    {
        var rule = new FxAlertRule
        {
            Kind = FxAlertRuleKind.NetArrivalAbove,
            BaseCurrency = "CNY", HeldCurrency = "JPY", TargetCurrency = "USD",
            ThresholdAmount = 13m,
            // No SpreadPct, no FixedFee.
        };

        var result = FxAlertRuleEvaluator.Evaluate(
            [rule], _ => null, _ => new FxPathFacts { NetArrival = 99m });

        Assert.Empty(result.Alerts);
        Assert.Contains(result.Notes, n => n.Contains("比例成本"));
    }

    [Fact]
    public void ANetArrivalRuleWithCostsFiresAboveAndStaysSilentBelow()
    {
        var rule = new FxAlertRule
        {
            Kind = FxAlertRuleKind.NetArrivalAbove,
            BaseCurrency = "CNY", HeldCurrency = "JPY", TargetCurrency = "USD",
            ThresholdAmount = 13m,
            SpreadPct = 0.5m,
            FixedFee = 1m,
        };

        var above = FxAlertRuleEvaluator.Evaluate(
            [rule], _ => null, _ => new FxPathFacts { NetArrival = 13.2m });
        Assert.Single(above.Alerts);
        Assert.Contains("费率模型", above.Alerts[0].Message);

        var below = FxAlertRuleEvaluator.Evaluate(
            [rule], _ => null, _ => new FxPathFacts { NetArrival = 12.9m });
        Assert.Empty(below.Alerts);
    }
}

public class HeldCurrencyMovingWeightedCostTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_mwcost_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    /// <summary>反例 #8：两批不同汇率购汇后，成本=实际投入（不是最后一笔反推）。</summary>
    [Fact]
    public void TwoBatchesBlendToTheActualInvestedCost()
    {
        using var store = NewStore();
        // The review's counterexample: 100 CNY → 2 000 JPY, then 100 CNY → 2 500 JPY.
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 2, 2), 100m, "CNY", 2_500m, "JPY"),
        ]);

        var transactions = store.ListTransactions();
        var cost = HeldCurrencyCostCalculator.CostOf(transactions, "CNY", "JPY", 4_500m);

        // Actual invested: 200 CNY for 4 500 JPY — not 180 from the last rate.
        Assert.Equal(4_500m, cost.PricedAmount);
        Assert.Equal(200m, cost.PricedCostInBase);
        Assert.Equal(0m, cost.UnpricedAmount);
        Assert.Equal(200m, cost.BlendedCostInBase);
    }

    [Fact]
    public void PartiallyConvertingAwayReleasesTheAverageCost()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 2, 2), 100m, "CNY", 2_500m, "JPY"),
            // Spend half the pot back to CNY: releases the blended average (22.5 JPY/CNY).
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 3, 2), 2_250m, "JPY", 100m, "CNY"),
        ]);

        var transactions = store.ListTransactions();
        var balances = PositionCalculator.Replay(transactions).CashBalances;
        var remaining = balances.Single(b => b.Currency == "JPY").Amount; // 2 250
        var cost = HeldCurrencyCostCalculator.CostOf(transactions, "CNY", "JPY", remaining);

        // 200 in, half released at average → 100 CNY cost for the remaining 2 250 JPY.
        Assert.Equal(2_250m, cost.PricedAmount);
        Assert.Equal(100m, cost.PricedCostInBase);
        Assert.Equal(100m, cost.BlendedCostInBase);
    }

    [Fact]
    public void MoneyFromOutsideExchangesHasNoCostBasis()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            // Interest credited in JPY: no exchange, no cost basis.
            LedgerTransaction.Interest("acct", new DateOnly(2026, 2, 2), 50m, "JPY"),
        ]);

        var transactions = store.ListTransactions();
        var cost = HeldCurrencyCostCalculator.CostOf(transactions, "CNY", "JPY", 2_050m);

        // The 50 JPY has no honest cost — reported as unknown, not priced at the last rate.
        Assert.Equal(2_000m, cost.PricedAmount);
        Assert.Equal(50m, cost.UnpricedAmount);
        Assert.Null(cost.BlendedCostInBase);
        Assert.Null(cost.BlendedRateHeldPerBase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

// --- P1-R07：外币成本池覆盖消费、提现与手续费 -----------------------------------------

public class HeldCurrencyFullCashflowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_fullcost_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    private static HeldCurrencyCost CostOfLedger(PortfolioStore store, decimal? expectedBalance = null)
    {
        var transactions = store.ListTransactions();
        var balance = expectedBalance
                      ?? PositionCalculator.Replay(transactions).CashBalances
                          .Where(b => b.Currency == "JPY").Sum(b => b.Amount);
        return HeldCurrencyCostCalculator.CostOf(transactions, "CNY", "JPY", balance);
    }

    /// <summary>反例 #11：外币部分提现后，持有数量与成本同比下降。</summary>
    [Fact]
    public void PartialWithdrawalReleasesCostProportionally()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Withdraw("acct", new DateOnly(2026, 2, 2), 1_000m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        // Half the pot withdrawn: 1 000 JPY remain, cost released at the average → 50 CNY.
        Assert.Equal(1_000m, cost.PricedAmount);
        Assert.Equal(50m, cost.PricedCostInBase);
        Assert.Equal(0m, cost.UnpricedAmount);
    }

    /// <summary>反例 #12：换出外币并支付源币手续费时，手续费计入释放量。</summary>
    [Fact]
    public void SourceCurrencyFeeCountsAsReleased()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            // Convert 1 500 JPY back to CNY with a 10 JPY fee: 1 510 JPY leave the pool.
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 2, 2), 1_500m, "JPY", 75m, "CNY", fee: 10m),
        ]);

        var cost = CostOfLedger(store);
        // 2 000 − 1 510 = 490 JPY remain at the average 20 JPY/CNY → 24.5 CNY.
        Assert.Equal(490m, cost.PricedAmount);
        Assert.Equal(24.5m, Math.Round(cost.PricedCostInBase, 4));
    }

    [Fact]
    public void ForeignCurrencySpendingOnABuyReleasesAtAverage()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            // Buy a JPY-priced stock spending 800 JPY cash: the pool shrinks, cost releases.
            LedgerTransaction.Buy("acct", "TSE:7203", new DateOnly(2026, 2, 2), 10m, 80m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        // 2 000 − 800 = 1 200 JPY at the average → 60 CNY cost basis for the remaining cash.
        Assert.Equal(1_200m, cost.PricedAmount);
        Assert.Equal(60m, cost.PricedCostInBase);
    }

    [Fact]
    public void ExternalDepositFormsTheUnpricedPool()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Deposit("acct", new DateOnly(2026, 2, 2), 500m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        Assert.Equal(2_000m, cost.PricedAmount);
        Assert.Equal(100m, cost.PricedCostInBase);
        Assert.Equal(500m, cost.UnpricedAmount);
        Assert.Null(cost.BlendedCostInBase);
    }

    [Fact]
    public void MixedPoolsReleaseProportionallyOnSpending()
    {
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 3), 1_000m, "JPY"),
            // Spend half the 3 000 pot: each pool releases half.
            LedgerTransaction.Withdraw("acct", new DateOnly(2026, 2, 2), 1_500m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        Assert.Equal(1_000m, cost.PricedAmount);
        Assert.Equal(50m, cost.PricedCostInBase);
        Assert.Equal(500m, cost.UnpricedAmount);
    }

    [Fact]
    public void CrossAccountTransferIsConservativeUnderPerTransactionReplay()
    {
        // Remediation WP3: withdraw+deposit pairs are NO LONGER netted into a transfer —
        // no link proves the two rows belong together. The replay releases the withdrawn
        // amount's cost and books the arrival as unpriced: conservative (the cost basis is
        // understated, never fabricated), exactly as the plan §6.2 prescribes until an
        // explicit CashTransfer type exists.
        using var store = NewStore();
        store.AddTransactions(
        [
            // WP-03 R1: the bank account is funded before it converts.
            LedgerTransaction.OpeningCash("bank", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("bank", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Withdraw("bank", new DateOnly(2026, 1, 5), 2_000m, "JPY"),
            LedgerTransaction.Deposit("broker", new DateOnly(2026, 1, 5), 2_000m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        // The withdrawn 2 000 released its cost; the unrelated-looking 2 000 arrives unpriced.
        Assert.Equal(0m, cost.PricedAmount);
        Assert.Equal(2_000m, cost.UnpricedAmount);
        // No honest blended cost exists → the callers must treat it as unknown.
        Assert.Null(cost.BlendedCostInBase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

// --- 整改计划 WP2：提醒规则的费用三态语义 ---------------------------------------------

public class CostSemanticsRuleTests
{
    /// <summary>验收 #6：只填固定费（比例成本未知）的净到账规则不得评估。</summary>
    [Fact]
    public void FixedFeeOnlyRuleIsNotEvaluatedWithAMissingVariableCostNote()
    {
        var rule = new FxAlertRule
        {
            Kind = FxAlertRuleKind.NetArrivalAbove,
            BaseCurrency = "CNY", TargetCurrency = "USD", HeldCurrency = "JPY",
            ThresholdAmount = 13m,
            // HasCostModel today counts a fixed fee alone — but a net figure needs the
            // variable cost known too (None ≠ 0). This rule must be refused.
            SpreadPct = null,
            FixedFee = 1m,
        };

        var result = FxAlertRuleEvaluator.Evaluate(
            [rule], _ => null, _ => new FxPathFacts { NetArrival = 99m });

        Assert.Empty(result.Alerts);
        Assert.Contains(result.Notes, n => n.Contains("比例成本"));
    }

    /// <summary>验收 #7：比例成本显式 0 + 固定费 1 → 正常评估。</summary>
    [Fact]
    public void ExplicitZeroSpreadWithFixedFeeEvaluatesNormally()
    {
        var rule = new FxAlertRule
        {
            Kind = FxAlertRuleKind.NetArrivalAbove,
            BaseCurrency = "CNY", TargetCurrency = "USD", HeldCurrency = "JPY",
            ThresholdAmount = 13m,
            SpreadPct = 0m, // explicit zero — the user confirmed no spread
            FixedFee = 1m,
        };

        var above = FxAlertRuleEvaluator.Evaluate(
            [rule], _ => null, _ => new FxPathFacts { NetArrival = 13.2m });
        Assert.Single(above.Alerts);

        var below = FxAlertRuleEvaluator.Evaluate(
            [rule], _ => null, _ => new FxPathFacts { NetArrival = 12.9m });
        Assert.Empty(below.Alerts);
    }
}

// --- 整改计划 WP3：同日无关流水不得抵消 ---------------------------------------------

public class SameDayFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_sameday_tests", Guid.NewGuid().ToString("N"));

    private static HeldCurrencyCost CostOfLedger(PortfolioStore store)
    {
        var transactions = store.ListTransactions();
        var balance = PositionCalculator.Replay(transactions).CashBalances
            .Where(b => b.Currency == "JPY").Sum(b => b.Amount);
        return HeldCurrencyCostCalculator.CostOf(transactions, "CNY", "JPY", balance);
    }

    /// <summary>验收 #9：同日提现 1,000 + 无关外部入账 1,000 不得抵消。</summary>
    [Fact]
    public void SameDayWithdrawAndUnrelatedDepositDoNotNet()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            // Day 2: withdraw 1 000 JPY (spending it) AND an unrelated 1 000 JPY arrives
            // from outside. Net change zero — but they are two different facts.
            LedgerTransaction.Withdraw("acct", new DateOnly(2026, 2, 2), 1_000m, "JPY"),
            LedgerTransaction.Deposit("acct", new DateOnly(2026, 2, 2), 1_000m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        // Correct (fail-closed): the withdrawal releases 50 CNY of cost; the deposit is
        // unpriced. The old daily-net logic kept all 2 000 JPY priced at 100 CNY.
        Assert.Equal(1_000m, cost.PricedAmount);
        Assert.Equal(50m, cost.PricedCostInBase);
        Assert.Equal(1_000m, cost.UnpricedAmount);
    }

    /// <summary>验收 #9 变体：提现 1,500、入账 1,000 → 保留 500 旧成本 + 1,000 未知。</summary>
    [Fact]
    public void PartialOverlapKeepsOldCostForTheRemainder()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.Withdraw("acct", new DateOnly(2026, 2, 2), 1_500m, "JPY"),
            LedgerTransaction.Deposit("acct", new DateOnly(2026, 2, 2), 1_000m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        // 2 000 − 1 500 = 500 JPY keep their average cost (25 CNY); the fresh 1 000 is unpriced.
        Assert.Equal(500m, cost.PricedAmount);
        Assert.Equal(25m, cost.PricedCostInBase);
        Assert.Equal(1_000m, cost.UnpricedAmount);
    }

    /// <summary>同日先购汇再消费：顺序逐笔，消费按当时均价释放。</summary>
    [Fact]
    public void SameDayBuyThenSpendReplaysInOrder()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.AddTransactions(
        [
            // WP-03 R1: fund the account first — spending money it never had is now refused.
            LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 1), 500m, "CNY", "建账余额"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 1, 2), 100m, "CNY", 2_000m, "JPY"),
            LedgerTransaction.FxExchange("acct", new DateOnly(2026, 2, 2), 100m, "CNY", 3_000m, "JPY"),
            // Same day: spend 1 000 of the now-5 000 pool at the blended average.
            LedgerTransaction.Withdraw("acct", new DateOnly(2026, 2, 2), 1_000m, "JPY"),
        ]);

        var cost = CostOfLedger(store);
        // Blended: 5 000 JPY for 200 CNY; spend 1 000 → 4 000 JPY for 160 CNY.
        Assert.Equal(4_000m, cost.PricedAmount);
        Assert.Equal(160m, cost.PricedCostInBase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
