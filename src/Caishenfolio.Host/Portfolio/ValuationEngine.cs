using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>A price observation for one instrument. NAV-priced funds use the same shape.</summary>
public sealed record PriceQuote
{
    public required string Symbol { get; init; }
    public required decimal Price { get; init; }
    public required string Currency { get; init; }
    public required DateOnly AsOf { get; init; }
    public string Provider { get; init; } = "";

    public static PriceQuote Of(string symbol, decimal price, string currency, DateOnly asOf, string provider = "")
    {
        if (price < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "价格不能为负数。");
        }

        return new PriceQuote
        {
            Symbol = SymbolId.Parse(symbol).Normalized().Value,
            Price = price,
            Currency = Currencies.Normalize(currency),
            AsOf = asOf,
            Provider = provider,
        };
    }
}

/// <summary>One position valued at a point in time.</summary>
public sealed record PositionValuation
{
    public required Position Position { get; init; }
    public PriceQuote? Quote { get; init; }
    /// <summary>False when no usable price or rate was available — such a holding is excluded from totals.</summary>
    public required bool Priced { get; init; }
    public Money? MarketValue { get; init; }
    public Money? MarketValueBase { get; init; }
    public required Money CostBasisBase { get; init; }
    public Money? UnrealizedPnl { get; init; }
    public Money? UnrealizedPnlBase { get; init; }
    public required Money RealizedPnlBase { get; init; }
    public required Money DividendsBase { get; init; }
    /// <summary>Share of the priced portfolio; null when unpriced.</summary>
    public decimal? Weight { get; init; }
}

/// <summary>A slice of the portfolio grouped by one dimension.</summary>
public sealed record AllocationSlice
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Money Value { get; init; }
    public required decimal Weight { get; init; }
}

public sealed record PortfolioValuation
{
    public required string BaseCurrency { get; init; }
    public required DateOnly AsOf { get; init; }
    public required IReadOnlyList<PositionValuation> Positions { get; init; }
    public required Money HoldingsValue { get; init; }
    public required Money CashValue { get; init; }
    public required Money TotalValue { get; init; }
    public required Money CostBasis { get; init; }
    public required Money UnrealizedPnl { get; init; }
    public required Money RealizedPnl { get; init; }
    public required Money Dividends { get; init; }
    public required IReadOnlyList<AllocationSlice> ByAssetClass { get; init; }
    public required IReadOnlyList<AllocationSlice> ByRegion { get; init; }
    public required IReadOnlyList<AllocationSlice> ByCurrency { get; init; }
    public required IReadOnlyList<AllocationSlice> ByAccount { get; init; }
    /// <summary>Missing prices and rates. Non-empty means the totals cover only part of the portfolio.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Total gain including realized results and income, against money actually put in.</summary>
    public Money TotalPnl => UnrealizedPnl + RealizedPnl + Dividends;

    public bool IsComplete => Warnings.Count == 0;
}

