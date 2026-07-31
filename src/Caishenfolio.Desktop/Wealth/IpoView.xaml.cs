using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

public sealed record IpoRow
{
    public required string Id { get; init; }
    public required string SubscribeDate { get; init; }
    public required string Name { get; init; }
    public required string Symbol { get; init; }
    public required string StatusText { get; init; }
    public required string QuantityText { get; init; }
    public required string IssuePriceText { get; init; }
    public required string SoldPriceText { get; init; }
    public required string ListingDateText { get; init; }
    public required string ProfitText { get; init; }
    public required bool IsGain { get; init; }
    public required bool IsLoss { get; init; }

    public static IpoRow From(IpoSubscription ipo)
    {
        var profit = ipo.RealizedProfit;
        return new IpoRow
        {
            Id = ipo.Id,
            SubscribeDate = ipo.SubscribeDate.ToString("yyyy-MM-dd"),
            Name = ipo.Name,
            Symbol = ipo.Symbol,
            StatusText = Label(ipo.Status),
            QuantityText = $"{Number(ipo.SubscribedQuantity)} / {Number(ipo.AllottedQuantity)}",
            IssuePriceText = Number(ipo.IssuePrice),
            SoldPriceText = ipo.SoldPrice == 0m ? "" : Number(ipo.SoldPrice),
            ListingDateText = ipo.ListingDate?.ToString("yyyy-MM-dd") ?? "",
            // Unsold allotments show as pending rather than as zero profit.
            ProfitText = profit is null
                ? (ipo.Status == IpoStatus.NotAllotted ? "未中签" : "待结算")
                : $"{profit.Value.Amount:+#,0.##;-#,0.##;0} {profit.Value.Currency}",
            IsGain = profit is { } p && p.Amount > 0m,
            IsLoss = profit is { } q && q.Amount < 0m,
        };
    }

    private static string Label(IpoStatus status) => status switch
    {
        IpoStatus.Subscribed => "已申购",
        IpoStatus.NotAllotted => "未中签",
        IpoStatus.Allotted => "已中签",
        IpoStatus.Paid => "已缴款",
        IpoStatus.Sold => "已卖出",
        _ => "已放弃",
    };

    private static string Number(decimal value) =>
        value.ToString("#,0.####", CultureInfo.InvariantCulture);
}

public partial class IpoView : UserControl
{
    private readonly ObservableCollection<IpoRow> _rows = [];
    private PortfolioWorkspace? _workspace;

    public IpoView()
    {
        InitializeComponent();
        IpoGrid.ItemsSource = _rows;
        DateBox.Text = DateTime.Today.ToString("yyyy-MM-dd");
    }

    public event Action<string>? Notified;

    public void Bind(PortfolioWorkspace workspace)
    {
        _workspace = workspace;
        Reload();
    }

