using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Caishenfolio.Host.Portfolio;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One currency pair, with the carry expressed as money rather than a bare percentage.</summary>
public sealed record CarryRow
{
    public required string Pair { get; init; }
    public required string RateText { get; init; }
    public required string BaseRateText { get; init; }
    public required string QuoteRateText { get; init; }
    public required string CarryText { get; init; }
    public required string CarryOnMillionText { get; init; }
    public required bool CarryIsPositive { get; init; }
    public required string PercentileText { get; init; }
    public required string RangeText { get; init; }
    public required string RateSourceText { get; init; }
    public required bool RatesStale { get; init; }

    public static CarryRow From(CarryLegDto leg, string baseCurrency)
    {
        var carry = leg.Carry;
        return new CarryRow
        {
            Pair = leg.Pair,
            RateText = Number(leg.Rate),
            BaseRateText = Percent(leg.BaseRate),
            QuoteRateText = Percent(leg.QuoteRate),
            RateSourceText = RateSource(leg),
            RatesStale = leg.RatesStale,
            CarryText = carry is null
                ? "—"
                : (carry.Value * 100m).ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + "%",
            // A percentage is abstract; the same number as money on a million is not.
            CarryOnMillionText = carry is null
                ? "—"
                : (carry.Value * 1_000_000m).ToString("+#,0;-#,0", CultureInfo.InvariantCulture)
                  + " " + baseCurrency,
            CarryIsPositive = carry is > 0m,
            PercentileText = leg.Percentile is { } p
                ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : "数据不足",
            RangeText = leg.Low is { } low && leg.High is { } high
                ? $"{Number(low)} ~ {Number(high)}"
                : "—",
        };
    }

    /// <summary>Names the rate and when it was published, so a stale gap is visible as stale.</summary>
    private static string RateSource(CarryLegDto leg)
    {
        var parts = new[] { leg.BaseRateInfo, leg.QuoteRateInfo }
            .Where(info => info is not null)
            .Select(info => Describe(info!))
            .ToArray();
        return parts.Length == 0 ? "—" : string.Join("；", parts);
    }

    private static string Describe(PolicyRateDto info)
    {
        var name = string.IsNullOrWhiteSpace(info.Name) ? info.Currency : info.Name;
        var stamp = string.IsNullOrWhiteSpace(info.AsOf) ? "日期未知" : info.AsOf;
        return info.Stale
            ? $"{name}（{stamp} 内置值，未刷新）"
            : $"{name}（{stamp}）";
    }

