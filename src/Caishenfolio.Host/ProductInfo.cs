using System.Reflection;

namespace Caishenfolio.Host;

/// <summary>
/// Product identity. Version and phase come from the assembly, which MSBuild fills from the
/// root <c>VERSION</c> / <c>PHASE</c> files — the numbers are never typed into code, because
/// they used to live in four places and drifted to four different values.
/// </summary>
public static class ProductInfo
{
    public const string Brand = "OMNIX";
    public const string Name = "OMNIX-Caishenfolio";
    public const string ResearchDisclaimer = "研究/模拟结论，非投资建议。";
    public const string ScopeSummary = "OMNIX · 个人多市场多资产理财工作台（资产账本 + 研究）；不含券商自动挂单。";

    /// <summary>Repository the desktop checks for newer releases.</summary>
    public const string RepositoryUrl = "https://github.com/plnoble/OMNIX-Caishenfolio";

    public static string Version { get; } = ReadInformationalVersion();

    /// <summary>Development phase, e.g. <c>R5</c>. Must match <c>caishenfolio_core.PRODUCT_PHASE</c>.</summary>
    public static string Phase { get; } = ReadMetadata("OmnixPhase") ?? "unknown";

    private static string ReadInformationalVersion()
    {
        var raw = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "0.0.0";
        }

        // Source-control builds append "+<commit>"; the release number is the part before it.
        var plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }

    private static string? ReadMetadata(string key) =>
        typeof(ProductInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))
            ?.Value;
}
