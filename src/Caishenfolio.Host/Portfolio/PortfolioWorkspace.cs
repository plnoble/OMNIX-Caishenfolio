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

    /// <summary>
    /// 累计收益 = Σ 账户（总资产 − 净投入）— the ONE definition this product uses (WP-02 裁决：
    /// 总览与账户页必须同口径同词；两处显示不同数字是 bug 而不是设计). Null when any account's
    /// figure is unknown, because a partial sum would be a confident wrong number.
    /// </summary>
    public Money? CumulativePnlBase { get; init; }

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

        // WP-03 D2: a ledger written before the balance rule can already be negative. It opens
        // as usual — nothing is rewritten — and the shortfall is named here so the user sees it
        // instead of reading a silent zero on the overview.
        //
        // Codex review #6: the words state HISTORY and nothing about the present. The stored amount
        // is the DEEPEST point reached and the date is when the gap began; the account may well be
        // positive again by now (a later deposit closes the gap), and a balance that went negative
        // through an allowed path (e.g. financing) is not necessarily a pre-upgrade leftover — so
        // the text does not claim a current balance, and does not guess the cause.
        foreach (var shortfall in state.HistoricalShortfalls)
        {
            var account = accounts.FirstOrDefault(a => a.Id == shortfall.AccountId);
            var name = account?.Name ?? shortfall.AccountId;
            warnings.Add(
                $"账户 {name} 的 {shortfall.Currency} 现金出现过历史负余额：" +
                $"自 {shortfall.FirstDate:yyyy-MM-dd} 起变负，最低到 {shortfall.Amount:#,0.##}（{shortfall.Currency}）。" +
                "账本照常可用，已发生的记录不会被改写；新的换汇不会再允许花超。");
        }

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

        var returnWarnings = new List<string>();
        // Rates observed on any day, so each flow can be folded in at its own day (R5). The
        // valuation itself keeps using the as-of converter above.
        var history = new HistoricalFxConverter(_store.ListFxRates());
        var xirrStatus = AssessXirr(state, valuation, date, history, returnWarnings);
        var twr = TryTwr(state, valuation, date, history, returnWarnings);

        var summaries = SummarizeAccounts(state, valuation, date, accounts, history);

        return new WorkspaceSnapshot
        {
            AsOf = date,
            BaseCurrency = BaseCurrency,
            Valuation = valuation,
            Accounts = accounts,
            Instruments = instruments,
            Transactions = transactions,
            CashBalances = state.CashBalances,
            // One number, one source of truth: the metric and its explanation never disagree.
            Xirr = xirrStatus.Value,
            XirrStatus = xirrStatus,
            AccountSummaries = summaries,
            CumulativePnlBase = SumCumulativePnl(summaries),
            Twr = twr,
            Risk = risk,
            Alerts = alerts,
            Warnings = warnings
                .Concat(valuation.Warnings)
                .Concat(returnWarnings)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
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

    /// <summary>
    /// WP-01 R5: external flows are discounted at the exchange rate of the day they happened, so
    /// a portfolio holding more than one currency still has a return. A flow whose own-day rate
    /// is missing makes the result explicitly incomplete (the date is named) — it is never
    /// converted at today's rate and never silently dropped.
    /// </summary>
    private ReturnAssessment AssessXirr(
        LedgerState state,
        PortfolioValuation valuation,
        DateOnly asOf,
        HistoricalFxConverter history,
        List<string> warnings)
    {
        var (flows, missing) = ConvertFlows(state.ExternalFlows, history);
        if (missing.Count > 0)
        {
            foreach (var gap in missing)
            {
                warnings.Add($"外部流 {gap}，该笔未计入资金加权收益率。");
            }

            return ReturnAssessment.Missing(
                $"有 {missing.Count} 笔外部流缺少发生当日的汇率，资金加权收益率无法计算。" +
                "用今天的汇率代替会得到一个看着合理但错的数，所以这里不猜。",
                missing);
        }

        try
        {
            return ReturnAssessment.Resolved(ReturnMetrics.Xirr(flows, valuation.TotalValue, asOf));
        }
        catch (LedgerException ex)
        {
            return ReturnAssessment.Missing($"现金流仍无法折算到 {BaseCurrency}：{ex.Message}", missing);
        }
    }

    /// <summary>
    /// Folds each flow into the base currency at the rate observed on the flow's own day. A flow
    /// that cannot be converted is reported with its date instead of being dropped or approximated
    /// (WP-01 R5) — dropping it would quietly change the answer.
    /// </summary>
    private (List<ExternalFlow> Flows, List<string> Missing) ConvertFlows(
        IEnumerable<ExternalFlow> flows, HistoricalFxConverter history)
    {
        var converted = new List<ExternalFlow>();
        var missing = new List<string>();
        foreach (var flow in flows)
        {
            if (history.TryConvert(flow.Amount, BaseCurrency, flow.Date, out var conversion))
            {
                converted.Add(flow with { Amount = conversion });
                continue;
            }

            missing.Add($"{flow.Date:yyyy-MM-dd} {flow.Amount.Currency}→{BaseCurrency} 缺少当日汇率");
        }

        return (converted, missing);
    }

    /// <summary>
    /// WP-01 目标 3: per-account 总资产 / 累计收益 / 资金加权收益率 / 年化. A balance-mode account
    /// without a reported total is unknown, not zero (D3).
    /// </summary>
    /// <summary>
    /// Σ of the per-account 累计收益, or null when any single account's figure is unknown: a sum
    /// that silently drops the unknown account would be a smaller number pretending to be total.
    /// </summary>
    private Money? SumCumulativePnl(IReadOnlyList<AccountSummary> summaries)
    {
        if (summaries.Count == 0)
        {
            return Money.Zero(BaseCurrency);
        }

        var total = Money.Zero(BaseCurrency);
        foreach (var summary in summaries)
        {
            if (summary.CumulativePnlBase is not { } pnl)
            {
                return null;
            }

            total += pnl;
        }

        return total;
    }

    private IReadOnlyList<AccountSummary> SummarizeAccounts(
        LedgerState state,
        PortfolioValuation valuation,
        DateOnly asOf,
        IReadOnlyList<Account> accounts,
        HistoricalFxConverter history)
    {
        var values = valuation.AccountValues.ToDictionary(v => v.AccountId, StringComparer.Ordinal);
        var summaries = new List<AccountSummary>(accounts.Count);
        foreach (var account in accounts)
        {
            values.TryGetValue(account.Id, out var value);
            var known = value?.ValueKnown == true;
            var totalValue = known ? value!.TotalValue : (Money?)null;

            var accountFlows = state.FlowsForAccount(account.Id);
            var (flows, missing) = ConvertFlows(accountFlows, history);
            var accountWarnings = new List<string>(missing);
            // One flow without its own day's rate is enough to make the sum meaningless: leaving the
            // others in would understate 净投入 and turn the unconverted money into invented profit.
            // The contract says null (未知) and the UI already renders null as 未知, so hand it null.
            var netInvested = missing.Count > 0
                ? (Money?)null
                : Money.Sum(flows.Select(f => f.Amount), BaseCurrency);
            var firstFlowDate = flows.Count == 0 ? (DateOnly?)null : flows.Min(f => f.Date);

            // A balance-mode account is worth exactly what was last reported. Money moved after that
            // report never reaches the reported number, so the account (and the portfolio total) is
            // quietly off by the amount moved until the user reports again. Same reasoning as D3:
            // say it out loud rather than present a number that looks authoritative.
            if (account.Mode == AccountValuationMode.Balance
                && state.BalanceAssertions.TryGetValue(account.Id, out var assertion))
            {
                var afterReport = accountFlows.Where(f => f.Date > assertion.Date).ToList();
                if (afterReport.Count > 0)
                {
                    accountWarnings.Add(
                        $"账户「{account.Name}」最后上报总额是 {assertion.Date:yyyy-MM-dd}，" +
                        $"之后还有 {afterReport.Count} 笔资金进出（最近 {afterReport.Max(f => f.Date):yyyy-MM-dd}），" +
                        "报告里的金额没有跟着动，可能偏低或偏高；请补报当天资产金额。");
                }
            }

            if (accountFlows.Count > 0 && account.Mode == AccountValuationMode.Balance
                && !state.BalanceAssertions.ContainsKey(account.Id))
            {
                accountWarnings.Add(
                    $"账户「{account.Name}」是余额模式且已有 {accountFlows.Count} 笔资金进出，但从未报过当天资产金额，" +
                    "市值为未知（不是 0）。");
            }

            summaries.Add(new AccountSummary
            {
                AccountId = account.Id,
                Name = account.Name,
                Currency = account.MainCurrency,
                Mode = account.Mode,
                ValueKnown = known,
                TotalValueBase = totalValue,
                NetInvestedBase = netInvested,
                MoneyWeightedReturn = AssessAccountReturn(account, totalValue, flows, missing, asOf),
                Annualized = totalValue is { } total && netInvested is { } invested
                    ? AnnualizedReturn(total - invested, invested, asOf, firstFlowDate)
                    : null,
                Warnings = accountWarnings,
            });
        }

        return summaries;
    }

    private ReturnAssessment AssessAccountReturn(
        Account account,
        Money? totalValue,
        IReadOnlyList<ExternalFlow> flows,
        IReadOnlyList<string> missing,
        DateOnly asOf)
    {
        if (missing.Count > 0)
        {
            return ReturnAssessment.Missing(
                $"账户「{account.Name}」有 {missing.Count} 笔资金进出缺少发生当日的汇率，收益率无法计算。", missing);
        }

        if (totalValue is not { } value)
        {
            return ReturnAssessment.Missing(
                $"账户「{account.Name}」还没有可用的市值（余额模式未报过总额，或持仓未定价），收益率未知。", []);
        }

        try
        {
            return ReturnAssessment.Resolved(ReturnMetrics.Xirr(flows, value, asOf));
        }
        catch (LedgerException ex)
        {
            return ReturnAssessment.Missing($"账户「{account.Name}」现金流无法折算：{ex.Message}", []);
        }
    }

    /// <summary>
    /// 年化: the cumulative return (累计收益 ÷ 净投入) compounded over the account's holding span.
    /// The money-weighted rate is already an annual figure, so this is the other, simpler read.
    /// </summary>
    private static double? AnnualizedReturn(
        Money pnl, Money netInvested, DateOnly asOf, DateOnly? firstFlowDate)
    {
        if (netInvested.Amount == 0m || firstFlowDate is not { } start || start >= asOf)
        {
            return null;
        }

        var cumulative = (double)(pnl.Amount / Math.Abs(netInvested.Amount));
        return ReturnMetrics.Annualize(cumulative, asOf.DayNumber - start.DayNumber);
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
    private double? TryTwr(
        LedgerState state,
        PortfolioValuation valuation,
        DateOnly asOf,
        HistoricalFxConverter history,
        List<string> warnings)
    {
        // The chain of daily values is denominated in the base currency, so interim flows have to
        // be folded in at their own day's rate for the same reason XIRR does (R5). A gap makes the
        // chain incomparable; the caller reports which date is missing.
        var (flowsConverted, missing) = ConvertFlows(state.ExternalFlows, history);
        if (missing.Count > 0)
        {
            foreach (var gap in missing)
            {
                warnings.Add($"外部流 {gap}，时间加权收益率无法计算。");
            }

            return null;
        }

        try
        {
            var history_ = _store.ListValuationHistory(BaseCurrency);
            var points = history_.Count > 0 && history_[^1].Date != asOf
                ? [.. history_, new ValuationPoint(asOf, valuation.TotalValue.Amount)]
                : history_;
            if (points.Count == 0)
            {
                return null;
            }

            // Snapshot semantics: each point already contains that day's flows. Interior
            // flows (after the first point, on/before the last) all belong in the chain —
            // TimeWeighted places them by its end-of-day convention (P1-03).
            var flows = flowsConverted
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
