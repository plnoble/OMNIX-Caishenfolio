using System.Net;
using System.Text;
using System.Text.Json;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Host.Tests;

public class AnalyticsCoreClientTests
{
    [Fact]
    public async Task GetHealthAsync_ParsesPayload()
    {
        var json = """
            {"status":"ok","product":"OMNIX-Caishenfolio","version":"0.7.0","phase":"P4","disclaimer":"研究/模拟结论，非投资建议。","live_trading_enabled":false,"market_provider":"akshare","market_provider_ready":true,"market_data_synthetic":false}
            """;
        using var http = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/"),
        };
        using var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:8765/"), http);

        var health = await client.GetHealthAsync();

        Assert.Equal("ok", health.Status);
        Assert.Equal("OMNIX-Caishenfolio", health.Product);
        Assert.Equal("P4", health.Phase);
        Assert.False(health.LiveTradingEnabled);
        Assert.Equal("akshare", health.MarketProvider);
        Assert.True(health.MarketProviderReady);
        Assert.False(health.MarketDataSynthetic);
    }

    [Fact]
    public async Task SearchSymbolsAsync_ParsesItems()
    {
        var json = """
            {"items":[{"symbol":"NASDAQ:AAPL","market":"us","asset_class":"equity","name":"Apple","provider":"fixture"}],"provider":"fixture"}
            """;
        using var http = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/"),
        };
        using var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:8765/"), http);

        var result = await client.SearchSymbolsAsync("AAPL");
        Assert.Equal("fixture", result.Provider);
        Assert.Single(result.Items);
        Assert.Equal("NASDAQ:AAPL", result.Items[0].Symbol);
    }

    [Fact]
    public async Task GetMarketBarsAsync_ParsesBars()
    {
        var json = """
            {"ok":true,"provider":"fixture","data":[{"timestamp_utc":"2024-01-02T00:00:00+00:00","open":10,"high":11,"low":9,"close":10.5,"volume":1000,"currency":"CNY"}],"warnings":["fixture_synthetic_data"],"error":null}
            """;
        using var http = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/"),
        };
        using var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:8765/"), http);

        var result = await client.GetMarketBarsAsync("SSE:600000", "2024-01-02", "2024-01-05");
        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data!);
        Assert.Equal(10.5m, result.Data![0].Close);
    }

    [Fact]
    public async Task GetMarketBarsAsync_AcceptsFractionalVolumeFromRealFeeds()
    {
        // AkShare / pandas often serializes volume as 1234567.0 (not integer JSON).
        var json = """
            {"ok":true,"provider":"akshare","data":[{"timestamp_utc":"2024-01-02T00:00:00+00:00","open":10.1,"high":11.2,"low":9.8,"close":10.5,"volume":1234567.0,"currency":"CNY"}],"warnings":["real_market_data"],"error":null}
            """;
        using var http = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/"),
        };
        using var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:8765/"), http);

        var result = await client.GetMarketBarsAsync("SSE:600000", "2024-01-02", "2024-01-05");
        Assert.True(result.Ok);
        Assert.Equal(1234567.0m, result.Data![0].Volume);
    }

    [Fact]
    public async Task RunSymbolSnapshotAsync_ParsesResearchPayload()
    {
        var json = """
            {"ok":true,"task":{"id":"task_1","kind":"research","title":"Symbol snapshot SSE:600000","status":"succeeded","summary":"3 bars"},"artifact":{"id":"artifact_1","kind":"research_snapshot","title":"snap","content_type":"application/json","uri_or_payload":"{}"},"summary":{"bar_count":3},"disclaimer":"研究/模拟结论，非投资建议。","error":null}
            """;
        using var http = new HttpClient(new StubHandler(json, HttpStatusCode.OK))
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/"),
        };
        using var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:8765/"), http);

        var result = await client.RunSymbolSnapshotAsync("SSE:600000", "2024-01-02", "2024-01-05");
        Assert.True(result.Ok);
        Assert.Equal("task_1", result.Task?.Id);
        Assert.Equal("research_snapshot", result.Artifact?.Kind);
    }

    [Fact]
    public void Constructor_RejectsNonLoopback()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AnalyticsCoreClient(new Uri("http://192.168.1.10:8765/")));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;

        public StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

// --- P2-03 / 反例 #17：买入卡与卖出卡的请求序列化与端到端响应解析 ---------------------

