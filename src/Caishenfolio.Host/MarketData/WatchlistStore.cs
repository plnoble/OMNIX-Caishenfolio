using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caishenfolio.Host.MarketData;

/// <summary>
/// User watchlist under State root (local only, not in git).
/// </summary>
public sealed class WatchlistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public WatchlistStore(string stateRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootDirectory);
        Directory.CreateDirectory(stateRootDirectory);
        _filePath = Path.Combine(Path.GetFullPath(stateRootDirectory), "watchlist.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyList<WatchlistItem> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<WatchlistItem>();
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var items = JsonSerializer.Deserialize<List<WatchlistItem>>(json, JsonOptions);
                return items ?? new List<WatchlistItem>();
            }
            catch
            {
                return Array.Empty<WatchlistItem>();
            }
        }
    }

    public void Save(IEnumerable<WatchlistItem> items)
    {
        lock (_gate)
        {
            var list = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Symbol))
                .GroupBy(i => i.Symbol.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(i => i.MarketLabel)
                .ThenBy(i => i.Symbol)
                .ToList();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list, JsonOptions));
        }
    }

    public IReadOnlyList<WatchlistItem> Add(WatchlistItem item)
    {
        var list = Load().ToList();
        list.RemoveAll(x => string.Equals(x.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, item);
        Save(list);
        return Load();
    }

    public IReadOnlyList<WatchlistItem> Remove(string symbol)
    {
        var list = Load().Where(x => !string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToList();
        Save(list);
        return Load();
    }
}

public sealed class WatchlistItem
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public string Market { get; set; } = "";
    public string MarketLabel { get; set; } = "";
    public string AssetClass { get; set; } = "";
    public string Note { get; set; } = "";
}
