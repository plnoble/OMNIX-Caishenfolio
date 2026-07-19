using System.Text.RegularExpressions;

namespace Caishenfolio.Host.Security;

public static partial class SensitiveValueRedactor
{
    private static readonly HashSet<string> SensitiveKeyFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "secret",
        "token",
        "api_key",
        "apikey",
        "authorization",
        "access_key",
        "private_key",
        "credential",
    };

    public static string RedactText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var redacted = CredentialAssignmentRegex().Replace(input, "$1=[REDACTED]");
        redacted = BearerRegex().Replace(redacted, "Bearer [REDACTED]");
        return redacted;
    }

    public static object? RedactObject(object? value, string? key = null)
    {
        if (key is not null && IsSensitiveKey(key))
        {
            return "[REDACTED]";
        }

        return value switch
        {
            null => null,
            string s => RedactText(s),
            IDictionary<string, object?> dict => dict.ToDictionary(
                pair => pair.Key,
                pair => RedactObject(pair.Value, pair.Key)),
            IReadOnlyDictionary<string, object?> dict => dict.ToDictionary(
                pair => pair.Key,
                pair => RedactObject(pair.Value, pair.Key)),
            IEnumerable<object?> list when value is not string => list.Select(item => RedactObject(item)).ToArray(),
            _ => value,
        };
    }

    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Replace('-', '_').Replace(' ', '_');
        return SensitiveKeyFragments.Any(fragment =>
            normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"(?i)\b(password|secret|token|api[_-]?key|authorization|access[_-]?key|private[_-]?key|credential)\b\s*[:=]\s*([^\s,;]+)", RegexOptions.Compiled)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled)]
    private static partial Regex BearerRegex();
}
