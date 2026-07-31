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
    public required IReadOnlyList<string> Warnings { get; init; }

    public bool IsEmpty => Transactions.Count == 0;
}

/// <summary>
/// UI-agnostic facade over the ledger: refresh, record, import, export.
///
/// Lives in the Host rather than the desktop layer so the wealth workflow is unit-testable
/// without a window, and so the authority boundary keeps state on the Host side.
/// </summary>
public sealed class PortfolioWorkspace(
    PortfolioStore store,
    IMarketPricingSource? pricingSource = null,
    string baseCurrency = Currencies.Cny)
{
    public string BaseCurrency { get; } = Currencies.Normalize(baseCurrency);

    public PortfolioStore Store => store;

    /// <summary>
    /// Where prices come from. Settable because the desktop opens the ledger before the
    /// Analytics Core is up — until then the ledger still works, holdings just stay unpriced.
    /// </summary>
    public IMarketPricingSource? PricingSource { get; set; } = pricingSource;

    /// <summary>Values the ledger as of <paramref name="asOf"/>, fetching prices when a source exists.</summary>
    public async Task<WorkspaceSnapshot> RefreshAsync(
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var date = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var transactions = store.ListTransactions();
        var state = PositionCalculator.Replay(transactions);
        var accounts = store.ListAccounts(includeArchived: true);
        var instruments = store.ListInstruments();

        var warnings = new List<string>();
        IReadOnlyDictionary<string, PriceQuote> quotes;
        FxConverter fx;

        var source = PricingSource;
        if (source is null)
        {
            // Offline: value with whatever rates were snapshotted; every holding stays unpriced.
            quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
            fx = store.CreateFxConverter(date);
        }
        else
        {
            var pricing = await new PortfolioPricingService(source, store)
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
            Warnings = warnings.Concat(valuation.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    public Account AddAccount(string name, AccountKind kind, string mainCurrency, string broker = "") =>
        store.SaveAccount(Account.Create(name, kind, mainCurrency, broker));

    /// <summary>
    /// Records a transaction and remembers the instrument, so allocation grouping has metadata
    /// without a separate "add instrument" step the user would have to remember.
    /// </summary>
    public LedgerTransaction Record(LedgerTransaction transaction, string? instrumentName = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        store.AddTransaction(transaction);
        EnsureInstrument(transaction.Symbol, instrumentName, transaction.Currency);
        return transaction;
    }

    public CsvImportPreview PreviewImport(string csvText, string defaultAccountId) =>
        TransactionCsvImporter.Preview(
            csvText,
            defaultAccountId,
            store,
            BaseCurrency,
            store.ListAccounts(includeArchived: true).ToDictionary(a => a.Name, a => a.Id, StringComparer.Ordinal));

    public int CommitImport(CsvImportPreview preview, bool skipInvalidRows = false)
    {
        var written = TransactionCsvImporter.Commit(preview, store, skipInvalidRows);
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
        LegacyFillImporter.Import(plans, store, accountId, BaseCurrency);

    private void EnsureInstrument(string symbol, string? name, string currency)
    {
        if (string.IsNullOrEmpty(symbol) || store.GetInstrument(symbol) is not null)
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

        store.SaveInstrument(Instrument.FromSymbol(
            parsed.Value,
            string.IsNullOrWhiteSpace(name) ? parsed.Code : name!,
            assetClass,
            currency));
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
