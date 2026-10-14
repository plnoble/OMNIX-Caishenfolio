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

    /// <summary>Latest price for one instrument — the channel portfolio valuation consumes.</summary>
    public async Task<MarketQuoteResponse> GetQuoteAsync(
        string symbol,
        bool crossCheck = false,
        decimal? tolerancePercent = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"market/quote?symbol={Uri.EscapeDataString(symbol)}";
        if (crossCheck)
        {
            path += "&cross_check=1";
            if (tolerancePercent is { } tolerance)
            {
                path += $"&tolerance={tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }
        }

        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<MarketQuoteResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new MarketQuoteResponse { Ok = false, Error = "Empty quote payload." };
    }

    /// <summary>Daily NAV series for an off-exchange fund; funds have no OHLCV.</summary>
    public async Task<MarketNavResponse> GetNavSeriesAsync(
        string symbol,
        string start,
        string end,
        CancellationToken cancellationToken = default)
    {
        var path =
            $"market/nav?symbol={Uri.EscapeDataString(symbol)}" +
            $"&start={Uri.EscapeDataString(start)}" +
            $"&end={Uri.EscapeDataString(end)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<MarketNavResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new MarketNavResponse { Ok = false, Error = "Empty NAV payload." };
    }

    public async Task<MarketFxResponse> GetFxRateAsync(
        string baseCurrency,
        string quoteCurrency,
        CancellationToken cancellationToken = default)
    {
        var path =
            $"market/fx?base={Uri.EscapeDataString(baseCurrency)}" +
            $"&quote={Uri.EscapeDataString(quoteCurrency)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<MarketFxResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new MarketFxResponse { Ok = false, Error = "Empty FX payload." };
    }

    /// <summary>Valuation multiples with their percentile, band, explanation and history.</summary>
    public async Task<ValuationResponse> GetValuationAsync(
        string symbol,
        int years = 10,
        CancellationToken cancellationToken = default)
    {
        var path = $"market/valuation?symbol={Uri.EscapeDataString(symbol)}&years={years}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<ValuationResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new ValuationResponse { Ok = false, Error = "Empty valuation payload." };
    }

    /// <summary>Interest-rate differentials and where each pair sits in its own range.</summary>
    public async Task<FxCarryResponse> GetFxCarryAsync(
        string baseCurrency,
        IEnumerable<string> currencies,
        int days = 365,
        CancellationToken cancellationToken = default)
    {
        var path =
            $"market/fx/carry?base={Uri.EscapeDataString(baseCurrency)}" +
            $"&currencies={Uri.EscapeDataString(string.Join(",", currencies))}&days={days}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<FxCarryResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new FxCarryResponse { Ok = false };
    }

    /// <summary>
    /// The N4 evidence card (plan §9): traceable facts about a symbol — data quality,
    /// valuation, fundamentals, market behaviour, historical check, portfolio impact —
    /// ending in a status about the evidence, never a trade instruction.
    /// </summary>
    public async Task<JsonElement> PostEvidenceCardAsync(
        string symbol,
        string start,
        string end,
        string adjustment = "raw",
        decimal? candidateAmount = null,
        string? candidateCurrency = null,
        decimal? portfolioValue = null,
        string? portfolioCurrency = null,
        decimal? currentWeightOfSymbol = null,
        decimal? singlePositionLimit = null,
        decimal? cashAvailable = null,
        int? horizonDays = null,
        string side = "buy",
        string funding = "existing_cash",
        string? fundingCurrency = null,
        decimal? maxLossPct = null,
        decimal? priceTolerancePct = null,
        IReadOnlyDictionary<string, double>? regionWeights = null,
        IReadOnlyDictionary<string, double>? currencyWeights = null,
        string instrumentRegion = "",
        string instrumentCurrency = "",
        CancellationToken cancellationToken = default)
    {
        object? portfolio = null;
        if (candidateAmount is not null && portfolioValue is not null)
        {
            portfolio = new Dictionary<string, object?>
            {
                ["candidate_amount"] = candidateAmount,
                ["candidate_currency"] = candidateCurrency ?? portfolioCurrency ?? "",
                ["portfolio_value"] = portfolioValue,
                ["portfolio_currency"] = portfolioCurrency ?? "",
                ["current_weight_of_symbol"] = currentWeightOfSymbol ?? 0m,
                ["single_position_limit"] = singlePositionLimit,
                ["cash_available"] = cashAvailable ?? 0m,
                ["horizon_days"] = horizonDays,
                // P1-01/P1-10: explicit funding source and the user's own loss ceiling are
                // auditable inputs the server gates on — they travel with the request.
                ["funding"] = funding,
                ["funding_currency"] = fundingCurrency ?? portfolioCurrency ?? "",
                ["max_loss_pct"] = maxLossPct,
                // P2-R02: real shape facts, so the server can compute after-weights.
                ["region_weights"] = regionWeights,
                ["currency_weights"] = currencyWeights,
                ["instrument_region"] = instrumentRegion,
                ["instrument_currency"] = instrumentCurrency,
            };
        }

        var body = new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["start"] = start,
            ["end"] = end,
            ["adjustment"] = adjustment,
            ["portfolio"] = portfolio,
            // P2-03: the direction is a first-class request parameter, not a UI-only concept.
            ["side"] = side,
        };
        if (priceTolerancePct is not null)
        {
            body["price_tolerance_pct"] = priceTolerancePct;
        }

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("research/evidence-card", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Exchange-path comparison (N2): held-currency pot vs fresh base money vs via/direct,
    /// each with gross/net amounts, cost breakdown, breakeven and quote provenance.
    /// </summary>
    public async Task<FxPathsResponse> PostFxPathsAsync(        string baseCurrency,
        string targetCurrency,
        decimal baseAmount,
        string? heldCurrency = null,
        decimal? heldAmount = null,
        decimal? heldCostInBase = null,
        IReadOnlyDictionary<string, FxPathRequestCost>? costs = null,
        decimal? splitFraction = null,
        CancellationToken cancellationToken = default)
    {
        object? held = heldCurrency is null || heldAmount is null || heldCostInBase is null
            ? null
            : new
            {
                currency = heldCurrency,
                amount = heldAmount.Value,
                cost_in_base = heldCostInBase.Value,
            };

        var costPayload = costs?.ToDictionary(
            pair => pair.Key,
            pair => (object)new
            {
                spread_pct = pair.Value.SpreadPercent,
                fixed_fee = pair.Value.FixedFee,
                fee_currency = string.IsNullOrWhiteSpace(pair.Value.FeeCurrency) ? null : pair.Value.FeeCurrency,
                fee_pct = pair.Value.FeePercent,
                slippage_pct = pair.Value.SlippagePercent,
            });

        var body = new Dictionary<string, object?>
        {
            ["base_currency"] = baseCurrency,
            ["target_currency"] = targetCurrency,
            ["base_amount"] = baseAmount,
            ["held"] = held,
            ["costs"] = costPayload,
            ["split_fraction"] = splitFraction,
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("research/fx-paths", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<FxPathsResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new FxPathsResponse { Ok = false, Error = "Empty FX-path payload." };
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

    public async Task<JsonElement> PostJsonAsync(
        string relativePath,
        object body,
        CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync(relativePath, content, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var clone = doc.RootElement.Clone();
        if (!response.IsSuccessStatusCode
            && response.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity)
        {
            response.EnsureSuccessStatusCode();
        }

        return clone;
    }

    public Task<JsonElement> RunMaBacktestAsync(
        string symbol,
        string start,
        string end,
        int fast = 5,
        int slow = 20,
        string adjustment = "raw",
        string interval = "daily",
        object? costs = null,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/backtest-ma",
            new { symbol, start, end, fast, slow, adjustment, interval, costs },
            cancellationToken);

    public Task<JsonElement> CompareSymbolsAsync(
        IEnumerable<string> symbols,
        string start,
        string end,
        string adjustment = "raw",
        string interval = "daily",
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/compare",
            new { symbols = symbols.ToArray(), start, end, adjustment, interval },
            cancellationToken);

    public Task<JsonElement> ExportReportAsync(
        string artifactRoot,
        string title,
        string? symbol,
        IEnumerable<object> sections,
        string? filename = null,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/export-report",
            new
            {
                artifact_root = artifactRoot,
                title,
                symbol,
                sections = sections.ToArray(),
                filename,
            },
            cancellationToken);

    public Task<JsonElement> ExportParquetAsync(
        string symbol,
        string start,
        string end,
        string adjustment = "raw",
        string interval = "daily",
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "market/export-parquet",
            new { symbol, start, end, adjustment, interval },
            cancellationToken);

    public Task<JsonElement> GridSuggestAsync(
        string symbol,
        string start,
        string end,
        string adjustment = "raw",
        string interval = "daily",
        int? lookback = null,
        int? gridCount = null,
        double orderCash = 1000,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/grid-suggest",
            new
            {
                symbol,
                start,
                end,
                adjustment,
                interval,
                lookback,
                grid_count = gridCount,
                order_cash = orderCash,
            },
            cancellationToken);

    public Task<JsonElement> GridBacktestAsync(
        string symbol,
        string start,
        string end,
        double lower,
        double upper,
        int gridCount,
        double orderCash = 1000,
        double? initialCash = null,
        string adjustment = "raw",
        string interval = "daily",
        object? costs = null,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/grid-backtest",
            new
            {
                symbol,
                start,
                end,
                lower,
                upper,
                grid_count = gridCount,
                order_cash = orderCash,
                initial_cash = initialCash,
                adjustment,
                interval,
                costs,
            },
            cancellationToken);

    public async Task<JsonElement> GridListPlansAsync(
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var path = $"research/grid/plans?active_only={(activeOnly ? "1" : "0")}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var clone = doc.RootElement.Clone();
        response.EnsureSuccessStatusCode();
        return clone;
    }

    public Task<JsonElement> GridCreatePlanAsync(
        string symbol,
        double lower,
        double upper,
        int gridCount,
        double orderCash = 1000,
        string? name = null,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/grid/plans",
            new
            {
                symbol,
                lower,
                upper,
                grid_count = gridCount,
                order_cash = orderCash,
                name,
                note,
            },
            cancellationToken);

    public Task<JsonElement> GridAddFillAsync(
        string planId,
        string side,
        double price,
        double qty,
        double fee = 0,
        double? gridLevel = null,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            "research/grid/fills",
            new
            {
                plan_id = planId,
                side,
                price,
                qty,
                fee,
                grid_level = gridLevel,
                note,
            },
            cancellationToken);

    public async Task<JsonElement> GridSnapshotAsync(
        string planId,
        double? lastPrice = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"research/grid/plans/{Uri.EscapeDataString(planId)}/snapshot";
        if (lastPrice is not null)
        {
            path += $"?last_price={lastPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var clone = doc.RootElement.Clone();
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }

        return clone;
    }

    public Task<JsonElement> GridDeactivatePlanAsync(
        string planId,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync(
            $"research/grid/plans/{Uri.EscapeDataString(planId)}/deactivate",
            new { },
            cancellationToken);

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

public sealed class ValuationResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("readings")]
    public List<MetricReadingDto> Readings { get; set; } = new();

    /// <summary>How the multiples were obtained — empty when they came from a vendor series.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>True when PE/PB were computed here from price and per-share fundamentals.</summary>
    [JsonPropertyName("reconstructed")]
    public bool Reconstructed { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class MetricReadingDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("current")]
    public decimal? Current { get; set; }

    [JsonPropertyName("percentile")]
    public decimal? Percentile { get; set; }

    [JsonPropertyName("band")]
    public BandDto? Band { get; set; }

    [JsonPropertyName("explanation")]
    public Dictionary<string, string> Explanation { get; set; } = new();

    [JsonPropertyName("outcome_summaries")]
    public List<string> OutcomeSummaries { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

public sealed class BandDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class FxCarryResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("base_currency")]
    public string BaseCurrency { get; set; } = "";

    [JsonPropertyName("legs")]
    public List<CarryLegDto> Legs { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    [JsonPropertyName("disclaimer")]
    public string Disclaimer { get; set; } = "";
}

/// <summary>Cost inputs for one exchange path. Null fields mean *unknown*, never free.</summary>
public sealed record FxPathRequestCost(
    decimal? SpreadPercent = null,
    decimal? FixedFee = null,
    string? FeeCurrency = null,
    decimal? FeePercent = null,
    decimal? SlippagePercent = null);

public sealed class FxPathsResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("base_currency")]
    public string BaseCurrency { get; set; } = "";

    [JsonPropertyName("target_currency")]
    public string TargetCurrency { get; set; } = "";

    [JsonPropertyName("base_amount")]
    public decimal BaseAmount { get; set; }

    [JsonPropertyName("held")]
    public FxHeldDto? Held { get; set; }

    [JsonPropertyName("quotes")]
    public Dictionary<string, FxQuoteInfoDto> Quotes { get; set; } = new();

    [JsonPropertyName("implied_cross")]
    public FxQuoteInfoDto? ImpliedCross { get; set; }

    [JsonPropertyName("paths")]
    public List<FxPathDto> Paths { get; set; } = new();

    [JsonPropertyName("best_path")]
    public string? BestPath { get; set; }

    /// <summary>Percent units: 3.011 means +3.011%. Null when not computable.</summary>
    [JsonPropertyName("held_advantage_gross_pct")]
    public decimal? HeldAdvantageGrossPct { get; set; }

    [JsonPropertyName("held_advantage_net_pct")]
    public decimal? HeldAdvantageNetPct { get; set; }

    /// <summary>Held-currency units per 1 base at which the two main paths tie (gross basis).</summary>
    [JsonPropertyName("breakeven_held_rate")]
    public decimal? BreakevenHeldRate { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    [JsonPropertyName("disclaimer")]
    public string Disclaimer { get; set; } = "";
}

public sealed class FxHeldDto
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("cost_in_base")]
    public decimal CostInBase { get; set; }

    [JsonPropertyName("implied_rate")]
    public decimal ImpliedRate { get; set; }
}

/// <summary>A quote with its direction, source and date stated — never a bare number.</summary>
public sealed class FxQuoteInfoDto
{
    [JsonPropertyName("pair")]
    public string Pair { get; set; } = "";

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "";

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("as_of")]
    public string AsOf { get; set; } = "";

    [JsonPropertyName("provenance")]
    public Dictionary<string, string> Provenance { get; set; } = new();
}

public sealed class FxPathDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>ok | gross_only | missing_quote | no_exchange.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("starting_asset")]
    public string StartingAsset { get; set; } = "";

    [JsonPropertyName("opportunity_cost")]
    public string OpportunityCost { get; set; } = "";

    [JsonPropertyName("legs")]
    public List<FxPathLegDto> Legs { get; set; } = new();

    [JsonPropertyName("gross_amount")]
    public decimal? GrossAmount { get; set; }

    [JsonPropertyName("cost_detail")]
    public Dictionary<string, JsonElement>? CostDetail { get; set; }

    [JsonPropertyName("net_amount")]
    public decimal? NetAmount { get; set; }

    [JsonPropertyName("net_per_base_amount")]
    public decimal? NetPerBaseAmount { get; set; }

    /// <summary>Distance to the best comparable path; basis states net or gross.</summary>
    [JsonPropertyName("diff_vs_best")]
    public FxPathDiffDto? DiffVsBest { get; set; }

    [JsonPropertyName("formula")]
    public string Formula { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

public sealed class FxPathDiffDto
{
    [JsonPropertyName("best_path")]
    public string BestPath { get; set; } = "";

    /// <summary>net | gross — the amount the difference was computed on.</summary>
    [JsonPropertyName("basis")]
    public string Basis { get; set; } = "";

    [JsonPropertyName("abs")]
    public decimal Abs { get; set; }

    [JsonPropertyName("abs_in_target")]
    public string AbsInTarget { get; set; } = "";

    /// <summary>Percent of the best path's value; negative means behind.</summary>
    [JsonPropertyName("pct")]
    public decimal Pct { get; set; }
}

public sealed class FxPathLegDto
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "";
}

public sealed class CarryLegDto
{
    [JsonPropertyName("pair")]
    public string Pair { get; set; } = "";

    [JsonPropertyName("base_rate")]
    public decimal? BaseRate { get; set; }

    [JsonPropertyName("quote_rate")]
    public decimal? QuoteRate { get; set; }

    [JsonPropertyName("carry")]
    public decimal? Carry { get; set; }

    [JsonPropertyName("rate")]
    public decimal? Rate { get; set; }

    [JsonPropertyName("percentile")]
    public decimal? Percentile { get; set; }

    [JsonPropertyName("low")]
    public decimal? Low { get; set; }

    [JsonPropertyName("high")]
    public decimal? High { get; set; }

    [JsonPropertyName("base_rate_info")]
    public PolicyRateDto? BaseRateInfo { get; set; }

    [JsonPropertyName("quote_rate_info")]
    public PolicyRateDto? QuoteRateInfo { get; set; }

    /// <summary>True when either rate could not be refreshed, so the gap may not be today's.</summary>
    [JsonPropertyName("rates_stale")]
    public bool RatesStale { get; set; }
}

/// <summary>A central-bank rate with the source and date it came from.</summary>
public sealed class PolicyRateDto
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("as_of")]
    public string? AsOf { get; set; }

    [JsonPropertyName("stale")]
    public bool Stale { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

public sealed class MarketQuoteResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("data")]
    public MarketQuoteDto? Data { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class MarketQuoteDto
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("as_of")]
    public string AsOf { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    /// <summary>How many sources agreed on a currency and were compared; 0 when not cross-checked.</summary>
    [JsonPropertyName("source_count")]
    public int SourceCount { get; set; }

    /// <summary>Percent spread between the highest and lowest source price.</summary>
    [JsonPropertyName("spread_pct")]
    public decimal SpreadPercent { get; set; }

    /// <summary>Each source, its price and its deviation, e.g. <c>akshare=10(-9.1%);yfinance=12(+9.1%)</c>.</summary>
    [JsonPropertyName("sources")]
    public string Sources { get; set; } = "";

    /// <summary>Sources whose price deviates from the median by more than the tolerance.</summary>
    [JsonPropertyName("outliers")]
    public string Outliers { get; set; } = "";
}

public sealed class MarketNavResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("data")]
    public List<MarketNavPointDto>? Data { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class MarketNavPointDto
{
    [JsonPropertyName("as_of")]
    public string AsOf { get; set; } = "";

    [JsonPropertyName("nav")]
    public decimal Nav { get; set; }

    [JsonPropertyName("accumulated_nav")]
    public decimal? AccumulatedNav { get; set; }

    [JsonPropertyName("daily_return")]
    public decimal? DailyReturn { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";
}

public sealed class MarketFxResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("data")]
    public MarketFxDto? Data { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class MarketFxDto
{
    [JsonPropertyName("base_currency")]
    public string BaseCurrency { get; set; } = "";

    [JsonPropertyName("quote_currency")]
    public string QuoteCurrency { get; set; } = "";

    [JsonPropertyName("rate")]
    public decimal Rate { get; set; }

    [JsonPropertyName("as_of")]
    public string AsOf { get; set; } = "";

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