/// <summary>
/// Values a replayed ledger in one base currency.
///
/// Unpriced holdings are never treated as zero: they are flagged and left out of the totals,
/// so a provider outage shows up as an incomplete valuation instead of a portfolio that
/// silently shrank.
/// </summary>
public static class ValuationEngine
{
    public static PortfolioValuation Value(
        LedgerState state,
        IReadOnlyDictionary<string, PriceQuote> quotes,
        FxConverter fx,
        string baseCurrency,
        DateOnly asOf,
        IReadOnlyDictionary<string, Instrument>? instruments = null,
        IReadOnlyDictionary<string, Account>? accounts = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(fx);

        var baseCcy = Currencies.Normalize(baseCurrency);
        var warnings = new List<string>();
        var valuations = new List<PositionValuation>();

        var holdingsValue = Money.Zero(baseCcy);
        var costBasis = Money.Zero(baseCcy);
        var unrealized = Money.Zero(baseCcy);
        var realized = Money.Zero(baseCcy);
        var dividends = Money.Zero(baseCcy);

        foreach (var position in state.Positions)
        {
            var costBase = ConvertOrWarn(fx, position.CostBasis, baseCcy, position.Symbol, warnings);
            var realizedBase = ConvertOrWarn(fx, position.RealizedPnl, baseCcy, position.Symbol, warnings);
            var dividendsBase = ConvertOrWarn(fx, position.Dividends, baseCcy, position.Symbol, warnings);

            realized += realizedBase ?? Money.Zero(baseCcy);
            dividends += dividendsBase ?? Money.Zero(baseCcy);

            if (!position.IsOpen)
            {
                valuations.Add(new PositionValuation
                {
                    Position = position,
                    Priced = true,
                    CostBasisBase = costBase ?? Money.Zero(baseCcy),
                    RealizedPnlBase = realizedBase ?? Money.Zero(baseCcy),
                    DividendsBase = dividendsBase ?? Money.Zero(baseCcy),
                });
                continue;
            }

            costBasis += costBase ?? Money.Zero(baseCcy);

            if (!quotes.TryGetValue(position.Symbol, out var quote))
            {
                warnings.Add($"{position.Symbol} 缺少最新价格，未计入总市值。");
                valuations.Add(new PositionValuation
                {
                    Position = position,
                    Priced = false,
                    CostBasisBase = costBase ?? Money.Zero(baseCcy),
                    RealizedPnlBase = realizedBase ?? Money.Zero(baseCcy),
                    DividendsBase = dividendsBase ?? Money.Zero(baseCcy),
                });
                continue;
            }

            var marketValue = Money.Of(position.Quantity * quote.Price, quote.Currency);
            if (!fx.TryConvert(marketValue, baseCcy, out var marketValueBase))
            {
                warnings.Add($"{position.Symbol} 缺少 {quote.Currency} → {baseCcy} 的汇率，未计入总市值。");
                valuations.Add(new PositionValuation
                {
                    Position = position,
                    Quote = quote,
                    Priced = false,
                    MarketValue = marketValue,
                    CostBasisBase = costBase ?? Money.Zero(baseCcy),
                    RealizedPnlBase = realizedBase ?? Money.Zero(baseCcy),
                    DividendsBase = dividendsBase ?? Money.Zero(baseCcy),
                });
                continue;
            }

            var unrealizedLocal = string.Equals(quote.Currency, position.Currency, StringComparison.Ordinal)
                ? marketValue - position.CostBasis
                : (Money?)null;
            var unrealizedBase = marketValueBase - (costBase ?? Money.Zero(baseCcy));

            holdingsValue += marketValueBase;
            unrealized += unrealizedBase;

            valuations.Add(new PositionValuation
            {
                Position = position,
                Quote = quote,
                Priced = true,
                MarketValue = marketValue,
                MarketValueBase = marketValueBase,
                CostBasisBase = costBase ?? Money.Zero(baseCcy),
                UnrealizedPnl = unrealizedLocal,
                UnrealizedPnlBase = unrealizedBase,
                RealizedPnlBase = realizedBase ?? Money.Zero(baseCcy),
                DividendsBase = dividendsBase ?? Money.Zero(baseCcy),
            });
        }

        var cashValue = Money.Zero(baseCcy);
        var cashByCurrency = new Dictionary<string, Money>(StringComparer.Ordinal);
        var cashByAccount = new Dictionary<string, Money>(StringComparer.Ordinal);
        foreach (var balance in state.CashBalances)
        {
            if (!fx.TryConvert(balance.Money, baseCcy, out var converted))
            {
                warnings.Add($"账户 {balance.AccountId} 的 {balance.Currency} 现金缺少汇率，未计入总额。");
                continue;
            }

            cashValue += converted;
            Accumulate(cashByCurrency, balance.Currency, converted);
            Accumulate(cashByAccount, balance.AccountId, converted);
        }

        var totalValue = holdingsValue + cashValue;
        var weighted = ApplyWeights(valuations, totalValue);

        return new PortfolioValuation
        {
            BaseCurrency = baseCcy,
            AsOf = asOf,
            Positions = weighted,
            HoldingsValue = holdingsValue,
            CashValue = cashValue,
            TotalValue = totalValue,
            CostBasis = costBasis,
            UnrealizedPnl = unrealized,
            RealizedPnl = realized,
            Dividends = dividends,
            ByAssetClass = GroupBy(weighted, CashBucket(cashValue), totalValue,
                p => AssetKey(p.Position.Symbol, instruments),
                AssetLabel),
            ByRegion = GroupBy(weighted, CashBucket(cashValue), totalValue,
                p => RegionKey(p.Position.Symbol, instruments),
                RegionLabel),
            // Currency exposure only means something if cash keeps its own currency.
            ByCurrency = GroupBy(weighted, cashByCurrency, totalValue,
                p => p.Position.Currency,
                key => key),
            ByAccount = GroupBy(weighted, cashByAccount, totalValue,
                p => p.Position.AccountId,
                key => accounts is not null && accounts.TryGetValue(key, out var account) ? account.Name : key),
            Warnings = warnings,
        };
    }

