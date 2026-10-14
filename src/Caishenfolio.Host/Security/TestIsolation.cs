namespace Caishenfolio.Host.Security;

/// <summary>
/// The only door through which a test harness may redirect state or force the market provider.
/// Production boots never set these variables. A name containing "TEST" is not enough: the
/// state root must be a real local path, and the provider override only applies together with
/// that root and only for the allowlisted fixture value.
/// </summary>
public static class TestIsolation
{
    public const string StateRootVariable = "CAISHENFOLIO_TEST_STATE_ROOT";
    public const string ForceProviderVariable = "CAISHENFOLIO_TEST_FORCE_PROVIDER";

    /// <summary>Providers a test harness is allowed to force. Real/upstream names are refused.</summary>
    public static readonly string[] AllowedForcedProviders = ["fixture"];

    public static TestIsolationDecision Resolve()
    {
        var raw = Environment.GetEnvironmentVariable(StateRootVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TestIsolationDecision(TestIsolationMode.Production, "", "", false);
        }

        if (!TryNormalizeStateRoot(raw, out var root, out var reason))
        {
            return new TestIsolationDecision(TestIsolationMode.Invalid, "", reason, false);
        }

        var provider = "";
        var forced = false;
        var providerRaw = Environment.GetEnvironmentVariable(ForceProviderVariable);
        if (!string.IsNullOrWhiteSpace(providerRaw))
        {
            var selected = providerRaw.Trim().ToLowerInvariant();
            if (!AllowedForcedProviders.Contains(selected, StringComparer.Ordinal))
            {
                return new TestIsolationDecision(
                    TestIsolationMode.Invalid,
                    "",
                    "测试模式只允许强制 fixture 数据源。",
                    false);
            }

            provider = selected;
            forced = true;
        }

        return new TestIsolationDecision(TestIsolationMode.Isolated, root, "", forced, provider);
    }

    public static bool TryResolveStateRoot(out string resolvedPath, out string reason)
    {
        var decision = Resolve();
        resolvedPath = decision.StateRoot;
        reason = decision.Reason;
        return decision.Mode == TestIsolationMode.Isolated;
    }

    public static bool TryNormalizeStateRoot(string raw, out string resolvedPath, out string reason)
    {
        resolvedPath = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "测试状态根为空。";
            return false;
        }

        var trimmed = raw.Trim().Trim('"');
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "测试状态根拒绝 UNC 路径。";
            return false;
        }

        if (trimmed.IndexOfAny(['*', '?']) >= 0)
        {
            reason = "测试状态根含通配符。";
            return false;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            reason = "测试状态根必须是完整绝对路径。";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex)
        {
            reason = $"测试状态根无法规范化：{ex.Message}";
            return false;
        }

        if (!Path.IsPathRooted(full))
        {
            reason = "测试状态根不是绝对路径。";
            return false;
        }

        var temp = Path.GetTempPath();
        var scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaishenfolioTest");
        if (!IsUnder(temp, full) && !IsUnder(scratch, full))
        {
            reason = "测试状态根必须位于临时目录或 CaishenfolioTest 隔离目录下。";
            return false;
        }

        var production = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio");
        if (IsSameOrRelated(production, full))
        {
            reason = "测试状态根不能是正式数据目录或其父/子目录。";
            return false;
        }

        if (ContainsReparsePoint(full))
        {
            reason = "测试状态根或其父目录是重解析点。";
            return false;
        }

        resolvedPath = full;
        return true;
    }

    /// <summary>
    /// Returns the allowlisted provider only when an isolated state root is also present.
    /// Missing isolation, an empty value, or any non-fixture name returns false and must
    /// leave the production provider priority unchanged.
    /// </summary>
    public static bool TryGetForcedProvider(out string provider)
    {
        var decision = Resolve();
        provider = decision.ForcedProvider;
        return decision.Mode == TestIsolationMode.Isolated && decision.ProviderForced;
    }

    private static bool IsUnder(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = candidate
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrRelated(string left, string right) =>
        IsUnder(left, right) || IsUnder(right, left) ||
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool ContainsReparsePoint(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? "";
        }

        return false;
    }
}

public enum TestIsolationMode
{
    Production,
    Isolated,
    Invalid,
}

public readonly record struct TestIsolationDecision(
    TestIsolationMode Mode,
    string StateRoot,
    string Reason,
    bool ProviderForced,
    string ForcedProvider = "");
