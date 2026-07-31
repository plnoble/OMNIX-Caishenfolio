using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// One observed exchange rate: <see cref="Rate"/> units of <see cref="QuoteCurrency"/>
/// per single unit of <see cref="BaseCurrency"/>. USDCNY = 7.2 means base USD, quote CNY.
/// </summary>
public sealed record FxRate
{
    public required string BaseCurrency { get; init; }
    public required string QuoteCurrency { get; init; }
    public required decimal Rate { get; init; }
    public required DateOnly AsOf { get; init; }
    public string Provider { get; init; } = "";

    public static FxRate Of(string baseCurrency, string quoteCurrency, decimal rate, DateOnly asOf, string provider = "")
    {
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "汇率必须为正数。");
        }

        var b = Currencies.Normalize(baseCurrency);
        var q = Currencies.Normalize(quoteCurrency);
        if (b == q)
        {
            throw new ArgumentException("汇率的两种货币不能相同。", nameof(quoteCurrency));
        }

        return new FxRate
        {
            BaseCurrency = b,
            QuoteCurrency = q,
            Rate = rate,
            AsOf = asOf,
            Provider = provider,
        };
    }

    /// <summary>Symbol this rate is published under, e.g. <c>FX:USDCNY</c>.</summary>
    public string Symbol => SymbolId.FxPair(BaseCurrency, QuoteCurrency).Value;
}

/// <summary>
/// Converts money between currencies using observed rates, falling back to the inverse rate
/// and then to triangulation through a pivot currency. A missing rate is reported, never
/// guessed — an unconvertible holding must surface as a warning rather than a wrong total.
/// </summary>
public sealed class FxConverter
{
    private readonly Dictionary<(string From, string To), decimal> _direct = new();
    private readonly string _pivot;

    public FxConverter(IEnumerable<FxRate> rates, string pivot = Currencies.Usd)
    {
        ArgumentNullException.ThrowIfNull(rates);
        _pivot = Currencies.Normalize(pivot);

        // Later observations win, so callers can pass a history and get the freshest rate.
        foreach (var rate in rates.OrderBy(r => r.AsOf))
        {
            _direct[(rate.BaseCurrency, rate.QuoteCurrency)] = rate.Rate;
            _direct[(rate.QuoteCurrency, rate.BaseCurrency)] = 1m / rate.Rate;
        }
    }

    public static FxConverter Empty { get; } = new([]);

    public bool TryGetRate(string from, string to, out decimal rate)
    {
        var f = Currencies.Normalize(from);
        var t = Currencies.Normalize(to);
        if (f == t)
        {
            rate = 1m;
            return true;
        }

        if (_direct.TryGetValue((f, t), out rate))
        {
            return true;
        }

        // Triangulate: HKD -> USD -> CNY when no HKDCNY rate was published.
        if (_direct.TryGetValue((f, _pivot), out var toPivot) &&
            _direct.TryGetValue((_pivot, t), out var fromPivot))
        {
            rate = toPivot * fromPivot;
            return true;
        }

        rate = 0m;
        return false;
    }

    /// <summary>
    /// Converts and rounds to the target currency's minor units. Rounding here is deliberate:
    /// a triangulated rate such as JPY→USD→CNY has no exact decimal form, and the result is a
    /// money value in the target currency, so it should carry that currency's precision rather
    /// than a tail of noise that would surface in totals and exports.
    /// </summary>
    public bool TryConvert(Money amount, string targetCurrency, out Money converted)
    {
        if (!TryGetRate(amount.Currency, targetCurrency, out var rate))
        {
            converted = default;
            return false;
        }

        converted = amount.ConvertTo(targetCurrency, rate).Round();
        return true;
    }

    public Money Convert(Money amount, string targetCurrency) =>
        TryConvert(amount, targetCurrency, out var converted)
            ? converted
            : throw new LedgerException($"缺少 {amount.Currency} → {Currencies.Normalize(targetCurrency)} 的汇率。");
}
