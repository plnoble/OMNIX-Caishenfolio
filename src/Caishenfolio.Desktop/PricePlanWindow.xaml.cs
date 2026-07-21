using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.MarketData;

namespace Caishenfolio.Desktop;

public partial class PricePlanWindow : Window
{
    private readonly PricePlanStore _store;
    private readonly string _symbol;
    private double? _lastPrice;
    private readonly Action? _onChanged;
    private readonly Action<string>? _onRequestPick;

    public PricePlanWindow(
        PricePlanStore store,
        string symbol,
        double? lastPrice = null,
        Action? onChanged = null,
        Action<string>? onRequestPick = null)
    {
        InitializeComponent();
        _store = store;
        _symbol = symbol.Trim();
        _lastPrice = lastPrice;
        _onChanged = onChanged;
        _onRequestPick = onRequestPick;
        ContextText.Text =
            $"{MarketLabels.FromSymbol(_symbol)}  {_symbol}" +
            (lastPrice is null ? "" : $"  |  现价参考 {lastPrice:0.####}");
        Reload();
    }

    /// <summary>Called when MainWindow adds a plan from chart pick.</summary>
    public void NotifyExternalChange(double? lastPrice = null)
    {
        if (lastPrice is not null)
        {
            _lastPrice = lastPrice;
            ContextText.Text =
                $"{MarketLabels.FromSymbol(_symbol)}  {_symbol}  |  现价参考 {lastPrice:0.####}";
        }

        Reload();
    }

    /// <summary>Fill fill-price box from chart pick; user still confirms qty.</summary>
    public void ApplyPickedFillPrice(double price, string side)
    {
        FillPriceBox.Text = price.ToString("0.####", CultureInfo.InvariantCulture);
        SelectSide(FillSideBox, side);
        Activate();
        FillQtyBox.Focus();
        StatusText.Text = $"已从图取{(side == "buy" ? "买" : "卖")}价 {price:0.####}，请填数量后点「登记成交」。";
    }

    private void Reload()
    {
        var levels = _store.ListLevels(_symbol, activeOnly: false);
        PlanList.ItemsSource = levels
            .Select(l => new RowItem(
                l.Id,
                $"{(l.Active ? "" : "[停] ")}{(l.Side == "buy" ? "买" : "卖")}  {l.Price:0.####}" +
                FormatDist(l.Price) +
                (string.IsNullOrWhiteSpace(l.Note) ? "" : $"  · {l.Note}")))
            .ToList();

        var fills = _store.ListFills(_symbol);
        FillList.ItemsSource = fills
            .Select(f => new RowItem(
                f.Id,
                $"{ShortTs(f.Ts)}  {(f.Side == "buy" ? "买" : "卖")}  {f.Qty:0.####}@{f.Price:0.####}" +
                (f.Fee > 0 ? $"  fee={f.Fee:0.####}" : "") +
                (string.IsNullOrWhiteSpace(f.Note) ? "" : $"  · {f.Note}")))
            .ToList();

        var snap = _store.Snapshot(_symbol, _lastPrice);
        var sb = new StringBuilder();
        sb.AppendLine(
            $"持仓≈{snap.OpenQty:0.####}  均价={Fmt(snap.AvgCost)}  已实现盈亏={snap.RealizedPnl:0.####}  未实现={Fmt(snap.UnrealizedPnl)}  费用={snap.Fees:0.####}  成交笔数={snap.FillCount}");
        if (_lastPrice is > 0)
        {
            sb.Append("相对现价：");
            foreach (var l in levels.Where(x => x.Active).OrderBy(x => x.Price))
            {
                var pct = (l.Price / _lastPrice.Value - 1.0) * 100.0;
                sb.Append($"  {(l.Side == "buy" ? "买" : "卖")}{l.Price:0.####}({pct:+0.00;-0.00}%)");
            }
        }

        sb.AppendLine();
        sb.Append("研究/模拟记录，非投资建议。台账为人工录入，非券商回报。");
        SummaryText.Text = sb.ToString();
        StatusText.Text = $"计划 {levels.Count(l => l.Active)} 条活跃 · 成交 {fills.Count} 笔";
    }

