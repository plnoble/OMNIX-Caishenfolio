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