    public void Reload()
    {
        if (_workspace is null)
        {
            return;
        }

        var subscriptions = _workspace.Store.ListIpoSubscriptions();
        _rows.Clear();
        foreach (var ipo in subscriptions)
        {
            _rows.Add(IpoRow.From(ipo));
        }

        var accounts = _workspace.Store.ListAccounts(includeArchived: false);
        AccountCombo.ItemsSource = accounts;
        if (AccountCombo.SelectedItem is null && accounts.Count > 0)
        {
            AccountCombo.SelectedIndex = 0;
        }

        var stats = IpoStatistics.From(subscriptions, _workspace.BaseCurrency);
        SubscriptionsText.Text = stats.Subscriptions.ToString(CultureInfo.InvariantCulture);
        HitRateText.Text = stats.HitRate is { } rate
            ? (rate * 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "—";
        ProfitText.Text = $"{stats.RealizedProfit.Amount:+#,0.##;-#,0.##;0} {stats.Currency}";
        AverageText.Text = stats.AveragePerAllotment is { } average
            ? $"{average.Amount:+#,0.##;-#,0.##;0} {average.Currency}"
            : "—";

        StatusText.Text = subscriptions.Count == 0
            ? "还没有记录。每次申购都记下来——只记得中签的那几次，会把中签率算成 100%。"
            : $"共 {subscriptions.Count} 条记录，其中 {stats.Allotments} 次中签、{stats.Sold} 次已卖出。";
    }

    private IpoSubscription? Selected() =>
        IpoGrid.SelectedItem is IpoRow row && _workspace is not null
            ? _workspace.Store.ListIpoSubscriptions().FirstOrDefault(i => i.Id == row.Id)
            : null;

    private void AddButton_OnClick(object sender, RoutedEventArgs e) => Guarded(() =>
    {
        if (_workspace is null)
        {
            return;
        }

        if (AccountCombo.SelectedValue is not string accountId)
        {
            throw new LedgerException("请先选择账户；没有账户可到「账本」页新建。");
        }

        var ipo = IpoSubscription.Create(
            accountId,
            SymbolBox.Text.Trim(),
            ParseDate(DateBox.Text, "申购日"),
            ParseNumber(QuantityBox.Text, "申购数量"),
            ParseNumber(PriceBox.Text, "发行价"),
            _workspace.BaseCurrency,
            NameBox.Text.Trim());

        _workspace.Store.SaveIpoSubscription(ipo);
        SymbolBox.Clear();
        NameBox.Clear();
        QuantityBox.Clear();
        PriceBox.Clear();
        Notify($"已记录 {ipo.Symbol} 的申购。");
    });

    private void AllotButton_OnClick(object sender, RoutedEventArgs e) => Guarded(() =>
    {
        var ipo = Require();
        var updated = ipo.WithAllotment(ParseNumber(AllottedBox.Text, "中签数量", allowZero: true));
        _workspace!.Store.SaveIpoSubscription(updated);
        AllottedBox.Clear();
        Notify(updated.IsAllotted ? $"已登记中签 {updated.AllottedQuantity}。" : "已登记未中签。");
    });

    private void PayButton_OnClick(object sender, RoutedEventArgs e) => Guarded(() =>
    {
        var ipo = Require();
        var listing = string.IsNullOrWhiteSpace(ListingDateBox.Text)
            ? (DateOnly?)null
            : ParseDate(ListingDateBox.Text, "上市日");
        _workspace!.Store.SaveIpoSubscription(
            ipo.WithPayment(ParseDate(PaymentDateBox.Text, "缴款日"), listing));
        Notify("已登记缴款，账本里已自动生成买入。");
    });

    private void SellButton_OnClick(object sender, RoutedEventArgs e) => Guarded(() =>
    {
        var ipo = Require();
        _workspace!.Store.SaveIpoSubscription(ipo.WithSale(
            ParseDate(SoldDateBox.Text, "卖出日"),
            ParseNumber(SoldPriceBox.Text, "卖出价"),
            string.IsNullOrWhiteSpace(SoldFeeBox.Text) ? 0m : ParseNumber(SoldFeeBox.Text, "费用", allowZero: true)));
        Notify("已登记卖出，账本里已自动生成卖出。");
    });

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e) => Guarded(() =>
    {
        var ipo = Require();
        var confirm = MessageBox.Show(
            $"删除 {ipo.SubscribeDate:yyyy-MM-dd} {ipo.Symbol} 的记录？\n账本里由它生成的买卖也会一并删除。",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _workspace!.Store.RemoveIpoSubscription(ipo.Id);
        Notify("已删除。");
    });

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => Reload();

    private IpoSubscription Require() =>
        Selected() ?? throw new LedgerException("请先在下方列表里选中一条记录。");

    private void Guarded(Action action)
    {
        try
        {
            action();
            Reload();
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
        {
            EntryStatusText.Text = ex.Message;
        }
    }

    private void Notify(string message)
    {
        EntryStatusText.Text = message;
        Notified?.Invoke(message);
    }

    private static DateOnly ParseDate(string text, string label) =>
        DateOnly.TryParse((text ?? "").Trim(), CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new LedgerException($"{label}无法解析：{text}（请用 yyyy-MM-dd）");

    private static decimal ParseNumber(string text, string label, bool allowZero = false)
    {
        if (!decimal.TryParse((text ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            throw new LedgerException($"{label}无法解析：{text}");
        }

        if (!allowZero && value <= 0m)
        {
            throw new LedgerException($"{label}必须大于 0。");
        }

        return value;
    }
}
