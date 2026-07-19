using Caishenfolio.Host.MarketData;

namespace Caishenfolio.Host.Tests;

public class WatchlistStoreTests
{
    [Fact]
    public void AddAndRemove_Persists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "caishenfolio-tests", $"wl-{Guid.NewGuid():N}");
        try
        {
            var store = new WatchlistStore(dir);
            store.Add(new WatchlistItem
            {
                Symbol = "SSE:600000",
                Name = "浦发银行",
                MarketLabel = "A股",
            });
            store.Add(new WatchlistItem
            {
                Symbol = "NASDAQ:AAPL",
                Name = "Apple",
                MarketLabel = "美股",
            });

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, x => x.Symbol == "SSE:600000");

            store.Remove("SSE:600000");
            loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal("NASDAQ:AAPL", loaded[0].Symbol);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
