using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>Everything the wealth pages render after one refresh.</summary>
public sealed record WorkspaceSnapshot
{
    public required DateOnly AsOf { get; init; }
    public required string BaseCurrency { get; init; }
    public required PortfolioValuation Valuation { get; init; }
    public required IReadOnlyList<Account> Accounts { get; init; }
    public required IReadOnlyList<Instrument> Instruments { get; init; }
    public required IReadOnlyList<LedgerTransaction> Transactions { get; init; }
    public required IReadOnlyList<CashBalance> CashBalances { get; init; }
    /// <summary>Money-weighted return, or null when the flows cannot support one.</summary>
    public double? Xirr { get; init; }

    /// <summary>
    /// Why <see cref="Xirr"/> is what it is. When a flow's own-day exchange rate is missing this
    /// says so and names the date, instead of the number silently disappearing (WP-01 R5).
    /// </summary>
    public required ReturnAssessment XirrStatus { get; init; }

    /// <summary>Per-account totals and returns (WP-01 目标 3).</summary>
    public required IReadOnlyList<AccountSummary> AccountSummaries { get; init; }

    /// <summary>Linked time-weighted return, or null with fewer than two valuation points.</summary>
    public double? Twr { get; init; }
    public required PortfolioRiskReport Risk { get; init; }
    public required IReadOnlyList<PortfolioAlert> Alerts { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public bool IsEmpty => Transactions.Count == 0;
}

/// <summary>
/// UI-agnostic facade over the ledger: refresh, record, import, export.
///
/// Lives in the Host rather than the desktop layer so the wealth workflow is unit-testable
/// without a window, and so the authority boundary keeps state on the Host side.
/// </summary>
public sealed class PortfolioWorkspace
{
    private readonly PortfolioStore _store;

    public PortfolioWorkspace(
        PortfolioStore store,
        IMarketPricingSource? pricingSource = null,
        string? baseCurrency = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        PricingSource = pricingSource;

        // Stored preferences win; an explicit currency argument is an override for tests and tools.
        Settings = _store.LoadSettings();
        if (!string.IsNullOrWhiteSpace(baseCurrency))
        {
            Settings = Settings with { BaseCurrency = Currencies.Normalize(baseCurrency!) };
        }
    }

    /// <summary>User preferences: base currency, concentration ceilings, target allocation.</summary>
    public PortfolioSettings Settings { get; private set; }

    public string BaseCurrency => Settings.BaseCurrency;

    public PortfolioStore Store => _store;

    /// <summary>
    /// Where prices come from. Settable because the desktop opens the ledger before the
    /// Analytics Core is up — until then the ledger still works, holdings just stay unpriced.
    /// </summary>
    public IMarketPricingSource? PricingSource { get; set; }

    /// <summary>Planned buy/sell levels from the research side, used to raise price alerts.</summary>
    public MarketData.PricePlanStore? PlanStore { get; set; }

    /// <summary>Validates, persists, and adopts new preferences. Throws rather than storing junk.</summary>
    public PortfolioSettings ApplySettings(PortfolioSettings settings)
    {
        Settings = _store.SaveSettings(settings);
        return Settings;
    }