    private string FormatDist(double price)
    {
        if (_lastPrice is not > 0)
        {
            return "";
        }

        var pct = (price / _lastPrice.Value - 1.0) * 100.0;
        return $"  ({pct:+0.00;-0.00}% )";
    }

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string ShortTs(string ts)
    {
        if (DateTimeOffset.TryParse(ts, out var dto))
        {
            return dto.ToLocalTime().ToString("MM-dd HH:mm");
        }

        return ts.Length > 16 ? ts[..16] : ts;
    }

    private void AddPlan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryParsePrice(PlanPriceBox.Text, out var price))
            {
                StatusText.Text = "请填写有效计划价格。";
                return;
            }

            var side = SelectedSide(PlanSideBox);
            _store.AddLevel(_symbol, side, price, PlanNoteBox.Text.Trim());
            PlanPriceBox.Clear();
            PlanNoteBox.Clear();
            Reload();
            Notify();
            StatusText.Text = $"已添加计划{(side == "buy" ? "买" : "卖")}点 {price:0.####}，K 线将显示横线。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void RemovePlan_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlanList.SelectedItem is not RowItem row)
        {
            StatusText.Text = "请先选择计划价位。";
            return;
        }

        _store.RemoveLevel(row.Id);
        Reload();
        Notify();
        StatusText.Text = "已删除计划价位。";
    }

    private void DeactivatePlan_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlanList.SelectedItem is not RowItem row)
        {
            StatusText.Text = "请先选择计划价位。";
            return;
        }

        _store.DeactivateLevel(row.Id);
        Reload();
        Notify();
        StatusText.Text = "已停用（不再画线，记录保留）。";
    }

    private void AddFill_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryParsePrice(FillPriceBox.Text, out var price))
            {
                StatusText.Text = "请填写有效成交价。";
                return;
            }

            if (!TryParsePrice(FillQtyBox.Text, out var qty) || qty <= 0)
            {
                StatusText.Text = "请填写有效数量。";
                return;
            }

            var fee = 0.0;
            _ = TryParsePrice(FillFeeBox.Text, out fee);
            var side = SelectedSide(FillSideBox);
            _store.AddFill(_symbol, side, price, qty, fee, FillNoteBox.Text.Trim());
            FillPriceBox.Clear();
            FillQtyBox.Clear();
            FillFeeBox.Text = "0";
            FillNoteBox.Clear();
            Reload();
            Notify();
            StatusText.Text = $"已登记{(side == "buy" ? "买" : "卖")} {qty:0.####}@{price:0.####}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void RemoveFill_OnClick(object sender, RoutedEventArgs e)
    {
        if (FillList.SelectedItem is not RowItem row)
        {
            StatusText.Text = "请先选择成交记录。";
            return;
        }

        _store.RemoveFill(row.Id);
        Reload();
        Notify();
        StatusText.Text = "已删除成交。";
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        Reload();
        Notify();
        StatusText.Text = "已同步到 K 线。";
    }

    private void PickPlanBuy_OnClick(object sender, RoutedEventArgs e)
    {
        _onRequestPick?.Invoke("plan_buy");
        StatusText.Text = "请在主窗口 K 线图上点击，添加计划买点。";
        Owner?.Activate();
    }

    private void PickPlanSell_OnClick(object sender, RoutedEventArgs e)
    {
        _onRequestPick?.Invoke("plan_sell");
        StatusText.Text = "请在主窗口 K 线图上点击，添加计划卖点。";
        Owner?.Activate();
    }

    private void PickFillBuy_OnClick(object sender, RoutedEventArgs e)
    {
        _onRequestPick?.Invoke("fill_buy");
        StatusText.Text = "请在主窗口 K 线图上点击取买价。";
        Owner?.Activate();
    }

    private void PickFillSell_OnClick(object sender, RoutedEventArgs e)
    {
        _onRequestPick?.Invoke("fill_sell");
        StatusText.Text = "请在主窗口 K 线图上点击取卖价。";
        Owner?.Activate();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Notify() => _onChanged?.Invoke();

    private static string SelectedSide(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "buy";
    }

    private static void SelectSide(ComboBox box, string side)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && item.Tag is string tag
                && string.Equals(tag, side, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }

    private static bool TryParsePrice(string text, out double value)
    {
        text = (text ?? "").Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private sealed record RowItem(string Id, string Display);
}
