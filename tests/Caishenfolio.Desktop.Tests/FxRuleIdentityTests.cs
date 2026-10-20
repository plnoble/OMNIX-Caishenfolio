using System.Globalization;
using System.IO;
using System.Windows.Controls;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// WP-03 D5, the part the audit sent back: the rule identity has to be CREATED by the settings
/// page and persisted with the rule — deriving it at comparison time is not identity, it is
/// classification. These tests drive the page's own rule-construction path (the same
/// "internal seam onto the real construction path" boundary <c>LedgerView.BuildTransaction</c>
/// uses), so a rule that never gets an id in the real path fails here.
/// </summary>
public class FxRuleIdentityTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(12);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_fx_rule_identity", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Fills the page's form with the audit's repro shape: CNY base, held JPY, target USD,
    /// threshold 1,000 USD, spread explicitly 0, and the given fixed fee.
    /// </summary>
    private static void FillNetArrivalForm(PortfolioSettingsWindow window, decimal fixedFee)
    {
        // XAML item order: CNY, HKD, USD, JPY (0..3). Base CNY / held JPY / target USD.
        ((ComboBox)window.FindName("FxRuleKindCombo")).SelectedIndex = 2; // 净到账达目标
        ((ComboBox)window.FindName("FxRuleBaseCombo")).SelectedIndex = 0;
        ((ComboBox)window.FindName("FxRuleHeldCombo")).SelectedIndex = 3;
        ((ComboBox)window.FindName("FxRuleTargetCombo")).SelectedIndex = 2;
        ((TextBox)window.FindName("FxRuleThresholdBox")).Text = "1000";
        ((TextBox)window.FindName("FxRuleSpreadBox")).Text = "0";
        ((TextBox)window.FindName("FxRuleFixedFeeBox")).Text = fixedFee.ToString(CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheSettingsPageAssignsANewRuleItsOwnStableIdentity()
    {
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var window = new PortfolioSettingsWindow(new PortfolioWorkspace(store));

            FillNetArrivalForm(window, 0m);
            var rule = window.BuildFxRule();

            // D5: identity is assigned where the rule is created, not derived later from its kind.
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.Equal(rule.Id.Trim(), rule.StableId);

            // …and two rules built from the same form are two rules, not one.
            FillNetArrivalForm(window, 2m);
            var other = window.BuildFxRule();
            Assert.NotEqual(rule.StableId, other.StableId);
            // The resource host requires Application.Current.Windows to be empty afterwards.
            window.Close();
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void A7_TwoRulesThatDifferOnlyByTheirCostModelAreBothDelivered()
    {
        // The audit's repro, verbatim: two CNY→USD net-arrival rules on the held JPY pot, the same
        // 1,000 USD threshold, the same 0 spread — only the fixed fee differs (0 vs 2 USD).
        // Round one gross arrival 1,001: only the zero-fee rule is satisfied and delivered.
        // Within the cooldown the gross arrival rises to 1,003: the fee-paying rule is satisfied
        // for the FIRST time, so it must be delivered too — it may not inherit its sibling's cooldown.
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var window = new PortfolioSettingsWindow(new PortfolioWorkspace(store));

            FillNetArrivalForm(window, 0m);
            var noFee = window.BuildFxRule();
            FillNetArrivalForm(window, 2m);
            var withFee = window.BuildFxRule();
            Assert.Equal(FxAlertRuleKind.NetArrivalAbove, noFee.Kind);
            Assert.Equal(noFee.ThresholdAmount, withFee.ThresholdAmount);
            Assert.NotEqual(noFee.FixedFee, withFee.FixedFee);

            // Two watches, two identities — this is what the audit found missing.
            Assert.NotEqual(noFee.StableId, withFee.StableId);
            Assert.NotEqual(
                AlertGatekeeper.KeyOf(Probe(noFee)),
                AlertGatekeeper.KeyOf(Probe(withFee)));

            // Each rule judges the SAME gross arrival against its OWN cost model: net = gross − its fee.
            FxPathFacts FactsFor(FxAlertRule rule, decimal gross) =>
                new() { NetArrival = gross - (rule.FixedFee ?? 0m) };

            var roundOne = FxAlertRuleEvaluator.Evaluate(
                [noFee, withFee],
                _ => (7.2m, "fixture"),
                rule => FactsFor(rule, 1_001m));
            var first = AlertGatekeeper.Filter(roundOne.Alerts, AlertNotificationState.Empty, Monday, Cooldown);
            Assert.Single(first.ToSend);

            var delivered = AlertGatekeeper.MarkDelivered(first.NextState, first.ToSend, Monday);

            var roundTwo = FxAlertRuleEvaluator.Evaluate(
                [noFee, withFee],
                _ => (7.2m, "fixture"),
                rule => FactsFor(rule, 1_003m));
            var second = AlertGatekeeper.Filter(roundTwo.Alerts, delivered, Monday.AddMinutes(30), Cooldown);

            var sent = Assert.Single(second.ToSend);
            Assert.NotEqual(first.ToSend[0].RuleId, sent.RuleId);
            Assert.Equal(withFee.StableId, sent.RuleId);
            // The resource host requires Application.Current.Windows to be empty afterwards.
            window.Close();
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void EditingARuleKeepsItsIdentityAndAdvancesTheRevisionOnlyWhenTheWatchChanges()
    {
        // D5: the settings page has to be able to CHANGE a rule without turning it into a new one.
        // The identity survives the edit; only a change to what is watched advances the revision.
        WpfTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_root);
            using var store = PortfolioStore.UnderStateRoot(_root);
            var window = new PortfolioSettingsWindow(new PortfolioWorkspace(store));

            FillNetArrivalForm(window, 0m);
            Click(window, "AddFxRuleButton");
            var row = Assert.Single(window.RuleRows);
            var identity = row.Rule.Id;
            Assert.False(string.IsNullOrWhiteSpace(identity));
            Assert.Equal(0, row.Rule.Revision);
            Assert.Equal(1_000m, row.Rule.ThresholdAmount);

            // Load it, change what it watches for, apply.
            Grid(window).SelectedItem = row;
            Click(window, "EditFxRuleButton");
            Assert.Equal("1000", ((TextBox)window.FindName("FxRuleThresholdBox")).Text);
            ((TextBox)window.FindName("FxRuleThresholdBox")).Text = "1200";
            Click(window, "AddFxRuleButton");

            Assert.Same(row, Assert.Single(window.RuleRows));
            Assert.Equal(identity, row.Rule.Id);
            Assert.Equal(1_200m, row.Rule.ThresholdAmount);
            Assert.Equal(1, row.Rule.Revision);

            // Applying an unchanged edit is not a new watch: the revision stays where it is.
            Click(window, "EditFxRuleButton");
            Click(window, "AddFxRuleButton");
            Assert.Equal(identity, row.Rule.Id);
            Assert.Equal(1, row.Rule.Revision);

            // And a rule added afterwards is still a separate identity, not a copy of this one.
            FillNetArrivalForm(window, 2m);
            Click(window, "AddFxRuleButton");
            Assert.Equal(2, window.RuleRows.Count);
            Assert.NotEqual(identity, window.RuleRows[1].Rule.Id);
            // The resource host requires Application.Current.Windows to be empty afterwards.
            window.Close();
            await Task.CompletedTask;
        });
    }

    private static System.Windows.Controls.DataGrid Grid(PortfolioSettingsWindow window) =>
        (System.Windows.Controls.DataGrid)window.FindName("FxRuleGrid");

    /// <summary>Presses a named button in the window, i.e. the user's path, not a private call.</summary>
    private static void Click(PortfolioSettingsWindow window, string buttonName) =>
        ((Button)window.FindName(buttonName)).RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>The alert a rule would produce when it fires — only its identity matters here.</summary>
    private static PortfolioAlert Probe(FxAlertRule rule) => new()
    {
        Kind = AlertKind.FxBand,
        Severity = AlertSeverity.Info,
        Symbol = "USD/CNY",
        Title = "汇率进入你设定的区间",
        Message = "（身份探针，不发送）",
        RuleId = rule.StableId,
        RuleRevision = rule.Revision,
        RuleLevel = rule.Level,
    };

    public void Dispose()
    {
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