using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

public class PortfolioSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_settings_tests", Guid.NewGuid().ToString("N"));

    private PortfolioStore NewStore() => PortfolioStore.UnderStateRoot(_root);

    [Fact]
    public void AFreshLedgerUsesDefaults()
    {
        using var store = NewStore();
        var settings = store.LoadSettings();

        Assert.Equal("CNY", settings.BaseCurrency);
        Assert.Equal(0.20m, settings.Thresholds.SinglePosition);
        Assert.Empty(settings.TargetAssetAllocation);
        Assert.True(settings.TargetsAreCoherent);
    }

    [Fact]
    public void RoundTripsEveryPreference()
    {
        using var store = NewStore();
        store.SaveSettings(new PortfolioSettings
        {
            BaseCurrency = "usd",
            Thresholds = new RiskThresholds
            {
                SinglePosition = 0.15m,
                AssetClass = 0.5m,
                Region = 0.65m,
                Currency = 0.75m,
                Cash = 0.3m,
            },
            TargetAssetAllocation = new Dictionary<string, decimal>
            {
                ["equity"] = 0.6m,
                ["bond"] = 0.3m,
                ["cash"] = 0.1m,
            },
        });

        var loaded = store.LoadSettings();

        Assert.Equal("USD", loaded.BaseCurrency);
        Assert.Equal(0.15m, loaded.Thresholds.SinglePosition);
        Assert.Equal(0.3m, loaded.Thresholds.Cash);
        Assert.Equal(3, loaded.TargetAssetAllocation.Count);
        Assert.Equal(0.6m, loaded.TargetAssetAllocation["equity"]);
        Assert.True(loaded.TargetsAreCoherent);
    }

    [Fact]
    public void RemovingAnAssetClassFromTheTargetMixActuallyRemovesIt()
    {
        using var store = NewStore();
        store.SaveSettings(new PortfolioSettings
        {
            TargetAssetAllocation = new Dictionary<string, decimal> { ["equity"] = 0.5m, ["bond"] = 0.5m },
        });

        store.SaveSettings(new PortfolioSettings
        {
            TargetAssetAllocation = new Dictionary<string, decimal> { ["equity"] = 1m },
        });

        var loaded = store.LoadSettings();
        Assert.Equal(["equity"], loaded.TargetAssetAllocation.Keys);
    }

    [Fact]
    public void TargetsMustAddUpToOneHundredPercent()
    {
        using var store = NewStore();
        var lopsided = new PortfolioSettings
        {
            TargetAssetAllocation = new Dictionary<string, decimal> { ["equity"] = 0.5m, ["bond"] = 0.2m },
        };

        var error = Assert.Throws<LedgerException>(() => store.SaveSettings(lopsided));
        Assert.Contains("必须正好 100%", error.Message);
        Assert.Empty(store.LoadSettings().TargetAssetAllocation);
    }

    [Fact]
    public void AnExplicitZeroTargetIsKeptAsAStatement()
    {
        // P1-R08: an explicit 0% is "hold none of this", not an omission — dropping it made
        // the rebalance analyzer refuse to plan the liquidation.
        using var store = NewStore();
        var saved = store.SaveSettings(new PortfolioSettings
        {
            TargetAssetAllocation = new Dictionary<string, decimal>
            {
                ["equity"] = 1m,
                ["bond"] = 0m,
            },
        });

        Assert.Equal(["bond", "equity"], saved.TargetAssetAllocation.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(0m, saved.TargetAssetAllocation["bond"]);
        Assert.True(saved.TargetsAreCoherent); // 1 + 0 still sums to 100%
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void ThresholdsMustBeAFraction(decimal value)
    {
        using var store = NewStore();
        var settings = new PortfolioSettings
        {
            Thresholds = RiskThresholds.Default with { SinglePosition = value },
        };

        Assert.Throws<LedgerException>(() => store.SaveSettings(settings));
    }

    [Fact]
    public void RejectsUnknownCurrencyAndAssetClass()
    {
        using var store = NewStore();

        Assert.Throws<ArgumentException>(() =>
            store.SaveSettings(new PortfolioSettings { BaseCurrency = "XYZ" }));

        Assert.Throws<LedgerException>(() => store.SaveSettings(new PortfolioSettings
        {
            TargetAssetAllocation = new Dictionary<string, decimal> { ["nonsense"] = 1m },
        }));
    }

    [Fact]
    public void AcceptsLegacyAssetNamesAndNormalizesThem()
    {
        using var store = NewStore();
        var saved = store.SaveSettings(new PortfolioSettings
        {
            // "fund" is the pre-split name for an off-exchange open-end fund.
            TargetAssetAllocation = new Dictionary<string, decimal> { ["fund"] = 1m },
        });

        Assert.Equal(["mutual_fund"], saved.TargetAssetAllocation.Keys);
    }

    [Fact]
    public async Task WorkspacePicksUpStoredPreferencesAndDrivesRiskWithThem()
    {
        using var store = NewStore();
        store.SaveSettings(new PortfolioSettings
        {
            BaseCurrency = "CNY",
            Thresholds = new RiskThresholds
            {
                SinglePosition = 1m,
                AssetClass = 1m,
                Region = 1m,
                Currency = 1m,
                Cash = 1m,
            },
            TargetAssetAllocation = new Dictionary<string, decimal> { ["equity"] = 0.7m, ["cash"] = 0.3m },
        });

        var workspace = new PortfolioWorkspace(store);
        Assert.Equal("CNY", workspace.BaseCurrency);
        Assert.Equal(1m, workspace.Settings.Thresholds.SinglePosition);

        var day = new DateOnly(2026, 1, 5);
        workspace.Record(LedgerTransaction.OpeningCash("acct", day, 100_000m, "CNY"));

        var snapshot = await workspace.RefreshAsync(new DateOnly(2026, 7, 31));

        // The relaxed ceilings mean 100% cash raises no concentration finding...
        Assert.Empty(snapshot.Risk.Findings);
        // ...but it is still 100% against a 30% cash target.
        Assert.Contains(snapshot.Risk.Drift, d => d.Key == "cash");
    }

    [Fact]
    public void ApplySettingsPersistsAndAdopts()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);

        workspace.ApplySettings(new PortfolioSettings { BaseCurrency = "HKD" });

        Assert.Equal("HKD", workspace.BaseCurrency);
        Assert.Equal("HKD", store.LoadSettings().BaseCurrency);

        // An invalid change is refused and leaves the stored value alone.
        Assert.Throws<LedgerException>(() => workspace.ApplySettings(new PortfolioSettings
        {
            BaseCurrency = "HKD",
            TargetAssetAllocation = new Dictionary<string, decimal> { ["equity"] = 0.4m },
        }));
        Assert.Equal("HKD", store.LoadSettings().BaseCurrency);
    }

    [Fact]
    public void ExplicitCurrencyArgumentOverridesStoredPreference()
    {
        using var store = NewStore();
        store.SaveSettings(new PortfolioSettings { BaseCurrency = "CNY" });

        Assert.Equal("USD", new PortfolioWorkspace(store, baseCurrency: "USD").BaseCurrency);
        Assert.Equal("CNY", new PortfolioWorkspace(store).BaseCurrency);
    }

    [Fact]
    public async Task TwrNeedsTwoValuationPointsAndThenLinksThem()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        workspace.Record(LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 100_000m, "CNY"));

        var first = await workspace.RefreshAsync(new DateOnly(2026, 1, 5));
        // One point in history: TWR is honestly null, not a number made from a single day.
        Assert.Null(first.Twr);
        Assert.NotNull(first.Xirr);

        // A second valuation day (value unchanged: 100k cash) makes the curve linkable.
        // The opening cash sits ON the first point, so it must not be double-counted —
        // before that fix a flat ledger reported -50%.
        var second = await workspace.RefreshAsync(new DateOnly(2026, 2, 5));
        Assert.NotNull(second.Twr);
        Assert.Equal(0.0, second.Twr!.Value, 6);
    }

    [Fact]
    public async Task TwrCountsAnInterimDepositAsAFlowNotAGain()
    {
        using var store = NewStore();
        var workspace = new PortfolioWorkspace(store);
        workspace.Record(LedgerTransaction.OpeningCash("acct", new DateOnly(2026, 1, 5), 100_000m, "CNY"));
        await workspace.RefreshAsync(new DateOnly(2026, 1, 5));

        // A deposit between the two points is money in, not a return.
        workspace.Record(LedgerTransaction.Deposit("acct", new DateOnly(2026, 1, 20), 50_000m, "CNY"));
        var second = await workspace.RefreshAsync(new DateOnly(2026, 2, 5));

        Assert.NotNull(second.Twr);
        Assert.Equal(0.0, second.Twr!.Value, 6);
    }

    // --- novice profile (N1) -------------------------------------------------------

    [Fact]
    public void RoundTripsTheNoviceProfile()
    {
        using var store = NewStore();
        store.SaveSettings(new PortfolioSettings
        {
            UiMode = UiMode.Professional,
            OnboardingCompleted = true,
            Goal = "  十年内攒够首付  ",
            HorizonYears = 10m,
            LiquidityNeed = " AnyTime ",
            EmergencyFundAmount = 60_000m,
            MaxDrawdownTolerance = 0.3m,
            WatchMarkets = ["hk", "CN", "jp"],
        });

        var loaded = store.LoadSettings();

        Assert.Equal(UiMode.Professional, loaded.UiMode);
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal("十年内攒够首付", loaded.Goal);
        Assert.Equal(10m, loaded.HorizonYears);
        Assert.Equal("anytime", loaded.LiquidityNeed);
        Assert.Equal(60_000m, loaded.EmergencyFundAmount);
        Assert.Equal(0.3m, loaded.MaxDrawdownTolerance);
        // Region codes normalize to their canonical form, deduplicated, sorted.
        Assert.Equal(["cn", "hk", "jp"], loaded.WatchMarkets.ToArray());
    }

    [Fact]
    public void AnOlderSettingsRowReadsAsNotStated()
    {
        using var store = NewStore();
        // No novice keys at all — the pre-N1 settings layout.
        store.SaveRawSetting("base_currency", "JPY");
        store.SaveRawSetting("risk.single_position", "0.2");

        var loaded = store.LoadSettings();

        Assert.Equal(UiMode.Novice, loaded.UiMode);
        Assert.False(loaded.OnboardingCompleted);
        Assert.Equal("", loaded.Goal);
        Assert.Null(loaded.HorizonYears);
        Assert.Equal("", loaded.LiquidityNeed);
        Assert.Equal(0m, loaded.EmergencyFundAmount);
        Assert.Equal(0m, loaded.MaxDrawdownTolerance);
        Assert.Empty(loaded.WatchMarkets);
        // What the old row did carry still loads.
        Assert.Equal("JPY", loaded.BaseCurrency);
    }

    [Fact]
    public void AnUnknownLiquidityCodeIsRejectedNotGuessed()
    {
        using var store = NewStore();

        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { LiquidityNeed = "whenever" }));
        // A garbage value written by an older/foreign row reads as not stated, not as a crash.
        store.SaveRawSetting("novice.liquidity", "garbage");
        Assert.Equal("", store.LoadSettings().LiquidityNeed);
    }

    [Fact]
    public void NoviceProfileValidatesItsOwnInputs()
    {
        using var store = NewStore();

        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { HorizonYears = -1m }));
        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { EmergencyFundAmount = -5m }));
        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { MaxDrawdownTolerance = 1.5m }));
        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { LiquidityNeed = "whenever" }));
        Assert.Throws<LedgerException>(() =>
            store.SaveSettings(new PortfolioSettings { WatchMarkets = ["xx"] }));

        // Nothing above may have landed.
        Assert.False(store.LoadSettings().OnboardingCompleted);
        Assert.Null(store.LoadSettings().HorizonYears);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
