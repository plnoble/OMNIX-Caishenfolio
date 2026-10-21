using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Portfolio;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Host.Notifications;

/// <summary>
/// Layer 2 of the background check (plan §8): the full price / concentration / data-quality
/// rule set with the app closed.
///
/// It runs a <see cref="PortfolioWorkspace"/> over the real ledger with the analytics core
/// as its pricing source — the same evaluator, the same thresholds, the same cross-check
/// setting the open app uses, so a foreground alert and a background alert are the same
/// statement. The core is borrowed, not owned: if one is already running (the app is open)
/// it is used as-is; otherwise one is started for the check and stopped afterwards, so a
/// background run never leaves a Python process behind.
///
/// Any failure propagates to the caller, which reports it as a limitation — never as a
/// reason to skip the deadline layer.
/// </summary>
/// <summary>
/// What layer 2 found: the alerts, plus the reasons some things could not be judged. The
/// reasons used to be dropped here, which is how an unreachable quote became indistinguishable
/// from "the condition is false" (WP-02 R6/A10).
/// </summary>
public sealed record BackgroundCheckResult
{
    public required IReadOnlyList<PortfolioAlert> Alerts { get; init; }

    /// <summary>Human-readable notes for whatever could not be evaluated.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>One outcome per enabled FX rule: met, not met, or unknown.</summary>
    public IReadOnlyList<FxRuleOutcome> FxOutcomes { get; init; } = [];

    /// <summary>
    /// Alert keys whose FX condition could not be judged this run. The notifier reads this property
    /// and hands it to the gatekeeper, which must keep their last verdict instead of writing an exit
    /// for a condition nobody actually judged (WP-02 R6/A10).
    /// </summary>
    public IReadOnlySet<string> UnknownAlertKeys =>
        FxOutcomes
            .Where(outcome => outcome.State == FxRuleState.Unknown)
            .Select(outcome => outcome.AlertKey)
            .ToHashSet(StringComparer.Ordinal);
}

public static class BackgroundExtendedCheck
{
    /// <summary>How long to wait for a freshly started core to answer health.</summary>
    private static readonly TimeSpan CoreStartupTimeout = TimeSpan.FromSeconds(60);

    public static async Task<BackgroundCheckResult> RunAsync(
        PortfolioStore store,
        string? coreRoot,
        string stateRoot,
        MarketCredentialsStore? credentialsStore = null,
        int port = 8765,
        HttpClient? http = null,
        IReadOnlyList<FxAlertRule>? fxRules = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        if (string.IsNullOrEmpty(coreRoot))
        {
            throw new InvalidOperationException("未找到分析核心（python 目录），无法执行价格检查。");
        }

        AnalyticsCoreProcessBroker? broker = null;
        AnalyticsCoreClient? client = null;
        try
        {
            // Prefer a core that is already up (the app may be open): reusing it costs one
            // health call, and we must not stop a process we did not start.
            if (await TryHealthAsync(port, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
            {
                client = AnalyticsCoreClient.ForLoopback(port);
            }
            else
            {
                broker = await StartCoreAsync(coreRoot, stateRoot, credentialsStore, port, cancellationToken)
                    .ConfigureAwait(false);
                client = new AnalyticsCoreClient(broker.BaseAddress, http);
                await WaitForHealthAsync(client, cancellationToken).ConfigureAwait(false);
            }

            var workspace = new PortfolioWorkspace(store)
            {
                PricingSource = new AnalyticsCorePricingSource(client),
            };
            var snapshot = await workspace
                .RefreshAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var alerts = snapshot.Alerts.ToList();
            var notes = new List<string>();
            IReadOnlyList<FxRuleOutcome> outcomes = [];

            // N2.4 FX watch rules: evaluated on the same core, appended to the same list.
            // Their failures are notes, not exceptions — one unreachable pair must not cost
            // the portfolio alerts that already succeeded.
            var activeRules = (fxRules ?? []).Where(r => r.Enabled).ToArray();
            if (activeRules.Length > 0)
            {
                var fx = await EvaluateFxRulesAsync(store, client, activeRules, cancellationToken)
                    .ConfigureAwait(false);
                alerts.AddRange(fx.Alerts);
                notes.AddRange(fx.Notes);
                outcomes = fx.Outcomes;
            }

            return new BackgroundCheckResult
            {
                Alerts = alerts,
                Notes = notes,
                FxOutcomes = outcomes,
            };
        }
        finally
        {
            client?.Dispose();
            broker?.Dispose();
        }
    }

    private static async Task<FxRuleEvaluationResult> EvaluateFxRulesAsync(
        PortfolioStore store,
        AnalyticsCoreClient client,
        IReadOnlyList<FxAlertRule> rules,
        CancellationToken cancellationToken)
    {
        // Prefetch everything asynchronously, then hand the pure evaluator plain lookups —
        // no blocking, and one unreachable pair degrades to a note instead of an exception.
        var rates = new Dictionary<string, (decimal Rate, string Source)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rules
                     .Where(r => r.Kind == FxAlertRuleKind.RateBand)
                     .Select(r => FxAlertRule.Normalize(r.Pair))
                     .Where(p => p.Contains('/'))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parts = pair.Split('/');
            try
            {
                var response = await client
                    .GetFxRateAsync(parts[0], parts[1], cancellationToken)
                    .ConfigureAwait(false);
                if (response.Ok && response.Data is { Rate: > 0m } data)
                {
                    rates[pair] = (data.Rate, $"{data.Provider}，{data.AsOf}");
                }
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // A pair that cannot be fetched is reported by the evaluator as a note.
            }
        }

        var facts = new Dictionary<string, FxPathFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules.Where(r => r.Kind != FxAlertRuleKind.RateBand))
        {
            // The cache key must carry everything the comparison depends on (P1-R06):
            // currencies AND the rule's cost model — otherwise the first rule's economics
            // (possibly cost-less) silently stands in for a second rule with real costs.
            var key = AdvantageKey(rule);
            if (facts.ContainsKey(key))
            {
                continue;
            }

            try
            {
                var held = HeldPositionOf(store, rule.BaseCurrency, rule.HeldCurrency);
                if (held is null)
                {
                    continue;
                }

                // The advantage is a ratio — the comparison amount cancels out — so any
                // positive base amount gives the same headline percentage. Net arrival is
                // quoted for the whole held pot, which is what the user would convert.
                // The rule's own cost model rides along (P1-06): without it the core is
                // fail-closed about net amounts and the rule would never fire.
                var costs = new Dictionary<string, FxPathRequestCost>(StringComparer.Ordinal)
                {
                    ["held_to_target"] = new(
                        SpreadPercent: rule.SpreadPct,
                        FixedFee: rule.FixedFee,
                        FeeCurrency: rule.FixedFee is null ? null : rule.TargetCurrency),
                    ["base_to_target"] = new(
                        SpreadPercent: rule.SpreadPct,
                        FixedFee: rule.FixedFee,
                        FeeCurrency: rule.FixedFee is null ? null : rule.TargetCurrency),
                };

                var response = await client
                    .PostFxPathsAsync(
                        rule.BaseCurrency,
                        rule.TargetCurrency,
                        baseAmount: 100m,
                        heldCurrency: rule.HeldCurrency,
                        heldAmount: held.Value.amount,
                        heldCostInBase: held.Value.cost,
                        costs: costs,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!response.Ok)
                {
                    continue;
                }

                var heldPath = response.Paths.FirstOrDefault(p => p.Key == "held_to_target");
                facts[key] = new FxPathFacts
                {
                    AdvantagePct = response.HeldAdvantageGrossPct,
                    NetArrival = heldPath?.NetAmount,
                    HeldCostRate = response.Held?.ImpliedRate,
                    BreakevenRate = response.BreakevenHeldRate,
                };
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // Not computable this run; the evaluator says so as a note.
            }
        }

        return FxAlertRuleEvaluator.Evaluate(
            rules,
            rule => rates.TryGetValue(FxAlertRule.Normalize(rule.Pair), out var quote)
                ? quote
                : null,
            rule => facts.TryGetValue(AdvantageKey(rule), out var value) ? value : null);
    }