    private static Money? ConvertOrWarn(
        FxConverter fx, Money amount, string baseCurrency, string context, List<string> warnings)
    {
        if (amount.IsZero)
        {
            return Money.Zero(baseCurrency);
        }

        if (fx.TryConvert(amount, baseCurrency, out var converted))
        {
            return converted;
        }

        warnings.Add($"{context} 缺少 {amount.Currency} → {baseCurrency} 的汇率。");
        return null;
    }

    private static IReadOnlyList<PositionValuation> ApplyWeights(
        List<PositionValuation> valuations, Money totalValue) =>
        valuations
            .Select(v => v.MarketValueBase is { } value && !totalValue.IsZero
                ? v with { Weight = value.Amount / totalValue.Amount }
                : v)
            .OrderByDescending(v => v.MarketValueBase?.Amount ?? decimal.MinValue)
            .ThenBy(v => v.Position.Symbol, StringComparer.Ordinal)
            .ToArray();

    private const string CashKey = "cash";

    private static Dictionary<string, Money> CashBucket(Money cashValue) =>
        cashValue.IsZero
            ? new Dictionary<string, Money>(StringComparer.Ordinal)
            : new Dictionary<string, Money>(StringComparer.Ordinal) { [CashKey] = cashValue };

    private static void Accumulate(Dictionary<string, Money> buckets, string key, Money value) =>
        buckets[key] = buckets.TryGetValue(key, out var current) ? current + value : value;

    private static IReadOnlyList<AllocationSlice> GroupBy(
        IReadOnlyList<PositionValuation> valuations,
        Dictionary<string, Money> cashBuckets,
        Money totalValue,
        Func<PositionValuation, string> keySelector,
        Func<string, string> labelSelector)
    {
        var buckets = new Dictionary<string, Money>(StringComparer.Ordinal);
        foreach (var item in valuations.Where(v => v.MarketValueBase is not null))
        {
            Accumulate(buckets, keySelector(item), item.MarketValueBase!.Value);
        }

        foreach (var pair in cashBuckets)
        {
            Accumulate(buckets, pair.Key, pair.Value);
        }

        return buckets
            .Select(pair => new AllocationSlice
            {
                Key = pair.Key,
                Label = pair.Key == CashKey ? AssetClass.Cash.ToDisplayName() : labelSelector(pair.Key),
                Value = pair.Value,
                Weight = totalValue.IsZero ? 0m : pair.Value.Amount / totalValue.Amount,
            })
            .OrderByDescending(slice => slice.Value.Amount)
            .ToArray();
    }

    private static string AssetKey(string symbol, IReadOnlyDictionary<string, Instrument>? instruments)
    {
        if (instruments is not null && instruments.TryGetValue(symbol, out var instrument))
        {
            return instrument.AssetClass.ToCode();
        }

        // Without instrument metadata the venue is still a usable signal.
        return SymbolId.TryParse(symbol, out var parsed) && ExchangeRegistry.TryGet(parsed.Exchange, out var info)
            ? info.DefaultAssetClass.ToCode()
            : AssetClass.Equity.ToCode();
    }

    private static string AssetLabel(string key) =>
        AssetClasses.TryParse(key, out var asset) ? asset.ToDisplayName() : key;

    private static string RegionKey(string symbol, IReadOnlyDictionary<string, Instrument>? instruments)
    {
        if (instruments is not null && instruments.TryGetValue(symbol, out var instrument))
        {
            return instrument.Region.ToCode();
        }

        return SymbolId.TryParse(symbol, out var parsed) && ExchangeRegistry.TryGetRegion(parsed.Exchange, out var region)
            ? region.ToCode()
            : MarketRegion.Global.ToCode();
    }

    private static string RegionLabel(string key) =>
        MarketRegions.TryParse(key, out var region) ? region.ToDisplayName() : key;
}
