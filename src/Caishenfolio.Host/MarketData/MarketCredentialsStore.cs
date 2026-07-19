using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caishenfolio.Host.MarketData;

/// <summary>
/// Local market credentials under State root. Never commit this file to git.
/// </summary>
public sealed class MarketCredentialsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    public MarketCredentialsStore(string stateRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootDirectory);
        Directory.CreateDirectory(stateRootDirectory);
        _filePath = Path.Combine(Path.GetFullPath(stateRootDirectory), "market_credentials.json");
    }

    public string FilePath => _filePath;

    public MarketCredentials Load()
    {
        if (!File.Exists(_filePath))
        {
            return new MarketCredentials();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<MarketCredentials>(json, JsonOptions) ?? new MarketCredentials();
        }
        catch
        {
            return new MarketCredentials();
        }
    }

    public void Save(MarketCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    public void ApplyToEnvironment(IDictionary<string, string?> environment)
    {
        var creds = Load();
        environment["CAISHENFOLIO_CREDENTIALS_PATH"] = _filePath;
        environment["CAISHENFOLIO_MARKET_PROVIDER"] = string.IsNullOrWhiteSpace(creds.MarketProvider)
            ? "auto"
            : creds.MarketProvider.Trim();
        environment["CAISHENFOLIO_HTTP_TRUST_ENV"] = creds.HttpTrustEnv ? "1" : "0";

        if (!string.IsNullOrWhiteSpace(creds.TushareToken))
        {
            environment["CAISHENFOLIO_TUSHARE_TOKEN"] = creds.TushareToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(creds.AlphavantageApiKey))
        {
            environment["CAISHENFOLIO_ALPHAVANTAGE_API_KEY"] = creds.AlphavantageApiKey.Trim();
        }

        // Bars cache location (user-selectable). Reject empty → default beside credentials.
        var cachePath = string.IsNullOrWhiteSpace(creds.BarsCachePath)
            ? Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "bars_cache.db")
            : creds.BarsCachePath.Trim();
        cachePath = Path.GetFullPath(cachePath);
        var cacheDir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        environment["CAISHENFOLIO_BARS_CACHE_PATH"] = cachePath;
        environment["CAISHENFOLIO_BARS_CACHE"] = "1";
        environment["CAISHENFOLIO_BARS_CACHE_MAX_MB"] = string.IsNullOrWhiteSpace(creds.BarsCacheMaxMb)
            ? "512"
            : creds.BarsCacheMaxMb.Trim();
        environment["CAISHENFOLIO_SYMBOL_INDEX_PATH"] = Path.Combine(
            Path.GetDirectoryName(cachePath) ?? ".",
            "symbol_name_index.json");
    }
}

public sealed class MarketCredentials
{
    public string MarketProvider { get; set; } = "auto";
    public bool HttpTrustEnv { get; set; }
    public string TushareToken { get; set; } = "";
    public string AlphavantageApiKey { get; set; } = "";

    /// <summary>Full path to bars_cache.db (or directory+filename). Empty = default under State.</summary>
    public string BarsCachePath { get; set; } = "";

    /// <summary>Max cache size in MB (string for simple JSON). Default 512.</summary>
    public string BarsCacheMaxMb { get; set; } = "512";
}
