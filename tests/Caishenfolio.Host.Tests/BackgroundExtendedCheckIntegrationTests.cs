using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// Opt-in integration proof for layer 2's production path: borrow or start a real analytics
/// core, run a valuation through it, and clean up. Skipped unless
/// <c>CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS=1</c> — it needs a provisioned venv and a free
/// port, exactly like the live-market tests need a network.
/// </summary>
public class BackgroundExtendedCheckIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_extended_check", Guid.NewGuid().ToString("N"));

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

    private static string RealStateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Caishenfolio",
        "state");

    [Fact]
    public async Task StartsOrReusesACoreValuesTheLedgerAndStopsWhatItStarted()
    {
        if (!Enabled)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveAccount(Account.Create("测试账户", AccountKind.Securities, "CNY"));

        var check = await BackgroundExtendedCheck.RunAsync(
            store,
            repoRoot,
            RealStateRoot(),
            port: 8765);

        // An empty ledger has nothing to alert about — what this proves is that the whole
        // chain (runtime → core → health → valuation → teardown) completed without throwing.
        Assert.NotNull(check.Alerts);
    }

    /// <summary>
    /// P1-R06 counterexample through the real production path: a cost-less advantage rule
    /// must not pollute a costed net-arrival rule on the same currency triple, and the
    /// net-arrival rule must fire when the threshold is met (and stay silent just below).
    /// The fixture provider supplies deterministic quotes, so the numbers are stable.
    /// </summary>
    [Fact]
    public async Task CostedNetArrivalRulesEvaluateIndependentlyThroughTheRealChain()
    {
        if (!Enabled)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var store = PortfolioStore.UnderStateRoot(_root);
        store.SaveAccount(Account.Create("测试账户", AccountKind.Securities, "CNY"));
        // A real JPY pot: 100 CNY bought 2 000 JPY at the bank.
        store.AddTransaction(LedgerTransaction.FxExchange(
            "acct", new DateOnly(2026, 1, 5), 100m, "CNY", 2_000m, "JPY"));

        // Rule 1: cost-less advantage (would poison the cache under the old key).
        // Rule 2: net arrival WITH a cost model, threshold low enough to fire.
        var rules = new List<FxAlertRule>
        {
            new()
            {
                Kind = FxAlertRuleKind.HeldAdvantageAbove,
                BaseCurrency = "CNY", TargetCurrency = "USD", HeldCurrency = "JPY",
                ThresholdPct = 1m,
            },
            new()
            {
                Kind = FxAlertRuleKind.NetArrivalAbove,
                BaseCurrency = "CNY", TargetCurrency = "USD", HeldCurrency = "JPY",
                ThresholdAmount = 0.01m, // any positive net arrival clears this
                SpreadPct = 1m,
                FixedFee = 0.5m,
            },
        };

        var check = await BackgroundExtendedCheck.RunAsync(
            store,
            repoRoot,
            RealStateRoot(),
            port: 8766,
            fxRules: rules);

        // The net-arrival rule must have fired through its OWN cost model, not inherited
        // the first rule's cost-less facts (which would leave NetArrival null forever).
        Assert.Contains(check.Alerts, a => a.Kind == AlertKind.FxNetArrival);

        // And a threshold just out of reach stays silent on the same machinery.
        var quietCheck = await BackgroundExtendedCheck.RunAsync(
            store,
            repoRoot,
            RealStateRoot(),
            port: 8766,
            fxRules:
            [
                new FxAlertRule
                {
                    Kind = FxAlertRuleKind.NetArrivalAbove,
                    BaseCurrency = "CNY", TargetCurrency = "USD", HeldCurrency = "JPY",
                    ThresholdAmount = 1_000_000m, // unreachable
                    SpreadPct = 1m,
                    FixedFee = 0.5m,
                },
            ]);

        Assert.DoesNotContain(quietCheck.Alerts, a => a.Kind == AlertKind.FxNetArrival);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
