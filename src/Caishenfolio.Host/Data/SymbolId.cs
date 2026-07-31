using System.Text.RegularExpressions;

namespace Caishenfolio.Host.Data;

public sealed partial class SymbolId : IEquatable<SymbolId>
{
    private static readonly Regex Pattern = SymbolRegex();

    public string Exchange { get; }
    public string Code { get; }
    public string Value => $"{Exchange}:{Code}";

    private SymbolId(string exchange, string code)
    {
        Exchange = exchange;
        Code = code;
    }

    public static SymbolId Parse(string value)
    {
        if (!TryParse(value, out var symbol))
        {
            throw new FormatException(
                $"Invalid symbol '{value}'. Expected EXCHANGE:SYMBOL (e.g. SSE:600000, NASDAQ:AAPL).");
        }

        return symbol;
    }

    public static bool TryParse(string? value, out SymbolId symbol)
    {
        symbol = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Pattern.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        symbol = new SymbolId(match.Groups["exchange"].Value.ToUpperInvariant(), match.Groups["code"].Value.ToUpperInvariant());
        return true;
    }

    /// <summary>Builds the identity of a currency pair, e.g. USD/CNY → <c>FX:USDCNY</c>.</summary>
    public static SymbolId FxPair(string baseCurrency, string quoteCurrency) =>
        new(ExchangeRegistry.Fx, Currencies.Normalize(baseCurrency) + Currencies.Normalize(quoteCurrency));

    public bool IsFx => string.Equals(Exchange, ExchangeRegistry.Fx, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits <c>FX:USDCNY</c> into its base and quote currencies; false for non-FX or unknown codes.</summary>
    public bool TryGetFxPair(out string baseCurrency, out string quoteCurrency)
    {
        baseCurrency = "";
        quoteCurrency = "";
        if (!IsFx || Code.Length != 6)
        {
            return false;
        }

        var left = Code[..3];
        var right = Code[3..];
        if (!Currencies.IsKnown(left) || !Currencies.IsKnown(right))
        {
            return false;
        }

        baseCurrency = Currencies.Normalize(left);
        quoteCurrency = Currencies.Normalize(right);
        return true;
    }

    /// <summary>
    /// Resolves venue aliases so the ledger keeps one identity per instrument
    /// (<c>SH:600000</c> and <c>SSE:600000</c> collapse to the same symbol).
    /// Unknown venues are preserved as-is rather than rejected, so provider output still parses.
    /// </summary>
    public SymbolId Normalized() =>
        ExchangeRegistry.TryGet(Exchange, out var info) && !string.Equals(info.Code, Exchange, StringComparison.Ordinal)
            ? new SymbolId(info.Code, Code)
            : this;

    public override string ToString() => Value;

    public bool Equals(SymbolId? other) =>
        other is not null
        && string.Equals(Exchange, other.Exchange, StringComparison.Ordinal)
        && string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SymbolId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Exchange, Code);

    [GeneratedRegex(@"^(?<exchange>[A-Z0-9.]+):(?<code>[A-Z0-9.\-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex SymbolRegex();
}
