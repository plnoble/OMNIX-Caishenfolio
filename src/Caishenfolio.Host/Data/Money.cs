using System.Globalization;

namespace Caishenfolio.Host.Data;

/// <summary>
/// A currency-tagged decimal amount. The ledger never uses binary floating point for money:
/// arithmetic across different currencies is refused rather than silently summed.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency) =>
        new(amount, Currencies.Normalize(currency));

    public static Money Zero(string currency) => Of(0m, currency);

    public bool IsZero => Amount == 0m;

    /// <summary>Rounds to the currency's minor units (half away from zero, the retail-statement convention).</summary>
    public Money Round() =>
        new(Math.Round(Amount, Currencies.MinorUnitsOf(Currency), MidpointRounding.AwayFromZero), Currency);

    public Money Negate() => new(-Amount, Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>Converts using <paramref name="rate"/> expressed as target-per-unit-of-this-currency.</summary>
    public Money ConvertTo(string targetCurrency, decimal rate)
    {
        var target = Currencies.Normalize(targetCurrency);
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "汇率必须为正数。");
        }

        return string.Equals(target, Currency, StringComparison.Ordinal)
            ? this
            : new Money(Amount * rate, target);
    }

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "+");
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "-");
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => value.Negate();

    public static Money operator *(Money left, decimal factor) => new(left.Amount * factor, left.Currency);

    public static Money operator *(decimal factor, Money right) => right * factor;

    public static Money operator /(Money left, decimal divisor)
    {
        if (divisor == 0m)
        {
            throw new DivideByZeroException("金额除以零。");
        }

        return new Money(left.Amount / divisor, left.Currency);
    }

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right, ">");
        return left.Amount > right.Amount;
    }

    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "<");
        return left.Amount < right.Amount;
    }

    public static bool operator >=(Money left, Money right) => !(left < right);

    public static bool operator <=(Money left, Money right) => !(left > right);

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other, "compare");
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Sums same-currency amounts; an empty sequence has no currency, so the caller must supply one.</summary>
    public static Money Sum(IEnumerable<Money> values, string currency)
    {
        var total = Zero(currency);
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    private static void EnsureSameCurrency(Money left, Money right, string op)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"不能对不同货币做 '{op}' 运算：{left.Currency} 与 {right.Currency}。请先按本位币折算。");
        }
    }

    public override string ToString() =>
        Amount.ToString("0.####", CultureInfo.InvariantCulture) + " " + Currency;
}
