using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Python;
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

    /// <summary>
    /// WP-02 A5–A7 through the REAL client/core pair: the new JSON fields must survive the wire.
    /// A unit test can only prove the two sides agree if it also pins the names, and this is the
    /// one place that would catch a `[JsonPropertyName]` typo (which would silently show "—" on
    /// the page instead of failing anywhere).
    /// </summary>
    [Fact]
    public async Task TheNowAndCriticalFieldsSurviveTheRealClientAndCore()
    {
        if (!Enabled)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        // Start a core the same way the production path does, so this proves the CLIENT/CORE
        // pair (JSON names included), not just the two in-process halves.
        var previousProvider = Environment.GetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER");
        Environment.SetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER", "fixture");
        AnalyticsCoreProcessBroker? broker = null;
        try
        {
            broker = await StartCoreAsync(repoRoot, RealStateRoot(), 8767);
            using var client = new AnalyticsCoreClient(broker.BaseAddress);
            await WaitForHealthAsync(client);

            var response = await client.PostFxPathsAsync(
                "CNY", "USD", baseAmount: 100m,
                heldCurrency: "JPY", heldAmount: 2_000m, heldCostInBase: 100m);

            Assert.True(response.Ok, response.Error);
            // 100 CNY bought 2,000 JPY → 20 JPY per CNY. Fixture quotes: USD/CNY 7.2, USD/JPY 150.
            Assert.NotNull(response.Now);
            Assert.True(response.Now!.CostKnown);
            Assert.Equal("USD", response.Now.TargetCurrency);
            Assert.NotNull(response.Now.Baseline);
            Assert.Equal("USD/CNY", response.Now.Baseline!.QuotePair);
            Assert.False(string.IsNullOrWhiteSpace(response.Now.Baseline.FrozenAt));
            Assert.NotNull(response.Now.PnlVsCostInBase);

            Assert.NotNull(response.FutureCritical);
            Assert.True(response.FutureCritical!.Solvable);
            Assert.Equal("USD/JPY", response.FutureCritical.Pair);
            // T = 20 × 7.2 = 144 JPY per USD.
            Assert.Equal(144m, response.FutureCritical.Rate!.Value, 3);
            Assert.NotNull(response.FutureCritical.RequiredMovePct);
            // The old tie rate is a different number in a different unit and must still be there.
            Assert.NotNull(response.BreakevenHeldRate);
            Assert.NotEqual(response.BreakevenHeldRate!.Value, response.FutureCritical.Rate!.Value);
        }
        finally
        {
            broker?.Dispose();
            Environment.SetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER", previousProvider);
        }
    }

    private static async Task<AnalyticsCoreProcessBroker> StartCoreAsync(
        string coreRoot, string stateRoot, int port)
    {
        var provisioner = new PythonRuntimeProvisioner(new PythonRuntimeOptions
        {
            StateRoot = stateRoot,
            PythonProjectDirectory = Path.Combine(coreRoot, "python"),
        });
        var runtime = await provisioner.ProvisionAsync();
        Assert.True(runtime.CanRun, runtime.Summary);
        var broker = new AnalyticsCoreProcessBroker(port: port);
        broker.Start(runtime.Interpreter!, coreRoot, credentialsStore: null);
        return broker;
    }

    private static async Task WaitForHealthAsync(AnalyticsCoreClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await client.GetHealthAsync(cts.Token);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(500, cts.Token);
            }
        }
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
