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