    /// <summary>Values the ledger as of <paramref name="asOf"/>, fetching prices when a source exists.</summary>
    public async Task<WorkspaceSnapshot> RefreshAsync(
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var date = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var transactions = _store.ListTransactions();
        var state = PositionCalculator.Replay(transactions);
        var accounts = _store.ListAccounts(includeArchived: true);
        var instruments = _store.ListInstruments();

        var warnings = new List<string>();
        IReadOnlyDictionary<string, PriceQuote> quotes;
        FxConverter fx;

        var source = PricingSource;
        if (source is AnalyticsCorePricingSource coreSource)
        {
            coreSource.CrossCheck = Settings.CrossCheckPrices;
            coreSource.TolerancePercent = Settings.PriceTolerancePercent;
        }

        if (source is null)
        {
            // Offline: value with whatever rates were snapshotted; every holding stays unpriced.
            quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
            fx = _store.CreateFxConverter(date);
        }
        else
        {
            var pricing = await new PortfolioPricingService(source, _store)
                .FetchAsync(state, BaseCurrency, date, cancellationToken)
                .ConfigureAwait(false);
            quotes = pricing.Quotes;
            fx = pricing.Fx;
            warnings.AddRange(pricing.Warnings);
        }

        var valuation = ValuationEngine.Value(
            state,
            quotes,
            fx,
            BaseCurrency,
            date,
            instruments.ToDictionary(i => i.Symbol, StringComparer.Ordinal),
            accounts.ToDictionary(a => a.Id, StringComparer.Ordinal));

        // Record the point before analysing, so today's value is part of the curve it reads.
        if (transactions.Count > 0)
        {
            _store.SaveValuationSnapshot(valuation);
        }

        var risk = PortfolioRiskAnalyzer.Analyze(
            valuation,
            Settings.Thresholds,
            _store.ListValuationHistory(BaseCurrency),
            Settings.TargetAssetAllocation,
            Settings.EmergencyFundAmount);

        var alerts = PortfolioAlertEvaluator.Evaluate(
            valuation,
            PlannedLevels(),
            risk,
            asOf: date,
            priceTolerancePercent: Settings.PriceTolerancePercent,
            ipoSubscriptions: _store.ListIpoSubscriptions());

        return new WorkspaceSnapshot
        {
            AsOf = date,
            BaseCurrency = BaseCurrency,
            Valuation = valuation,
            Accounts = accounts,
            Instruments = instruments,
            Transactions = transactions,
            CashBalances = state.CashBalances,
            Xirr = TryXirr(state, valuation, date),
            XirrStatus = AssessXirr(state, valuation, date),
            AccountSummaries = SummarizeAccounts(state, valuation, date, accounts),
            Twr = TryTwr(state, valuation, date),
            Risk = risk,
            Alerts = alerts,
            Warnings = warnings.Concat(valuation.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    public Account AddAccount(
        string name,
        AccountKind kind,
        string mainCurrency,
        string broker = "",
        AccountValuationMode mode = AccountValuationMode.Detail) =>
        _store.SaveAccount(Account.Create(name, kind, mainCurrency, broker, mode: mode));

    /// <summary>
    /// Records a transaction and remembers the instrument, so allocation grouping has metadata
    /// without a separate "add instrument" step the user would have to remember.
    /// </summary>
    public LedgerTransaction Record(LedgerTransaction transaction, string? instrumentName = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _store.AddTransaction(transaction);
        EnsureInstrument(transaction.Symbol, instrumentName, transaction.Currency);
        return transaction;
    }

    public CsvImportPreview PreviewImport(string csvText, string defaultAccountId) =>
        TransactionCsvImporter.Preview(
            csvText,
            defaultAccountId,
            _store,
            BaseCurrency,
            _store.ListAccounts(includeArchived: true).ToDictionary(a => a.Name, a => a.Id, StringComparer.Ordinal));

    /// <summary>
    /// Cancels a recorded transaction by pairing it with a reversal row. The original stays — an
    /// append-only ledger is corrected by an opposite entry, never by removal — and replay skips
    /// the pair, so cash, positions, and flows return to their pre-mistake state exactly.
    /// </summary>
    public LedgerTransaction ReverseTransaction(string transactionId, string reason = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        var original = _store.ListTransactions().FirstOrDefault(t => t.Id == transactionId)
                       ?? throw new LedgerException($"流水 {transactionId} 不存在。");
        var reversal = LedgerTransaction.Reversal(original, reason);
        _store.AddTransaction(reversal);
        return reversal;
    }

    public int CommitImport(CsvImportPreview preview, bool skipInvalidRows = false)
    {
        var written = TransactionCsvImporter.Commit(preview, _store, skipInvalidRows);
        foreach (var symbol in preview.Transactions
                     .Where(t => !string.IsNullOrEmpty(t.Symbol))
                     .Select(t => (t.Symbol, t.Currency))
                     .Distinct())
        {
            EnsureInstrument(symbol.Symbol, null, symbol.Currency);
        }

        return written;
    }

    public string ExportPositionsCsv(WorkspaceSnapshot snapshot) =>
        PortfolioReportExporter.PositionsCsv(
            snapshot.Valuation,
            snapshot.Instruments.ToDictionary(i => i.Symbol, StringComparer.Ordinal),
            snapshot.Accounts.ToDictionary(a => a.Id, StringComparer.Ordinal));

    public string ExportAllocationCsv(WorkspaceSnapshot snapshot) =>
        PortfolioReportExporter.AllocationCsv(snapshot.Valuation);

    public string ExportTransactionsCsv(WorkspaceSnapshot snapshot) =>
        PortfolioReportExporter.TransactionsCsv(
            snapshot.Transactions,
            snapshot.Accounts.ToDictionary(a => a.Id, StringComparer.Ordinal),
            snapshot.Instruments.ToDictionary(i => i.Symbol, StringComparer.Ordinal));

    /// <summary>Pulls the pre-ledger fill journal in; safe to call repeatedly.</summary>
    public LegacyImportResult ImportLegacyFills(MarketData.PricePlanStore plans, string accountId) =>
        LegacyFillImporter.Import(plans, _store, accountId, BaseCurrency);

    private void EnsureInstrument(string symbol, string? name, string currency)
    {
        if (string.IsNullOrEmpty(symbol) || _store.GetInstrument(symbol) is not null)
        {
            return;
        }

        if (!SymbolId.TryParse(symbol, out var parsed))
        {
            return;
        }

        parsed = parsed.Normalized();
        var assetClass = ExchangeRegistry.TryGet(parsed.Exchange, out var venue)
            ? venue.DefaultAssetClass
            : AssetClass.Equity;

        _store.SaveInstrument(Instrument.FromSymbol(
            parsed.Value,
            string.IsNullOrWhiteSpace(name) ? parsed.Code : name!,
            assetClass,
            currency));
    }

    private IReadOnlyList<MarketData.PlannedPriceLevel> PlannedLevels()
    {
        if (PlanStore is null)
        {
            return [];
        }

        try
        {
            return PlanStore.Load().Levels.Where(level => level.Active).ToArray();
        }
        catch (IOException)
        {
            // A missing or unreadable plan file must not block a valuation.
            return [];
        }
    }

    private double? TryXirr(LedgerState state, PortfolioValuation valuation, DateOnly asOf)
    {
        // A mixed-currency flow series cannot be discounted; skip rather than report a wrong rate.
        if (state.ExternalFlows.Any(f => f.Amount.Currency != BaseCurrency))
        {
            return null;
        }

        try
        {
            return ReturnMetrics.Xirr(state.ExternalFlows, valuation.TotalValue, asOf);
        }
        catch (LedgerException)
        {
            return null;
        }
    }

    /// <summary>
    /// WP-01 R5: external flows are discounted at the exchange rate of the day they happened, so
    /// a portfolio holding more than one currency still has a return. A flow whose own-day rate
    /// is missing makes the result explicitly incomplete (the date is named) — it is never
    /// converted at today's rate and never silently dropped.
    /// </summary>
    private ReturnAssessment AssessXirr(LedgerState state, PortfolioValuation valuation, DateOnly asOf) =>
        ReturnAssessment.Missing("多币种现金流按发生日折算尚未实现（WP-01 P3）。", []);

    /// <summary>
    /// WP-01 目标 3: per-account 总资产 / 累计收益 / 资金加权收益率 / 年化. A balance-mode account
    /// without a reported total is unknown, not zero (D3).
    /// </summary>
    private IReadOnlyList<AccountSummary> SummarizeAccounts(
        LedgerState state, PortfolioValuation valuation, DateOnly asOf, IReadOnlyList<Account> accounts)
    {
        var values = valuation.AccountValues.ToDictionary(v => v.AccountId, StringComparer.Ordinal);
        var summaries = new List<AccountSummary>(accounts.Count);
        foreach (var account in accounts)
        {
            values.TryGetValue(account.Id, out var value);
            var known = value?.ValueKnown == true;
            summaries.Add(new AccountSummary
            {
                AccountId = account.Id,
                Name = account.Name,
                Currency = account.MainCurrency,
                Mode = account.Mode,
                ValueKnown = known,
                TotalValueBase = known ? value!.TotalValue : null,
                NetInvestedBase = null,
                MoneyWeightedReturn = ReturnAssessment.Missing(
                    "账户级净投入与收益率折算尚未实现（WP-01 P3）。", []),
            });
        }

        return summaries;
    }

    /// <summary>
    /// Time-weighted return from the stored valuation curve. Needs at least two points, which
    /// means it is null on the very first refresh — the overview shows "—" until a second
    /// day of history exists, which is more honest than a number built from one point.
    ///
    /// A snapshot at date D is a full replay, so it already embeds flows dated D (and all
    /// earlier ones). Those must not be counted again as inflow — doing so double-counts the
    /// opening cash and turns a flat ledger into a -50% "return". Only flows strictly between
    /// two snapshot dates are genuine interim flows.
    /// </summary>
    private double? TryTwr(LedgerState state, PortfolioValuation valuation, DateOnly asOf)
    {
        if (state.ExternalFlows.Any(f => f.Amount.Currency != BaseCurrency))
        {
            return null;
        }

        try
        {
            var history = _store.ListValuationHistory(BaseCurrency);
            var points = history.Count > 0 && history[^1].Date != asOf
                ? [.. history, new ValuationPoint(asOf, valuation.TotalValue.Amount)]
                : history;
            if (points.Count == 0)
            {
                return null;
            }

            // Snapshot semantics: each point already contains that day's flows. Interior
            // flows (after the first point, on/before the last) all belong in the chain —
            // TimeWeighted places them by its end-of-day convention (P1-03).
            var flows = state.ExternalFlows
                .Where(f => f.Date > points[0].Date && f.Date <= points[^1].Date)
                .Select(f => new DatedAmount(f.Date, f.Amount.Amount))
                .ToArray();
            return ReturnMetrics.TimeWeighted(points, flows);
        }
        catch (LedgerException)
        {
            return null;
        }
    }
}
