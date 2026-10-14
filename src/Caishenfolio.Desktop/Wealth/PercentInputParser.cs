namespace Caishenfolio.Desktop.Wealth;

/// <summary>Outcome of parsing a user-typed percentage.</summary>
public readonly record struct PercentParseResult(bool Ok, decimal Fraction, string? Error)
{
    public static PercentParseResult Fail(string error) => new(false, 0m, error);
}

/// <summary>
/// One parser, one contract (remediation WP5): the user types a PERCENT (20 = 20%), the
/// API speaks a 0~1 fraction. Both the evidence-card view and its tests call THIS code —
/// a mirror implementation in the test file is how a broken conversion once stayed green.
/// </summary>
public static class PercentInputParser
{
    /// <summary>Validates 0 &lt; percent &lt;= 100 and converts once, right here.</summary>
    public static PercentParseResult ParseRequiredFraction(string? text, string fieldLabel)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return PercentParseResult.Fail($"「{fieldLabel}」未填写。");
        }

        if (!decimal.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            return PercentParseResult.Fail($"「{fieldLabel}」无法解析为数字：{trimmed}。");
        }

        if (percent <= 0m || percent > 100m)
        {
            return PercentParseResult.Fail(
                $"「{fieldLabel}」必须是 0 到 100 之间的百分比（收到 {trimmed}）。");
        }

        return new PercentParseResult(true, percent / 100m, null);
    }
}
