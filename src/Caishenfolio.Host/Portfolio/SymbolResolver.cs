using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>What the user typed, and what the ledger would do with it.</summary>
public sealed record SymbolResolution
{
    /// <summary>Canonical internal symbol, e.g. <c>SSE:600000</c>; null when unresolved.</summary>
    public string? Resolved { get; init; }

    /// <summary>The unmatched input, echoed back so the user can correct it.</summary>
    public required string Input { get; init; }

    /// <summary>How the answer was reached — every path is stated, none is a guess.</summary>
    public required string Reason { get; init; }

    /// <summary>Alternatives when the input matched more than one instrument.</summary>
    public required IReadOnlyList<string> Ambiguities { get; init; } = [];

    public bool IsAmbiguous => Ambiguities.Count > 1;
}

/// <summary>
/// Turns what a novice actually types into the internal <c>EXCHANGE:CODE</c> form.
///
/// Rules, in order:
/// 1. A bare exchange prefix (<c>600000</c>-style codes look like nothing else) is resolved by
///    digit shape: 6 A-share codes are Shanghai, 0/3 are Shenzhen, 4/8 are Beijing, 4–5 digit
///    numeric codes are Hong Kong, alphabetic tickers are US, 6-digit fund codes are FUND.
/// 2. An already-prefixed symbol (<c>SSE:600000</c>, <c>SH600000</c>) passes through normalized.
/// 3. A name or alias from the instrument table matches by exact name, then by contains.
/// 4. Nothing matches → unresolved with the original input preserved; never a fabricated symbol.
///
/// Ambiguity is reported, not silently resolved: <c>00700</c> is a valid HK code and could also
/// collide with a fund code, so the resolution says which readings exist and picks the
/// conventional one only when there is exactly one.
/// </summary>
public static class SymbolResolver
{
    public static SymbolResolution Resolve(
        string input,
        IReadOnlyList<Instrument>? instruments = null)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            return new SymbolResolution { Input = "", Ambiguities = [], Reason = "未输入标的。" };
        }

        // Already internal, possibly with an alias prefix: normalize only.
        if (SymbolId.TryParse(text, out var direct) && direct.Exchange != "")
        {
            return new SymbolResolution
            {
                Resolved = direct.Normalized().Value,
                Input = text,
                Ambiguities = [],
                Reason = "已按「市场:代码」原样识别。",
            };
        }

        // Shape readings from the code's own form (bare digits, bare ticker).
        var shapeCandidates = new List<string>();
        if (text.All(char.IsDigit))
        {
            shapeCandidates.AddRange(NumericCandidates(text));
        }
        else if (IsUsTicker(text))
        {
            // An alphabetic ticker is ambiguous between US venues — IBM trades on NYSE,
            // AAPL on NASDAQ — and the venue decides currency, region, timezone and feeds.
            shapeCandidates.Add($"NASDAQ:{text.ToUpperInvariant()}");
            shapeCandidates.Add($"NYSE:{text.ToUpperInvariant()}");
        }

        // Name/alias matches from the stored instrument table (含拼音简称由调用方存进 Name)。
        // A stored symbol matches by its code part as well as its full form, so a bare
        // "AAPL" finds the instrument the user already trades.
        var byName = new List<string>();
        foreach (var instrument in instruments ?? [])
        {
            var code = SymbolId.TryParse(instrument.Symbol, out var parsed) ? parsed.Code : "";
            if (string.Equals(instrument.Name, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(instrument.Symbol, text, StringComparison.OrdinalIgnoreCase)
                || (code.Length > 0 && string.Equals(code, text, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(instrument.Name)
                    && instrument.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                byName.Add(instrument.Symbol);
            }
        }

        var candidates = shapeCandidates.Concat(byName)
            .Where(c => SymbolId.TryParse(c, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return new SymbolResolution
            {
                Input = text,
                Ambiguities = [],
                Reason = "没找到匹配的标的。可以试试证券代码（如 600000）、名称，或「市场:代码」完整形式。",
            };
        }

        if (candidates.Count == 1)
        {
            var symbol = SymbolId.Parse(candidates[0]).Normalized().Value;
            return new SymbolResolution
            {
                Resolved = symbol,
                Input = text,
                Ambiguities = [],
                Reason = text.All(char.IsDigit) && NumericCandidates(text).Count == 1
                    ? $"按代码规则识别（{ShapeDescription(text)}）。"
                    : "按名称/代码匹配到唯一标的。",
            };
        }

        // A unique instrument-table hit resolves the input (the user's own book is a
        // legitimate disambiguator); several distinct shape readings without one must stay
        // unresolved — silently choosing HKEX for 7203 (Tokyo) or NASDAQ for IBM (NYSE)
        // poisons every number downstream (currency, region, feeds).
        var uniqueTableHit = byName.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uniqueTableHit.Count == 1 && shapeCandidates.Count > 1)
        {
            return new SymbolResolution
            {
                Resolved = uniqueTableHit[0],
                Input = text,
                Ambiguities = candidates,
                Reason = $"按账本标的表唯一匹配到 {uniqueTableHit[0]}。",
            };
        }

        if (shapeCandidates.Count > 1 && uniqueTableHit.Count == 0)
        {
            return new SymbolResolution
            {
                Resolved = null,
                Input = text,
                Ambiguities = candidates,
                Reason = $"「{text}」有 {candidates.Count} 种市场解读（{string.Join("、", candidates)}），" +
                         "裸代码无法唯一确定市场；请用完整形式（如 TSE:7203 / NYSE:IBM）或先在自选里搜索一次。",
            };
        }

        var primary = candidates[0];
        return new SymbolResolution
        {
            Resolved = primary,
            Input = text,
            Ambiguities = candidates,
            Reason = $"「{text}」有 {candidates.Count} 种解读，默认取 {primary}。建议用完整形式消除歧义。",
        };
    }

    /// <summary>
    /// BSE (北交所) bare-code prefixes, mirroring the Python side's ``cn_exchange_for_code``
    /// so both runtimes agree on one mapping instead of two drifting copies.
    /// </summary>
    private static readonly string[] BsePrefixes = ["43", "83", "87", "88", "92"];

    private static List<string> NumericCandidates(string digits)
    {
        var candidates = new List<string>();
        switch (digits.Length)
        {
            case 6 when digits[0] is '6' or '0' or '3':
                // A-share 6-digit: 6→SSE, 0/3→SZSE.
                candidates.Add($"{(digits[0] == '6' ? "SSE" : "SZSE")}:{digits}");
                break;
            case 6 when BsePrefixes.Any(p => digits.StartsWith(p, StringComparison.Ordinal)):
                // Beijing exchange stocks (430/830/870/920 series) — not mutual funds.
                candidates.Add($"BSE:{digits}");
                break;
            case 6:
                // Not an A-share or BSE shape; could be a CN fund code.
                candidates.Add($"FUND:{digits}");
                break;
            case 5:
                // 5 digits: Hong Kong convention (kept with the user's leading zero).
                candidates.Add($"HKEX:{digits}");
                break;
            case 4:
                // 4 digits is genuinely ambiguous: 00700 is HK, 7203 is Tokyo. Both venues
                // are listed and the input stays unresolved until the user disambiguates —
                // a wrong venue silently mislabels currency, region and every feed after it.
                candidates.Add($"HKEX:{digits.PadLeft(5, '0')}");
                candidates.Add($"TSE:{digits.PadLeft(4, '0')}");
                break;
        }

        return candidates;
    }

    private static string ShapeDescription(string digits) => digits switch
    {
        _ when digits.Length == 6 && digits[0] == '6' => "6 位数字、以 6 开头 = 上海",
        _ when digits.Length == 6 && (digits[0] == '0' || digits[0] == '3') => "6 位数字、以 0/3 开头 = 深圳",
        _ when digits.Length == 5 => "5 位数字 = 香港",
        _ => "数字代码",
    };

    private static bool IsUsTicker(string text) =>
        text.Length > 0 && text.Length <= 6 && text.All(c => char.IsAsciiLetter(c) || c == '.' || c == '-');
}
