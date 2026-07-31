using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One target-allocation row: an asset class and the percentage the user wants in it.</summary>
public sealed class TargetRow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Percent { get; set; } = "";
}

public partial class PortfolioSettingsWindow : Window
{
    /// <summary>Classes offered as rebalance targets; the rest are too niche to plan against.</summary>
    private static readonly AssetClass[] TargetableClasses =
    [
        AssetClass.Equity,
        AssetClass.Etf,
        AssetClass.MutualFund,
        AssetClass.Bond,
        AssetClass.ConvertibleBond,
        AssetClass.Cash,
        AssetClass.Commodity,
        AssetClass.Reit,
    ];

    private readonly PortfolioWorkspace _workspace;
    private readonly PricePlanStore? _planStore;
    private readonly ObservableCollection<TargetRow> _targets = [];

    public PortfolioSettingsWindow(PortfolioWorkspace workspace, PricePlanStore? planStore = null)
    {
        InitializeComponent();
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _planStore = planStore;

        Load(workspace.Settings);
        TargetList.ItemsSource = _targets;
        UpdateTargetTotal();
        LoadAccounts();
    }

    /// <summary>True when settings were saved, so the caller knows to refresh.</summary>
    public bool Saved { get; private set; }

    private void Load(PortfolioSettings settings)
    {
        SelectCurrency(settings.BaseCurrency);

        SinglePositionBox.Text = ToPercent(settings.Thresholds.SinglePosition);
        AssetClassBox.Text = ToPercent(settings.Thresholds.AssetClass);
        RegionBox.Text = ToPercent(settings.Thresholds.Region);
        CurrencyBox.Text = ToPercent(settings.Thresholds.Currency);
        CashBox.Text = ToPercent(settings.Thresholds.Cash);

        CrossCheckBox.IsChecked = settings.CrossCheckPrices;
        PriceToleranceBox.Text = settings.PriceTolerancePercent.ToString("0.##", CultureInfo.InvariantCulture);

        foreach (var asset in TargetableClasses)
        {
            var code = asset.ToCode();
            _targets.Add(new TargetRow
            {
                Key = code,
                Label = asset.ToDisplayName(),
                Percent = settings.TargetAssetAllocation.TryGetValue(code, out var weight)
                    ? ToPercent(weight)
                    : "",
            });
        }
    }

    private void SelectCurrency(string currency)
    {
        foreach (ComboBoxItem item in BaseCurrencyCombo.Items)
        {
            if (string.Equals(item.Content?.ToString(), currency, StringComparison.OrdinalIgnoreCase))
            {
                BaseCurrencyCombo.SelectedItem = item;
                return;
            }
        }

        BaseCurrencyCombo.SelectedIndex = 0;
    }

    private void LoadAccounts()
    {
        var accounts = _workspace.Store.ListAccounts(includeArchived: false);
        LegacyAccountCombo.ItemsSource = accounts;
        if (accounts.Count > 0)
        {
            LegacyAccountCombo.SelectedIndex = 0;
        }
        else
        {
            LegacyStatusText.Text = "还没有账户。请先在「账本」页新建一个账户。";
        }
    }

    private void TargetBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is TargetRow row)
        {
            row.Percent = box.Text;
        }

        UpdateTargetTotal();
    }

    private void UpdateTargetTotal()
    {
        var total = 0m;
        var anyInvalid = false;
        foreach (var row in _targets)
        {
            if (string.IsNullOrWhiteSpace(row.Percent))
            {
                continue;
            }

            if (TryParsePercent(row.Percent, out var value))
            {
                total += value;
            }
            else
            {
                anyInvalid = true;
            }
        }

        var filled = _targets.Any(r => !string.IsNullOrWhiteSpace(r.Percent));
        TargetTotalText.Text = anyInvalid
            ? "合计：有无法解析的数字"
            : $"合计 {total * 100m:0.##}%";
        TargetTotalText.Foreground = anyInvalid || (filled && Math.Abs(total - 1m) > 0.0001m)
            ? (System.Windows.Media.Brush)FindResource("BrushDanger")
            : (System.Windows.Media.Brush)FindResource("BrushAccent");
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            var targets = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in _targets.Where(r => !string.IsNullOrWhiteSpace(r.Percent)))
            {
                if (!TryParsePercent(row.Percent, out var weight))
                {
                    throw new LedgerException($"「{row.Label}」的目标占比无法解析：{row.Percent}");
                }

                targets[row.Key] = weight;
            }

            _workspace.ApplySettings(new PortfolioSettings
            {
                BaseCurrency = (BaseCurrencyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CNY",
                Thresholds = new RiskThresholds
                {
                    SinglePosition = ReadPercent(SinglePositionBox, "单一持仓上限"),
                    AssetClass = ReadPercent(AssetClassBox, "单一品种上限"),
                    Region = ReadPercent(RegionBox, "单一市场上限"),
                    Currency = ReadPercent(CurrencyBox, "单一货币上限"),
                    Cash = ReadPercent(CashBox, "现金上限"),
                },
                TargetAssetAllocation = targets,
                CrossCheckPrices = CrossCheckBox.IsChecked == true,
                PriceTolerancePercent = ReadNumber(PriceToleranceBox, "价格容差"),
            });

            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException)
        {
            // Nothing was written: ApplySettings validates before touching the store.
            ErrorText.Text = ex.Message;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ImportLegacyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_planStore is null)
        {
            LegacyStatusText.Text = "找不到旧版台账。";
            return;
        }

        if (LegacyAccountCombo.SelectedValue is not string accountId || accountId.Length == 0)
        {
            LegacyStatusText.Text = "请先选择要导入到哪个账户。";
            return;
        }

        try
        {
            var result = _workspace.ImportLegacyFills(_planStore, accountId);
            var lines = new List<string>
            {
                $"导入 {result.Imported} 条，跳过 {result.Skipped} 条（重复或无法解析）。",
            };
            lines.AddRange(result.Warnings.Take(5));
            LegacyStatusText.Text = string.Join(Environment.NewLine, lines);
            Saved = Saved || result.Imported > 0;
        }
        catch (Exception ex)
        {
            LegacyStatusText.Text = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>Reads a plain number, unlike <see cref="ReadPercent"/> which converts to a fraction.</summary>
    private static decimal ReadNumber(TextBox box, string label) =>
        decimal.TryParse((box.Text ?? "").Trim().TrimEnd('%'), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new LedgerException($"{label}无法解析：{box.Text}");

    private static decimal ReadPercent(TextBox box, string label) =>
        TryParsePercent(box.Text, out var value)
            ? value
            : throw new LedgerException($"{label}无法解析：{box.Text}");

    /// <summary>Reads a percentage the user typed and returns it as a fraction.</summary>
    private static bool TryParsePercent(string text, out decimal fraction)
    {
        fraction = 0m;
        var trimmed = (text ?? "").Trim().TrimEnd('%');
        if (!decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var percent))
        {
            return false;
        }

        fraction = percent / 100m;
        return true;
    }

    private static string ToPercent(decimal fraction) =>
        (fraction * 100m).ToString("0.##", CultureInfo.InvariantCulture);
}
