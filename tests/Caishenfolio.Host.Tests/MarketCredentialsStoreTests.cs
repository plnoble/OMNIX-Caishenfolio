using Caishenfolio.Host.MarketData;

namespace Caishenfolio.Host.Tests;

public class MarketCredentialsStoreTests
{
    [Fact]
    public void SaveLoad_RoundTrip_DoesNotLoseKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "caishenfolio-tests", $"cred-{Guid.NewGuid():N}");
        try
        {
            var store = new MarketCredentialsStore(dir);
            var customCache = Path.Combine(dir, "custom", "bars_cache.db");
            store.Save(new MarketCredentials
            {
                MarketProvider = "tushare",
                HttpTrustEnv = false,
                TushareToken = "tok_demo_123",
                AlphavantageApiKey = "av_demo_456",
                BarsCachePath = customCache,
                BarsCacheMaxMb = "256",
            });

            var loaded = store.Load();
            Assert.Equal("tushare", loaded.MarketProvider);
            Assert.False(loaded.HttpTrustEnv);
            Assert.Equal("tok_demo_123", loaded.TushareToken);
            Assert.Equal("av_demo_456", loaded.AlphavantageApiKey);
            Assert.Equal(customCache, loaded.BarsCachePath);

            var env = new Dictionary<string, string?>();
            store.ApplyToEnvironment(env);
            Assert.Equal("tushare", env["CAISHENFOLIO_MARKET_PROVIDER"]);
            Assert.Equal("0", env["CAISHENFOLIO_HTTP_TRUST_ENV"]);
            Assert.Equal("tok_demo_123", env["CAISHENFOLIO_TUSHARE_TOKEN"]);
            Assert.Equal("av_demo_456", env["CAISHENFOLIO_ALPHAVANTAGE_API_KEY"]);
            Assert.Equal(store.FilePath, env["CAISHENFOLIO_CREDENTIALS_PATH"]);
            Assert.Equal(Path.GetFullPath(customCache), env["CAISHENFOLIO_BARS_CACHE_PATH"]);
            Assert.Equal("256", env["CAISHENFOLIO_BARS_CACHE_MAX_MB"]);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
