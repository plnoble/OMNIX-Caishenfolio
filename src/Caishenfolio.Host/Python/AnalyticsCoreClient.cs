using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Python;

public sealed class AnalyticsCoreClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public AnalyticsCoreClient(Uri baseAddress, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        LoopbackBindPolicy.EnsureLoopback(baseAddress.Host);

        if (httpClient is null)
        {
            // Default client timeout for bars/research; search uses a shorter CTS.
            _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(120) };
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _http.BaseAddress ??= baseAddress;
            _ownsClient = false;
        }
    }

    public static AnalyticsCoreClient ForLoopback(int port = 8765) =>
        new(new Uri($"http://127.0.0.1:{port}/"));

    public async Task<AnalyticsHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<AnalyticsHealth>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? throw new InvalidOperationException("Health payload was empty.");
    }

    public async Task<MarketDiagnostics> GetMarketDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("market/diagnostics", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<MarketDiagnostics>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new MarketDiagnostics();
    }

    public async Task<SymbolSearchResponse> SearchSymbolsAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(query)
            ? $"symbols/search?limit={limit}"
            : $"symbols/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        // Search must not hang for a full minute if upstream stalls.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(path, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<SymbolSearchResponse>(JsonOptions, timeoutCts.Token)
            .ConfigureAwait(false);
        return payload ?? new SymbolSearchResponse();
    }

    public async Task<MarketBarsResponse> GetMarketBarsAsync(
        string symbol,
        string start,
        string end,
        string adjustment = "raw",
        string interval = "daily",
        CancellationToken cancellationToken = default)
    {
        var path =
            $"market/bars?symbol={Uri.EscapeDataString(symbol)}" +
            $"&start={Uri.EscapeDataString(start)}" +
            $"&end={Uri.EscapeDataString(end)}" +
            $"&adjustment={Uri.EscapeDataString(adjustment)}" +
            $"&interval={Uri.EscapeDataString(interval)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<MarketBarsResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new MarketBarsResponse { Ok = false, Error = "Empty bars payload." };
    }

    public async Task<JsonElement> SyncWatchlistCacheAsync(
        IEnumerable<string> symbols,
        int years = 10,
        CancellationToken cancellationToken = default)
    {
        var body = new { symbols = symbols.ToArray(), years };
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("market/cache/sync", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> ClearBarsCacheAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var body = new { symbol };
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("market/cache/clear", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> GetBarsCacheStatsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("market/cache", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    public async Task<ResearchSnapshotResponse> RunSymbolSnapshotAsync(
        string symbol,
        string start,
        string end,
        string adjustment = "raw",
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            symbol,
            start,
            end,
            adjustment,
        };
        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("research/symbol-snapshot", content, cancellationToken)
            .ConfigureAwait(false);
        var payload = await response.Content
            .ReadFromJsonAsync<ResearchSnapshotResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? new ResearchSnapshotResponse { Ok = false, Error = "Empty research payload." };

        // 422 is used for fail-closed market errors with a full task payload.
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity)
        {
            response.EnsureSuccessStatusCode();
        }

        return payload;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

public sealed class AnalyticsHealth
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("product")]
    public string Product { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("disclaimer")]
    public string Disclaimer { get; set; } = "";

    [JsonPropertyName("live_trading_enabled")]
    public bool LiveTradingEnabled { get; set; }

    [JsonPropertyName("market_provider")]
    public string MarketProvider { get; set; } = "";

    [JsonPropertyName("market_provider_ready")]
    public bool MarketProviderReady { get; set; }

    [JsonPropertyName("market_data_synthetic")]
    public bool MarketDataSynthetic { get; set; }

    [JsonPropertyName("http_trust_env")]
    public bool HttpTrustEnv { get; set; } = true;
}

public sealed class MarketDiagnostics
{
    [JsonPropertyName("market_provider")]
    public string MarketProvider { get; set; } = "";

    [JsonPropertyName("market_provider_ready")]
    public bool MarketProviderReady { get; set; }

    [JsonPropertyName("market_data_synthetic")]
    public bool MarketDataSynthetic { get; set; }

    [JsonPropertyName("http_trust_env")]
    public bool HttpTrustEnv { get; set; } = true;

    [JsonPropertyName("tips")]
    public List<string> Tips { get; set; } = new();

    [JsonPropertyName("supported_examples")]
    public List<string> SupportedExamples { get; set; } = new();
}

public sealed class SymbolSearchResponse
{
    [JsonPropertyName("items")]
    public List<SymbolSearchItem> Items { get; set; } = new();

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";
}

public sealed class SymbolSearchItem
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("market")]
    public string Market { get; set; } = "";

    [JsonPropertyName("asset_class")]
    public string AssetClass { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";
}

public sealed class MarketBarsResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("interval")]
    public string Interval { get; set; } = "daily";

    [JsonPropertyName("interval_label")]
    public string IntervalLabel { get; set; } = "日K";

    [JsonPropertyName("from_cache")]
    public bool FromCache { get; set; }

    [JsonPropertyName("data")]
    public List<MarketBarDto>? Data { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class MarketBarDto
{
    [JsonPropertyName("timestamp_utc")]
    public string TimestampUtc { get; set; } = "";

    [JsonPropertyName("open")]
    public decimal Open { get; set; }

    [JsonPropertyName("high")]
    public decimal High { get; set; }

    [JsonPropertyName("low")]
    public decimal Low { get; set; }

    [JsonPropertyName("close")]
    public decimal Close { get; set; }

    // Upstream (AkShare) often emits volume as JSON number with fraction (e.g. 1.23e6 or 1000.0).
    // Must not use Int64 — System.Text.Json rejects non-integer numbers for long.
    [JsonPropertyName("volume")]
    public decimal Volume { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";
}

public sealed class ResearchSnapshotResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("task")]
    public ResearchTaskDto? Task { get; set; }

    [JsonPropertyName("artifact")]
    public ResearchArtifactDto? Artifact { get; set; }

    [JsonPropertyName("summary")]
    public JsonElement? Summary { get; set; }

    [JsonPropertyName("disclaimer")]
    public string Disclaimer { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class ResearchTaskDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}

public sealed class ResearchArtifactDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("uri_or_payload")]
    public string? UriOrPayload { get; set; }
}
