namespace Caishenfolio.Host.Portfolio;

public enum AccountKind
{
    /// <summary>券商证券账户（A股/港股通/美股等）。</summary>
    Securities,
    /// <summary>基金销售平台账户（场外公募）。</summary>
    FundPlatform,
    /// <summary>银行账户/存款。</summary>
    Bank,
    /// <summary>现金或钱包。</summary>
    Cash,
    Other,
}

/// <summary>
/// A place where holdings and cash live. An account is deliberately not tied to one currency —
/// a 港美股账户 holds HKD and USD cash side by side, so balances are keyed by (account, currency).
/// </summary>
public sealed record Account
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AccountKind Kind { get; init; }
    /// <summary>Currency this account is usually funded in; reporting still rolls up to the portfolio base currency.</summary>
    public required string MainCurrency { get; init; }
    public string Broker { get; init; } = "";
    public string Note { get; init; } = "";
    public bool Archived { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static Account Create(
        string name,
        AccountKind kind,
        string mainCurrency,
        string broker = "",
        string note = "",
        string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Account
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"acct_{Guid.NewGuid():N}" : id!.Trim(),
            Name = name.Trim(),
            Kind = kind,
            MainCurrency = Data.Currencies.Normalize(mainCurrency),
            Broker = broker.Trim(),
            Note = note.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
