using Caishenfolio.Host.Data;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Host.Portfolio;

/// <summary>Market inputs a valuation needs, plus whatever could not be fetched.</summary>
public sealed record PricingSnapshot
{
    public required IReadOnlyDictionary<string, PriceQuote> Quotes { get; init; }
    public required FxConverter Fx { get; init; }
    public required IReadOnlyList<FxRate> Rates { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Source of prices and rates, so valuation can be tested without a running core.</summary>
public interface IMarketPricingSource
{
    Task<PriceQuote?> TryGetQuoteAsync(string symbol, CancellationToken cancellationToken = default);

    Task<FxRate?> TryGetFxRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default);
}

/// <summary>Pricing backed by the Python Analytics Core over loopback.</summary>
public sealed class AnalyticsCorePricingSource(AnalyticsCoreClient client) : IMarketPricingSource
{
    public async Task<PriceQuote?> TryGetQuoteAsync(
        string symbol, CancellationToken cancellationToken = default)
    {
        var response = await client.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.Data is null)
        {
            return null;
        }

        var data = response.Data;
        return PriceQuote.Of(
            data.Symbol,
            data.Price,
            data.Currency,
            ParseDate(data.AsOf),
            data.Provider);
    }

    public async Task<FxRate?> TryGetFxRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken cancellationToken = default)
    {
        var response = await client
            .GetFxRateAsync(baseCurrency, quoteCurrency, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok || response.Data is null || response.Data.Rate <= 0m)
        {
            return null;
        }

        var data = response.Data;
        return FxRate.Of(data.BaseCurrency, data.QuoteCurrency, data.Rate, ParseDate(data.AsOf), data.Provider);
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParse(value, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>
/// Collects the prices and rates a ledger needs and hands back a ready valuation input.
///
/// Every fetch is allowed to fail: a missing quote or rate becomes a warning, and the
/// valuation then reports itself as incomplete rather than pretending the holding is worthless.
/// Fetched rates are written to the store so the next valuation still works offline.
/// </summary>
public sealed class PortfolioPricingService(IMarketPricingSource source, PortfolioStore? store = null)
{
    /// <summary>Pivot for triangulation — most published pairs are quoted against USD.</summary>
    private const string Pivot = Currencies.Usd;

    public async Task<PricingSnapshot> FetchAsync(
        LedgerState state,
        string baseCurrency,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var baseCcy = Currencies.Normalize(baseCurrency);
        var warnings = new List<string>();

        var quotes = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        foreach (var symbol in state.OpenPositions.Select(p => p.Symbol).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var quote = await source.TryGetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
                if (quote is null)
                {
                    warnings.Add($"{symbol} 未取得最新价格。");
                    continue;
                }

                quotes[symbol] = quote;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"{symbol} 取价失败：{ex.Message}");
            }
        }

        var currencies = CurrenciesInPlay(state, quotes, baseCcy);
        var rates = new List<FxRate>();
        foreach (var currency in currencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rate = await FetchPairAsync(currency, baseCcy, cancellationToken).ConfigureAwait(false);
            if (rate is not null)
            {
                rates.Add(rate);
                continue;
            }

            // Fall back to the pivot leg so triangulation can still bridge the gap.
            var viaPivot = await FetchPairAsync(currency, Pivot, cancellationToken).ConfigureAwait(false);
            if (viaPivot is not null)
            {
                rates.Add(viaPivot);
                continue;
            }

            warnings.Add($"未取得 {currency} → {baseCcy} 的汇率。");
        }

        if (rates.Count > 0)
        {
            store?.SaveFxRates(rates);
        }

        // Stored snapshots fill in whatever this fetch could not reach.
        var known = store is null ? rates : store.ListFxRates(asOf).Concat(rates).ToList();

        return new PricingSnapshot
        {
            Quotes = quotes,
            Fx = new FxConverter(known, Pivot),
            Rates = rates,
            Warnings = warnings,
        };
    }

    private async Task<FxRate?> FetchPairAsync(
        string from, string to, CancellationToken cancellationToken)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return await source.TryGetFxRateAsync(from, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Currencies that must reach the base currency: holdings, quotes, and cash.</summary>
    private static IReadOnlyList<string> CurrenciesInPlay(
        LedgerState state, IReadOnlyDictionary<string, PriceQuote> quotes, string baseCurrency)
    {
        var currencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var position in state.Positions)
        {
            currencies.Add(position.Currency);
        }

        foreach (var quote in quotes.Values)
        {
            currencies.Add(quote.Currency);
        }

        foreach (var balance in state.CashBalances)
        {
            currencies.Add(balance.Currency);
        }

        currencies.Remove(baseCurrency);
        return currencies.OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }
}