public class EvidenceCardRequestTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).Result;
            var payload = """
                {
                  "symbol": "SSE:600000",
                  "as_of": "2026-09-01",
                  "side": "sell",
                  "data_quality": {"provider": "fixture", "data_as_of": "2026-09-01",
                                    "bar_count": 10, "quote_sources": 1, "quote_spread_pct": null,
                                    "adjustment": "raw", "missing": [], "conflicts": [], "warnings": [],
                                    "channel_as_of": {"bars": "2026-09-01"}},
                  "valuation": {}, "fundamentals": {}, "market_behaviour": {}, "historical_check": {},
                  "portfolio_impact": null,
                  "evidence_for": [], "evidence_against": [], "unknowns": [],
                  "invalidation": [], "conclusion": {"status": "insufficient", "summary": "s", "reasons": []},
                  "disclaimer": "d"
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task SideFundingAndMaxLossTravelInTheRequest()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8765/") };
        using var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:8765/"), http);

        var element = await client.PostEvidenceCardAsync(
            "SSE:600000", "2026-08-01", "2026-09-01",
            candidateAmount: 5_000m,
            candidateCurrency: "CNY",
            portfolioValue: 100_000m,
            portfolioCurrency: "CNY",
            currentWeightOfSymbol: 0.2m,
            singlePositionLimit: 0.5m,
            cashAvailable: 50_000m,
            horizonDays: 90,
            side: "sell",
            funding: "external",
            fundingCurrency: "CNY",
            // API contract is 0~1 fractions (the UI converts its percent input once, P1-R02).
            maxLossPct: 0.15m,
            priceTolerancePct: 1.5m);

        Assert.Equal("/research/evidence-card", handler.RequestPath);
        var body = handler.RequestBody;
        Assert.NotNull(body);
        using var doc = System.Text.Json.JsonDocument.Parse(body!);

        var root = doc.RootElement;
        Assert.Equal("sell", root.GetProperty("side").GetString());
        Assert.Equal(1.5m, root.GetProperty("price_tolerance_pct").GetDecimal());

        var portfolio = root.GetProperty("portfolio");
        Assert.Equal("external", portfolio.GetProperty("funding").GetString());
        Assert.Equal(0.15m, portfolio.GetProperty("max_loss_pct").GetDecimal());
        Assert.Equal(5_000m, portfolio.GetProperty("candidate_amount").GetDecimal());
        // P1-01: the server's existing-cash currency math needs the funding currency —
        // omitting it made every real request return empty after-weights with a
        // "missing funding currency" note, while direct-to-Python tests passed.
        Assert.Equal("CNY", portfolio.GetProperty("funding_currency").GetString());

        // The response round-trips the side the card was built for.
        Assert.Equal("sell", element.GetProperty("side").GetString());
    }
}

// --- 复审 P1-01：真实 C# → Python 链路的资金币种与权重守恒 ---------------------------

public class EvidenceCardFundingCurrencyChainTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_ecchain", Guid.NewGuid().ToString("N"));

    private static bool Enabled =>
        (Environment.GetEnvironmentVariable("CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS") ?? "")
        .Trim() == "1";

    private static string? FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "python")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// The production chain: HTTP client → Python route → assemble_card. Direct-to-Python
    /// tests injected funding_currency by hand; the request path dropped it, and every real
    /// card showed empty after-weights (recheck P1-01).
    /// </summary>
    [Fact]
    public async Task ExistingCnyCashBuyingUsdConservesCurrencyWeightsThroughTheRealChain()
    {
        if (!Enabled)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio", "state");

        // Same provisioning the background check uses: the app's managed venv, not the
        // system interpreter that happens to be on PATH.
        // The child inherits the host env: fixture keeps the chain offline-deterministic
        // (the broker defaults the provider to "auto" only when the host var is unset).
        Environment.SetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER", "fixture");
        var provisioner = new Caishenfolio.Host.Python.PythonRuntimeProvisioner(
            new Caishenfolio.Host.Python.PythonRuntimeOptions
            {
                StateRoot = stateRoot,
                PythonProjectDirectory = Path.Combine(repoRoot, "python"),
            });
        var runtime = await provisioner.ProvisionAsync();
        Assert.True(runtime.CanRun, runtime.Summary);

        using var broker = new Caishenfolio.Host.Python.AnalyticsCoreProcessBroker(port: 8781);
        broker.Start(runtime.Interpreter!, repoRoot, credentialsStore: null);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8781/") };
            var client = new Caishenfolio.Host.Python.AnalyticsCoreClient(
                new Uri("http://127.0.0.1:8781/"), http);

            // Wait for the server to accept requests before firing the card.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await client.GetHealthAsync().ConfigureAwait(true);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    await Task.Delay(300).ConfigureAwait(true);
                }
            }

            var card = await client.PostEvidenceCardAsync(
                "SSE:600000", "2024-01-01", "2025-12-31",
                candidateAmount: 10_000m,
                candidateCurrency: "CNY",
                portfolioValue: 100_000m,
                portfolioCurrency: "CNY",
                currentWeightOfSymbol: 0.2m,
                singlePositionLimit: 0.5m,
                cashAvailable: 50_000m,
                side: "buy",
                funding: "existing_cash",
                fundingCurrency: "CNY",
                regionWeights: new Dictionary<string, double> { ["cn"] = 0.2, ["us"] = 0.3, ["cash"] = 0.5 },
                currencyWeights: new Dictionary<string, double> { ["CNY"] = 0.6, ["USD"] = 0.4 },
                instrumentRegion: "us",
                instrumentCurrency: "USD").ConfigureAwait(true);

            // The route wraps the card: {ok, task, artifact, card: {...}}.
            var impact = card.GetProperty("card").GetProperty("portfolio_impact");
            var currency = impact.GetProperty("currency_weight_after");

            // CNY 60%→50%, USD 40%→50% — through the real request path, summing to 1.
            Assert.Equal(0.5m, currency.GetProperty("CNY").GetDecimal());
            Assert.Equal(0.5m, currency.GetProperty("USD").GetDecimal());
            var sum = currency.EnumerateObject().Sum(p => p.Value.GetDecimal());
            Assert.Equal(1.0m, sum, 5);
        }
        finally
        {
            broker.Stop();
        }
    }


    /// <summary>
    /// recheck-3 R3-01 acceptance #6: external funding through the REAL chain — the old
    /// position dilutes onto the new denominator (27.2727%, not 29.0909%) and a 28% limit
    /// is NOT tripped.
    /// </summary>
    [Fact]
    public async Task ExternalFundingDilutesTheOldPositionThroughTheRealChain()
    {
        if (!Enabled)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio", "state");
        Environment.SetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER", "fixture");

        var provisioner = new Caishenfolio.Host.Python.PythonRuntimeProvisioner(
            new Caishenfolio.Host.Python.PythonRuntimeOptions
            {
                StateRoot = stateRoot,
                PythonProjectDirectory = Path.Combine(repoRoot, "python"),
            });
        var runtime = await provisioner.ProvisionAsync();
        Assert.True(runtime.CanRun, runtime.Summary);

        using var broker = new Caishenfolio.Host.Python.AnalyticsCoreProcessBroker(port: 8783);
        broker.Start(runtime.Interpreter!, repoRoot, credentialsStore: null);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8783/") };
            var client = new Caishenfolio.Host.Python.AnalyticsCoreClient(
                new Uri("http://127.0.0.1:8783/"), http);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await client.GetHealthAsync().ConfigureAwait(true);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    await Task.Delay(300).ConfigureAwait(true);
                }
            }

            var card = await client.PostEvidenceCardAsync(
                "SSE:600000", "2024-01-01", "2025-12-31",
                candidateAmount: 10_000m,
                candidateCurrency: "CNY",
                portfolioValue: 100_000m,
                portfolioCurrency: "CNY",
                currentWeightOfSymbol: 0.2m,
                singlePositionLimit: 0.28m,
                cashAvailable: 50_000m,
                side: "buy",
                funding: "external",
                fundingCurrency: "CNY",
                regionWeights: new Dictionary<string, double> { ["cn"] = 0.5, ["us"] = 0.2, ["cash"] = 0.3 },
                currencyWeights: new Dictionary<string, double> { ["CNY"] = 0.6, ["USD"] = 0.4 },
                instrumentRegion: "us",
                instrumentCurrency: "USD").ConfigureAwait(true);

            var impact = card.GetProperty("card").GetProperty("portfolio_impact");
            var singleAfter = impact.GetProperty("single_position_weight_after").GetDecimal();

            // 30 000 / 110 000 = 27.2727% — the diluted value, not 29.0909%.
            Assert.Equal(0.272727m, Math.Round(singleAfter, 6));
            // And the 28% limit is NOT breached → no risk_exceeded from this rule.
            var status = card.GetProperty("card").GetProperty("conclusion").GetProperty("status").GetString();
            Assert.NotEqual("risk_exceeded", status);
        }
        finally
        {
            broker.Stop();
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);
}

// --- 复审 P1-03：费用三态经真实 C#→Python 链路 ---------------------------------------

public class FxPathCostChainTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_fxchain", Guid.NewGuid().ToString("N"));

    private static bool Enabled =>
        (Environment.GetEnvironmentVariable("CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS") ?? "")
        .Trim() == "1";

    private static string? FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "python")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static async Task<Caishenfolio.Host.Python.AnalyticsCoreClient> StartClientAsync(
        Caishenfolio.Host.Python.AnalyticsCoreProcessBroker broker, string repoRoot, string stateRoot)
    {
        Environment.SetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER", "fixture");
        var provisioner = new Caishenfolio.Host.Python.PythonRuntimeProvisioner(
            new Caishenfolio.Host.Python.PythonRuntimeOptions
            {
                StateRoot = stateRoot,
                PythonProjectDirectory = Path.Combine(repoRoot, "python"),
            });
        var runtime = await provisioner.ProvisionAsync();
        Assert.True(runtime.CanRun, runtime.Summary);
        broker.Start(runtime.Interpreter!, repoRoot, credentialsStore: null);

        var client = new Caishenfolio.Host.Python.AnalyticsCoreClient(broker.BaseAddress);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.GetHealthAsync().ConfigureAwait(true);
                return client;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(300).ConfigureAwait(true);
            }
        }

        throw new TimeoutException("分析核心 30 秒内未就绪。");
    }

    /// <summary>
    /// Recheck P1-03 completion bar: explicitly "spread 0, fixed fee 1 USD" must produce a
    /// net = gross − 1 through the real request path; the desktop form previously could
    /// never send a fixed fee, so every path stayed gross-only.
    /// </summary>
    [Fact]
    public async Task ExplicitZeroSpreadWithFixedFeeProducesANetFigureThroughTheRealChain()
    {
        if (!Enabled)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio", "state");

        using var broker = new Caishenfolio.Host.Python.AnalyticsCoreProcessBroker(port: 8782);
        var client = await StartClientAsync(broker, repoRoot!, stateRoot).ConfigureAwait(true);
        try
        {
            var costs = new Dictionary<string, FxPathRequestCost>
            {
                ["held_to_target"] = new(SpreadPercent: 0m, FixedFee: 1m, FeeCurrency: "USD"),
                ["base_to_target"] = new(SpreadPercent: 0m, FixedFee: 1m, FeeCurrency: "USD"),
            };

            var response = await client.PostFxPathsAsync(
                "CNY", "USD", 100m,
                heldCurrency: "JPY", heldAmount: 2450m, heldCostInBase: 100m,
                costs: costs).ConfigureAwait(true);

            Assert.True(response.Ok, response.Error ?? "fx-paths failed");

            var held = response.Paths.Single(p => p.Key == "held_to_target");
            // Fixture quote USD/JPY = 150: gross 2450/150 = 16.333333; net = gross − 1 —
            // a real net figure through the full request path (recheck P1-03).
            Assert.NotNull(held.NetAmount);
            Assert.Equal(15.333333m, held.NetAmount!.Value, 5);
            Assert.Equal("ok", held.Status, ignoreCase: true);

            // Only a fixed fee (spread unknown): the same request path stays gross-only.
            var feeOnly = new Dictionary<string, FxPathRequestCost>
            {
                ["held_to_target"] = new(FixedFee: 1m, FeeCurrency: "USD"),
            };
            var grossResponse = await client.PostFxPathsAsync(
                "CNY", "USD", 100m,
                heldCurrency: "JPY", heldAmount: 2450m, heldCostInBase: 100m,
                costs: feeOnly).ConfigureAwait(true);
            var grossHeld = grossResponse.Paths.Single(p => p.Key == "held_to_target");
            Assert.Null(grossHeld.NetAmount);
        }
        finally
        {
            broker.Stop();
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
