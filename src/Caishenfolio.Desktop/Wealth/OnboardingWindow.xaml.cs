using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>
/// First-run guide (plan §6: 首次引导采集). Collects the novice profile into the existing
/// <see cref="PortfolioSettings"/> fields. Every answer is optional — skipping marks onboarding
/// done too, because the field exists precisely so the app never nags twice.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly PortfolioWorkspace _workspace;
    private readonly Action<PortfolioSettings> _onSaved;

    public OnboardingWindow(PortfolioWorkspace workspace, Action<PortfolioSettings> onSaved)
    {
        InitializeComponent();
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _onSaved = onSaved;
        DrawdownHint.Text = TermGlossary.Find("drawdown")?.Plain ?? "";
        ErrorText.Text = "";
    }

    private void BaseCurrencyCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmergencyFundCurrencyTag is null)
        {
            return;
        }

        EmergencyFundCurrencyTag.Text = $"（{SelectedCurrency()}）";
    }

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _workspace.Settings with
            {
                BaseCurrency = SelectedCurrency(),
                Goal = GoalBox.Text.Trim(),
                HorizonYears = ParseOptional(HorizonBox.Text, "资金期限"),
                LiquidityNeed = SelectedLiquidity(),
                EmergencyFundAmount = ParseOptional(EmergencyFundBox.Text, "应急金") ?? 0m,
                MaxDrawdownTolerance = SelectedDrawdown(),
                WatchMarkets = SelectedMarkets(),
                OnboardingCompleted = true,
            };

            _workspace.ApplySettings(settings);
            _onSaved(settings);
            Close();
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
        {
            // Validation failures stay on the form — the user sees exactly which answer was
            // rejected and why, instead of a dialog that swallows the profile.
            ErrorText.Text = ex.Message;
        }
    }

    private void SkipButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Skipping still marks onboarding done (never nag twice), but keeps the profile
            // untouched — no half-remembered answers are written.
            _workspace.ApplySettings(_workspace.Settings with { OnboardingCompleted = true });
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException)
        {
            ErrorText.Text = $"跳过失败：{ex.Message}";
            return;
        }

        _onSaved(_workspace.Settings);
        Close();
    }

    private string SelectedCurrency() =>
        (BaseCurrencyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            string s when s.StartsWith("HKD", StringComparison.Ordinal) => "HKD",
            string s when s.StartsWith("USD", StringComparison.Ordinal) => "USD",
            string s when s.StartsWith("JPY", StringComparison.Ordinal) => "JPY",
            _ => "CNY",
        };

    private static decimal? ParseOptional(string text, string label)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{label}「{trimmed}」不是有效数字。");
    }

    private decimal SelectedDrawdown() =>
        (DrawdownCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { } raw
        && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    private string SelectedLiquidity() =>
        (LiquidityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private IReadOnlyList<string> SelectedMarkets()
    {
        var markets = new List<string>();
        if (CnCheck.IsChecked == true)
        {
            markets.Add("cn");
        }

        if (HkCheck.IsChecked == true)
        {
            markets.Add("hk");
        }

        if (UsCheck.IsChecked == true)
        {
            markets.Add("us");
        }

        if (JpCheck.IsChecked == true)
        {
            markets.Add("jp");
        }

        return markets;
    }
}