    private static string AdvantageKey(FxAlertRule rule) =>
        string.Join('/',
            FxAlertRule.Normalize(rule.BaseCurrency),
            FxAlertRule.Normalize(rule.TargetCurrency),
            FxAlertRule.Normalize(rule.HeldCurrency),
            // The cost model is part of the identity of the computed facts: two rules with
            // different fees must never share a cached FxPathFacts.
            rule.SpreadPct?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            rule.FixedFee?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "-");

    /// <summary>
    /// The held pot from the ledger, and what the batches say the still-held part cost:
    /// FIFO, the same cost the page shows (WP-02 R3/D8 — a moving average here would quietly
    /// disagree with the batch list). Null cost means "not fully known in the base currency"
    /// (money without a receipt, or a third-currency chain) and must NOT drop the pot: the
    /// paths that do not need a cost are still answerable (R5).
    /// </summary>
    internal static (decimal amount, decimal? cost)? HeldPositionOf(
        PortfolioStore store, string baseCurrency, string heldCurrency)
    {
        var transactions = store.ListTransactions();
        var balances = PositionCalculator.Replay(transactions).CashBalances;
        var amount = balances
            .Where(b => string.Equals(b.Currency, heldCurrency, StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Amount);
        if (amount <= 0m)
        {
            return null;
        }

        var open = FxLotLedger.FromTransactions(transactions).Lots
            .Where(lot => lot.IsOpen
                          && string.Equals(lot.Currency, heldCurrency, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var covered = open.Sum(lot => lot.RemainingAmount);
        var pricedInBase = open.Length > 0
                           && open.All(lot => string.Equals(
                               lot.CostCurrency, baseCurrency, StringComparison.OrdinalIgnoreCase));
        // The whole pot must be explained by batches priced in the base currency: a partly
        // uncovered pot has an unknown cost, and half a cost is worse than none.
        decimal? cost = pricedInBase && covered >= amount - 0.005m
            ? open.Sum(lot => lot.RemainingCostAmount)
            : null;
        return (amount, cost);
    }

    private static async Task<AnalyticsCoreProcessBroker> StartCoreAsync(
        string coreRoot,
        string stateRoot,
        MarketCredentialsStore? credentialsStore,
        int port,
        CancellationToken cancellationToken)
    {
        var provisioner = new PythonRuntimeProvisioner(new PythonRuntimeOptions
        {
            StateRoot = stateRoot,
            PythonProjectDirectory = Path.Combine(coreRoot, "python"),
        });
        var runtime = await provisioner.ProvisionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!runtime.CanRun)
        {
            throw new InvalidOperationException($"Python 运行时就绪失败：{runtime.Summary}");
        }

        var broker = new AnalyticsCoreProcessBroker(port: port);
        broker.Start(runtime.Interpreter!, coreRoot, credentialsStore);
        return broker;
    }

    private static async Task<bool> TryHealthAsync(
        int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            // ForLoopback owns its HttpClient, so disposing the probe is clean.
            using var client = AnalyticsCoreClient.ForLoopback(port);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitForHealthAsync(
        AnalyticsCoreClient client, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CoreStartupTimeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"分析核心在 {CoreStartupTimeout.TotalSeconds:0} 秒内未就绪，价格检查未执行。");
    }
}