    private static string Number(decimal? value) =>
        value is null ? "—" : value.Value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Percent(decimal? value) =>
        value is null ? "—" : (value.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// One batch row (WP-02 目标 1): when it was acquired, what this segment paid, what it bought, what
/// is left and what that remainder cost, plus the original money one hop further up the chain —
/// shown beside, never added to, the segment cost (R4).
/// </summary>
public sealed record FxLotRow
{
    public required string Date { get; init; }
    public required string Currency { get; init; }
    public required string Paid { get; init; }
    public required string Received { get; init; }
    public required string Remaining { get; init; }
    public required string RemainingCost { get; init; }
    public required string Upstream { get; init; }
    /// <summary>Raw remaining amount, used only to count how many batches are still open.</summary>
    public required string RemainingValue { get; init; }

    public static FxLotRow From(FxLot lot, FxLotLedger ledger)
    {
        var upstream = FxLotLedger.UpstreamCosts(ledger, lot);
        return new FxLotRow
        {
            Date = lot.AcquiredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Currency = lot.Currency,
            Paid = $"{Number(lot.CostAmount)} {lot.CostCurrency}",
            Received = $"{Number(lot.OriginalAmount)} {lot.Currency}",
            Remaining = $"{Number(lot.RemainingAmount)} {lot.Currency}",
            RemainingCost = $"{Number(lot.RemainingCostAmount)} {lot.CostCurrency}",
            Upstream = upstream.Count == 0
                ? "—（用新资金买入）"
                : string.Join(" + ", upstream.Select(pair => $"{Number(pair.Value)} {pair.Key}")),
            RemainingValue = lot.RemainingAmount.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string Number(decimal value) =>
        value.ToString("#,0.##", CultureInfo.InvariantCulture);
}

/// <summary>One path card on the comparison list — display-only projection of an FxPathDto.</summary>
public sealed record FxPathCard
{
    public required string Label { get; init; }
    public required string TargetCurrency { get; init; }
    public required string StatusText { get; init; }
    public required Brush StatusBrush { get; init; }
    public required string StartingAsset { get; init; }
    public required string AmountsText { get; init; }
    public required string Formula { get; init; }
    public required string Note { get; init; }

    // The card is one keyboard stop. Keep its name to the decision facts; long
    // provenance/formula text remains available separately rather than read on every Tab.
    public string AccessibleName => $"{Label}；目标币种 {TargetCurrency}；{AmountsText}；{StatusText}";
    public string AccessibleHelpText => string.Join("。", new[]
    {
        StartingAsset, Formula, Note, "研究模拟，不写入账本。行情是参考价，不是银行可成交报价。",
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static FxPathCard From(FxPathDto path, string target)
    {
        var (statusText, brush) = path.Status switch
        {
            "ok" => ("净额已计入成本", (Brush)System.Windows.Application.Current.FindResource("BrushAccent")),
            "gross_only" => ("成本输入缺失：仅毛额比较", (Brush)System.Windows.Application.Current.FindResource("BrushDanger")),
            "missing_quote" => ("缺少关键报价（fail-closed）", (Brush)System.Windows.Application.Current.FindResource("BrushDanger")),
            _ => ("非兑换路径（估值）", (Brush)System.Windows.Application.Current.FindResource("BrushFgMuted")),
        };

        var amounts = path.GrossAmount is { } gross
            ? $"毛到账 {Format(gross)} {target}" + (path.NetAmount is { } net ? $"　→　净到账 {Format(net)} {target}" : "　→　净到账 未知（成本未填）")
            : "毛到账 未知（缺报价）　→　净到账 未知（缺报价）";

        if (path.DiffVsBest is { } diff)
        {
            var basis = diff.Basis == "net" ? "净额口径" : "毛额口径";
            amounts += diff.Abs == 0m
                ? $"　·　{basis}下最优"
                : $"　·　距最优 {diff.AbsInTarget}（{diff.Pct:0.###}%，{basis}）";
        }

        return new FxPathCard
        {
            Label = path.Label,
            TargetCurrency = target,
            StatusText = statusText,
            StatusBrush = brush,
            StartingAsset = $"起始：{path.StartingAsset}；机会成本：{path.OpportunityCost}",
            AmountsText = amounts,
            Formula = path.Formula,
            Note = path.Note,
        };
    }

    private static string Format(decimal value) =>
        value.ToString("#,0.######", CultureInfo.InvariantCulture);
}

public partial class FxCarryView : UserControl
{
    private readonly ObservableCollection<CarryRow> _rows = [];
    private readonly ObservableCollection<string> _notes = [];
    private readonly ObservableCollection<FxPathCard> _pathCards = [];
    private Func<AnalyticsCoreClient?>? _clientAccessor;
    private string _costCompletenessNote = "";
    private Func<string>? _baseCurrencyAccessor;
    private Func<WorkspaceSnapshot?>? _snapshotAccessor;
    private int _carryRequest;
    private int? _shownYears;

    public FxCarryView()
    {
        InitializeComponent();
        LegGrid.ItemsSource = _rows;
        NoteList.ItemsSource = _notes;
        FxPathList.ItemsSource = _pathCards;
    }

    public void Bind(
        Func<AnalyticsCoreClient?> clientAccessor,
        Func<string> baseCurrencyAccessor,
        Func<WorkspaceSnapshot?>? snapshotAccessor = null)
    {
        _clientAccessor = clientAccessor;
        _baseCurrencyAccessor = baseCurrencyAccessor;
        _snapshotAccessor = snapshotAccessor;
        RefreshCurrencyLabels();
    }

    private void RefreshCurrencyLabels()
    {
        var baseCurrency = _baseCurrencyAccessor?.Invoke() ?? "CNY";
        FxBaseCurrencyLabel.Text = baseCurrency;
        FxHeldCostCurrencyLabel.Text = baseCurrency;
        // The target and held picks must not offer the base currency itself.
        foreach (var item in FxTargetCombo.Items.OfType<ComboBoxItem>())
        {
            item.Visibility = string.Equals(item.Content?.ToString(), baseCurrency, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void FxRangeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            LoadButton_OnClick(sender, e);
        }
    }

    private async void LoadButton_OnClick(object sender, RoutedEventArgs e) => await LoadCarryAsync();

    /// <summary>
    /// The whole carry-load state machine as one awaitable method — request id, busy window,
    /// result/state ownership — so long-term tests drive the real logic without clicking UI;
    /// the click and range-selection handlers stay thin wrappers.
    /// </summary>
    internal async Task LoadCarryAsync()
    {
        var client = _clientAccessor?.Invoke();
        if (client is null)
        {
            StatusText.Text = "分析核心尚未就绪，无法取汇率数据。可到「系统」页启动核心。";
            return;
        }

        var request = ++_carryRequest;
        LoadButton.IsEnabled = false;
        FxRangeCombo.IsEnabled = false;
        StatusText.Text = "正在取汇率与利差…";
        try
        {
            var baseCurrency = _baseCurrencyAccessor?.Invoke() ?? "CNY";
            string[] others = ["USD", "HKD", "JPY", "EUR"];
            var years = 1;
            if (FxRangeCombo.SelectedItem is ComboBoxItem selected
                && int.TryParse(selected.Tag?.ToString(), out var parsedYears))
            {
                years = parsedYears;
            }

            FxCarryMillionColumn.Header = "每 100 万年化利差（按当前利率）";

            var response = await client
                .GetFxCarryAsync(baseCurrency, others.Where(c => c != baseCurrency), years * 365)
                .ConfigureAwait(true);
            if (request != _carryRequest)
            {
                return;
            }

            FxRangeHeader.Text = $"{years} 年高低";
            FxPercentileHelp.Text =
                $"只描述当前汇率在所选 {years} 年区间的位置。接近 0% 表示接近该区间低位，接近 100% 表示接近该区间高位。它不预测涨跌；若来源没有覆盖完整 {years} 年，结果只代表实际返回的数据。";
            _rows.Clear();
            _notes.Clear();
            foreach (var leg in response.Legs)
            {
                _rows.Add(CarryRow.From(leg, baseCurrency));
            }

            foreach (var note in response.Notes)
            {
                _notes.Add("· " + note);
            }

            _shownYears = years;
            DisclaimerText.Text = response.Disclaimer;
            StatusText.Text = _rows.Count == 0
                ? "没有取到任何货币对。"
                : $"本位币 {baseCurrency}，区间 {years} 年，共 {_rows.Count} 个货币对。年化利差正数越高，持有利息越多；汇率位置越低，越接近该区间低位。";
        }
        catch (Exception ex)
        {
            if (request != _carryRequest)
            {
                return;
            }

            StatusText.Text = _shownYears is null
                ? $"查询失败，尚无成功结果：{ex.Message}"
                : $"查询失败，仍显示上次成功的 {_shownYears} 年结果：{ex.Message}";
        }
        finally
        {
            if (request == _carryRequest)
            {
                LoadButton.IsEnabled = true;
                FxRangeCombo.IsEnabled = true;
            }
        }
    }

    // --- N2: exchange-path comparison ----------------------------------------------

    /// <summary>
    /// Fills held currency, amount and historical cost from the ledger: the cash the user
    /// actually holds and the price they actually paid. Ledger facts, not guesses.
    /// </summary>
    private void FxPrefillButton_OnClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _snapshotAccessor?.Invoke();
        var baseCurrency = _baseCurrencyAccessor?.Invoke() ?? "CNY";

        // Currencies the user actually holds as cash, excluding the base.
        var heldCurrencies = (snapshot?.CashBalances ?? [])
            .Where(b => b.Amount != 0m && !string.Equals(b.Currency, baseCurrency, StringComparison.OrdinalIgnoreCase))
            .GroupBy(b => b.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Currency: g.Key, Total: g.Sum(b => b.Amount)))
            .Where(x => x.Total > 0m)
            .OrderBy(x => x.Currency, StringComparer.Ordinal)
            .ToArray();

        FxHeldCombo.Items.Clear();
        foreach (var (currency, _) in heldCurrencies)
        {
            FxHeldCombo.Items.Add(new ComboBoxItem { Content = currency });
        }

        if (FxHeldCombo.Items.Count == 0)
        {
            StatusText.Text = $"账本里没有 {baseCurrency} 以外的现金余额；可先在「账本」页录入换汇或直接手填。";
            return;
        }

        FxHeldCombo.SelectedIndex = 0;
        var held = (FxHeldCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var total = heldCurrencies.First(c => string.Equals(c.Currency, held, StringComparison.OrdinalIgnoreCase)).Total;
        FxHeldAmountBox.Text = total.ToString("0.##", CultureInfo.InvariantCulture);

        // The decision logic lives in LedgerHeldCostPrefill (recheck P1-04) so it is
        // unit-testable; the handler only pastes the result.
        var prefill = LedgerHeldCostPrefill.Compute(
            snapshot?.Transactions ?? [], total, baseCurrency, held);
        FxHeldCostBox.Text = prefill.CostText;
        StatusText.Text = prefill.StatusText;
    }

    private async void FxCompareButton_OnClick(object sender, RoutedEventArgs e)
    {
        var client = _clientAccessor?.Invoke();
        if (client is null)
        {
            StatusText.Text = "分析核心尚未就绪，无法取汇率。可到「系统」页启动核心。";
            return;
        }

        var baseCurrency = _baseCurrencyAccessor?.Invoke() ?? "CNY";
        var target = (FxTargetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "USD";
        var held = (FxHeldCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        if (!TryParse(FxAmountBox.Text, "比较金额", out var amount) || amount <= 0m)
        {
            StatusText.Text = "比较金额必须是大于 0 的数字。";
            return;
        }

        decimal? heldAmount = null;
        decimal? heldCost = null;
        if (held.Length > 0)
        {
            if (!TryParse(FxHeldAmountBox.Text, "已持有金额", out var parsedAmount) || parsedAmount <= 0m)
            {
                StatusText.Text = "已持有金额必须是大于 0 的数字（或点「按账本填入」）。";
                return;
            }

            heldAmount = parsedAmount;
            if (FxHeldCostBox.Text.Trim().Length > 0)
            {
                if (!TryParse(FxHeldCostBox.Text, "当时实付", out var parsedCost) || parsedCost <= 0m)
                {
                    StatusText.Text = "「当时实付」留空（成本未知）或填大于 0 的数字。";
                    return;
                }

                heldCost = parsedCost;
            }
        }

        // Full cost model (recheck P1-03): percentage AND fixed fee per path, both tri-state
        // (blank = unknown, 0 = confirmed none). Invalid text aborts the request with an
        // explicit message instead of being silently dropped as "no cost entered".
        var feeCurrency = (FxFeeCurrencyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? target;
        var costs = new Dictionary<string, FxPathRequestCost>();
        (TextBox PctBox, TextBox FeeBox, string Label, string Key)[] pathInputs =
        [
            (FxHeldCostPctBox, FxHeldFeeBox, "已持有→目标", "held_to_target"),
            (FxNewCostPctBox, FxNewFeeBox, "本位币直换", "base_to_target"),
            (FxDetourCostPctBox, FxDetourFeeBox, "绕行", "base_via_held_to_target"),
        ];
        foreach (var (pctBox, feeBox, label, key) in pathInputs)
        {
            if (!TryParseOptional(pctBox.Text, $"{label}比例成本", out var pct))
            {
                StatusText.Text = $"{label}比例成本无法解析：{pctBox.Text}（留空=未知，0=确认无，或填数字）。";
                return;
            }

            if (!TryParseOptional(feeBox.Text, $"{label}固定费", out var fee))
            {
                StatusText.Text = $"{label}固定费无法解析：{feeBox.Text}（留空=未知，0=确认无，或填数字）。";
                return;
            }

            if (pct is null && fee is null)
            {
                continue; // whole cost model unknown for this path — gross comparison
            }

            costs[key] = new FxPathRequestCost(
                SpreadPercent: pct, FixedFee: fee, FeeCurrency: feeCurrency);
        }

        // Cost-completeness is a persistent fact of THIS comparison (recheck-2 F-04): the
        // old flow wrote it to StatusText and the very next line overwrote it with
        // "正在取报价…" — the user never saw why the net figures were missing.
        _costCompletenessNote =
            costs.Count > 0 && costs.Values.Any(c => c.SpreadPercent is null || c.FixedFee is null)
                ? "注意：至少一条路径的比例成本或固定费未显式填写（留空=未知≠0），净到账按毛额比较；" +
                  "要算净额，请把该路径的比例成本与固定费都填上（0=确认无）。"
                : "";

        FxCompareButton.IsEnabled = false;
        StatusText.Text = "正在取报价并比较路径…";
        try
        {
            var response = await client
                .PostFxPathsAsync(
                    baseCurrency,
                    target,
                    amount,
                    held.Length > 0 ? held : null,
                    heldAmount,
                    heldCost,
                    costs.Count > 0 ? costs : null)
                .ConfigureAwait(true);

            RenderPathComparison(response, baseCurrency);
            // The completeness note rides WITH the result, not the transient status line.
            if (!string.IsNullOrEmpty(_costCompletenessNote))
            {
                StatusText.Text = _costCompletenessNote;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"路径比较失败：{ex.Message}";
        }
        finally
        {
            FxCompareButton.IsEnabled = true;
        }
    }

    internal void RenderPathComparison(FxPathsResponse response, string baseCurrency)
    {
        _pathCards.Clear();

        if (!response.Ok)
        {
            StatusText.Text = response.Error ?? "路径比较失败。";
            FxAdvantageGrossText.Text = "—";
            FxAdvantageNetText.Text = "—";
            FxBreakevenText.Text = "—";
            FxQuoteInfoText.Text = "";
            return;
        }

        foreach (var path in response.Paths)
        {
            _pathCards.Add(FxPathCard.From(path, response.TargetCurrency));
        }

        FxAdvantageGrossText.Text = response.HeldAdvantageGrossPct is { } gross
            ? FormatPct(gross)
            : "—";
        FxAdvantageNetText.Text = response.HeldAdvantageNetPct is { } net
            ? FormatPct(net)
            : "成本未填（毛额比较）";

        if (response.BreakevenHeldRate is { } breakeven && response.Held is { } held)
        {
            FxBreakevenCaption.Text = $"盈亏平衡（{baseCurrency}/{held.Currency}，毛额口径）";
            FxBreakevenText.Text =
                $"历史成交价高于 {breakeven.ToString("0.######", CultureInfo.InvariantCulture)} {held.Currency}/{baseCurrency} 时，已持有路径毛额占优；低于则新资金占优。";
        }
        else
        {
            FxBreakevenText.Text = "—";
        }

        var quoteParts = response.Quotes.Values
            .Select(q => $"{q.Pair} {q.Direction}（{q.Provider}，{q.AsOf}）")
            .ToList();
        if (response.ImpliedCross is { } cross)
        {
            quoteParts.Add($"隐含 {cross.Direction}（{cross.Provider}）");
        }

        RenderNowAndCritical(response);
        RefreshLots();

        FxQuoteInfoText.Text = "报价：" + string.Join("；", quoteParts);
        foreach (var warning in response.Warnings)
        {
            FxQuoteInfoText.Text += Environment.NewLine + "· " + warning;
        }

        DisclaimerText.Text = response.Disclaimer;
        StatusText.Text = $"已比较 {response.Paths.Count} 条路径。结果为研究模拟，不写入账本。";
    }

    /// <summary>
    /// WP-02 A5–A7 on screen: what converting now yields against a NAMED, DATED baseline, and the
    /// future pair value at which the two paths tie. The baseline travels with the number it
    /// explains — "+3% against what?" has to be answerable without guessing.
    /// </summary>
    private void RenderNowAndCritical(FxPathsResponse response)
    {
        var now = response.Now;
        if (now is null)
        {
            FxNowText.Text = "—";
        }
        else
        {
            var parts = new List<string>();
            if (now.GrossAmount is { } gross)
            {
                parts.Add($"{Format(gross)} {now.TargetCurrency}");
            }

            if (now.NetAmount is { } net && now.NetAmount != now.GrossAmount)
            {
                parts.Add($"净 {Format(net)} {now.TargetCurrency}");
            }

            if (now.AdvantageVsBaselinePct is { } advantage)
            {
                parts.Add($"比基准 {FormatPct(advantage)}");
            }

            if (now.PnlVsCostInBase is { } pnl)
            {
                parts.Add($"盈亏 {FormatSigned(pnl)}");
            }

            if (!now.CostKnown)
            {
                parts.Add("买入成本未知，只算到账额");
            }

            FxNowText.Text = string.Join("；", parts);
        }

        var critical = response.FutureCritical;
        if (critical is null)
        {
            FxCriticalText.Text = "—";
        }
        else if (!critical.Solvable || critical.Rate is not { } rate)
        {
            FxCriticalText.Text = string.IsNullOrWhiteSpace(critical.Reason)
                ? "无法计算"
                : $"无法计算（{critical.Reason}）";
        }
        else
        {
            // The unit is the pair's own: "1 USD = X JPY" — not the old JPY/CNY tie rate.
            var text = $"{critical.Unit.Replace("X", FormatRate(rate))}";
            if (critical.CurrentRate is { } current)
            {
                text += $"（现价 {FormatRate(current)}";
                if (critical.RequiredMovePct is { } move)
                {
                    text += $"，还需{(move < 0m ? "上升" : "下降")} {Format(Math.Abs(move))}%";
                }

                text += "）";
            }

            FxCriticalText.Text = text;
        }

        FxBaselineText.Text = now?.Baseline is { } b
            ? $"{b.Name}；{b.QuotePair} = {FormatRate(b.Rate ?? 0m)}，冻结于 {b.FrozenAt}"
            : "—";
    }

    /// <summary>
    /// The batches the held pot is made of (WP-02 目标 1). Read straight from the ledger's exchange
    /// rows, so a reversed exchange disappears here like anywhere else.
    /// </summary>
    private void RefreshLots()
    {
        var snapshot = _snapshotAccessor?.Invoke();
        if (snapshot is null)
        {
            FxLotGrid.ItemsSource = Array.Empty<FxLotRow>();
            FxLotsHint.Text = "账本尚未刷新，暂时看不到换汇批次。";
            return;
        }

        var ledger = FxLotLedger.FromTransactions(snapshot.Transactions);
        var rows = ledger.Lots
            .OrderBy(lot => lot.AcquiredOn)
            .ThenBy(lot => lot.Id, StringComparer.Ordinal)
            .Select(lot => FxLotRow.From(lot, ledger))
            .ToArray();
        FxLotGrid.ItemsSource = rows;

        var open = rows.Count(row => row.RemainingValue != "0");
        FxLotsHint.Text = rows.Length == 0
            ? "账本里还没有换汇记录。每换一次就形成一批，冲正的那一笔不会留下来。"
            : $"共 {rows.Length} 批，其中 {open} 批还有剩余。部分换出按取得日先进先出（FIFO）：从最早的一批开始扣；" +
              "「原始投入」是这条链更早一层的钱，与「剩余部分的成本」是两个币种的数，不要相加。";
        foreach (var warning in ledger.Warnings)
        {
            FxLotsHint.Text += Environment.NewLine + "· " + warning;
        }
    }

    private static string Format(decimal value) =>
        value.ToString("#,0.##", CultureInfo.InvariantCulture);

    private static string FormatSigned(decimal value) =>
        value.ToString("+#,0.##;-#,0.##;0", CultureInfo.InvariantCulture);

    private static string FormatRate(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatPct(decimal percent) =>
        percent.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) + "%";

    private static bool TryParse(string text, string label, out decimal value)
    {
        value = 0m;
        var trimmed = (text ?? "").Trim();
        return decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseOptional(string text, string label, out decimal? value)
    {
        value = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0m)
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
