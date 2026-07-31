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
            Settings.TargetAssetAllocation);

        var alerts = PortfolioAlertEvaluator.Evaluate(
            valuation,
            PlannedLevels(),
            risk,
            asOf: date,
            priceTolerancePercent: Settings.PriceTolerancePercent);

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
            Risk = risk,
            Alerts = alerts,
            Warnings = warnings.Concat(valuation.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    public Account AddAccount(string name, AccountKind kind, string mainCurrency, string broker = "") =>
        _store.SaveAccount(Account.Create(name, kind, mainCurrency, broker));

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
}
