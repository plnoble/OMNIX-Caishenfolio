using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caishenfolio.Host.MarketData;

/// <summary>
/// Local planned buy/sell levels + actual fills (not broker orders).
/// Stored under State root.
/// </summary>
public sealed class PricePlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public PricePlanStore(string stateRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootDirectory);
        Directory.CreateDirectory(stateRootDirectory);
        _filePath = Path.Combine(Path.GetFullPath(stateRootDirectory), "price_plan.json");
    }

    public string FilePath => _filePath;

    public PricePlanDocument Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return new PricePlanDocument();
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<PricePlanDocument>(json, JsonOptions)
                    ?? new PricePlanDocument();
            }
            catch
            {
                return new PricePlanDocument();
            }
        }
    }

    public void Save(PricePlanDocument doc)
    {
        lock (_gate)
        {
            doc.Levels ??= new List<PlannedPriceLevel>();
            doc.Fills ??= new List<ActualFill>();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(doc, JsonOptions));
        }
    }

    public IReadOnlyList<PlannedPriceLevel> ListLevels(string symbol, bool activeOnly = true)
    {
        var sym = symbol.Trim();
        return Load().Levels
            .Where(l => string.Equals(l.Symbol, sym, StringComparison.OrdinalIgnoreCase))
            .Where(l => !activeOnly || l.Active)
            .OrderBy(l => l.Side)
            .ThenBy(l => l.Price)
            .ToList();
    }

    public IReadOnlyList<ActualFill> ListFills(string symbol)
    {
        var sym = symbol.Trim();
        return Load().Fills
            .Where(f => string.Equals(f.Symbol, sym, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Ts)
            .ToList();
    }

    public PlannedPriceLevel AddLevel(string symbol, string side, double price, string? note = null)
    {
        side = NormalizeSide(side);
        if (price <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "价格必须为正。");
        }

        var doc = Load();
        var level = new PlannedPriceLevel
        {
            Id = "lvl_" + Guid.NewGuid().ToString("N")[..12],
            Symbol = symbol.Trim(),
            Side = side,
            Price = price,
            Note = note ?? "",
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        doc.Levels.Add(level);
        Save(doc);
        return level;
    }

    public bool DeactivateLevel(string id)
    {
        var doc = Load();
        var level = doc.Levels.FirstOrDefault(l => l.Id == id);
        if (level is null)
        {
            return false;
        }

        level.Active = false;
        Save(doc);
        return true;
    }

    public bool RemoveLevel(string id)
    {
        var doc = Load();
        var n = doc.Levels.RemoveAll(l => l.Id == id);
        if (n > 0)
        {
            Save(doc);
        }

        return n > 0;
    }

    public ActualFill AddFill(
        string symbol,
        string side,
        double price,
        double qty,
        double fee = 0,
        string? note = null,
        string? ts = null)
    {
        side = NormalizeSide(side);
        if (price <= 0 || qty <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "价格与数量必须为正。");
        }

        var doc = Load();
        var fill = new ActualFill
        {
            Id = "fill_" + Guid.NewGuid().ToString("N")[..12],
            Symbol = symbol.Trim(),
            Side = side,
            Price = price,
            Qty = qty,
            Fee = Math.Max(0, fee),
            Note = note ?? "",
            Ts = string.IsNullOrWhiteSpace(ts) ? DateTimeOffset.UtcNow.ToString("O") : ts!,
        };
        doc.Fills.Add(fill);
        Save(doc);
        return fill;
    }

    public bool RemoveFill(string id)
    {
        var doc = Load();
        var n = doc.Fills.RemoveAll(f => f.Id == id);
        if (n > 0)
        {
            Save(doc);
        }

        return n > 0;
    }

    /// <summary>FIFO open position summary for a symbol.</summary>
    public FillSnapshot Snapshot(string symbol, double? lastPrice = null)
    {
        var fills = ListFills(symbol).OrderBy(f => f.Ts).ToList();
        var lots = new List<(double Price, double Qty)>();
        double realized = 0;
        double fees = 0;
        foreach (var f in fills)
        {
            fees += f.Fee;
            if (f.Side == "buy")
            {
                lots.Add((f.Price, f.Qty));
            }
            else
            {
                var remain = f.Qty;
                while (remain > 1e-12 && lots.Count > 0)
                {
                    var (px, q) = lots[0];
                    var take = Math.Min(remain, q);
                    realized += take * (f.Price - px);
                    var left = q - take;
                    remain -= take;
                    if (left <= 1e-12)
                    {
                        lots.RemoveAt(0);
                    }
                    else
                    {
                        lots[0] = (px, left);
                    }
                }
            }
        }

        var openQty = lots.Sum(l => l.Qty);
        var openCost = lots.Sum(l => l.Price * l.Qty);
        double? avg = openQty > 1e-12 ? openCost / openQty : null;
        double? unreal = null;
        if (lastPrice is not null && avg is not null && openQty > 1e-12)
        {
            unreal = openQty * (lastPrice.Value - avg.Value);
        }

        return new FillSnapshot
        {
            Symbol = symbol.Trim(),
            RealizedPnl = realized,
            UnrealizedPnl = unreal,
            OpenQty = openQty,
            AvgCost = avg,
            Fees = fees,
            FillCount = fills.Count,
            LastPrice = lastPrice,
        };
    }

    private static string NormalizeSide(string side)
    {
        var s = (side ?? "").Trim().ToLowerInvariant();
        if (s is "buy" or "b" or "买" or "买入")
        {
            return "buy";
        }

        if (s is "sell" or "s" or "卖" or "卖出")
        {
            return "sell";
        }

        throw new ArgumentException("side 必须是 buy 或 sell。");
    }
}

public sealed class PricePlanDocument
{
    public List<PlannedPriceLevel> Levels { get; set; } = new();
    public List<ActualFill> Fills { get; set; } = new();
}

public sealed class PlannedPriceLevel
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    /// <summary>buy or sell</summary>
    public string Side { get; set; } = "buy";
    public double Price { get; set; }
    public string Note { get; set; } = "";
    public bool Active { get; set; } = true;
    public string CreatedAt { get; set; } = "";
}

public sealed class ActualFill
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "buy";
    public double Price { get; set; }
    public double Qty { get; set; }
    public double Fee { get; set; }
    public string Note { get; set; } = "";
    public string Ts { get; set; } = "";
}

public sealed class FillSnapshot
{
    public string Symbol { get; set; } = "";
    public double RealizedPnl { get; set; }
    public double? UnrealizedPnl { get; set; }
    public double OpenQty { get; set; }
    public double? AvgCost { get; set; }
    public double Fees { get; set; }
    public int FillCount { get; set; }
    public double? LastPrice { get; set; }
}

/// <summary>Overlay item for candle chart.</summary>
public sealed class ChartPriceMarker
{
    /// <summary>plan_buy | plan_sell | fill_buy | fill_sell</summary>
    public string Kind { get; init; } = "plan_buy";
    public double Price { get; init; }
    public string Label { get; init; } = "";
    public string Id { get; init; } = "";
}
