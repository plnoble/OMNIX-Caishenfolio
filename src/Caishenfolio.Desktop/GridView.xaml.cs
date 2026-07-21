using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop;

public partial class GridView : UserControl
{
    private AnalyticsCoreClient? _client;
    private string _symbol = "";
    private string _start = "";
    private string _end = "";
    private string _adjustment = "raw";
    private string _interval = "daily";
    private string? _selectedPlanId;

    public GridView()
    {
        InitializeComponent();
    }

    public void Bind(
        AnalyticsCoreClient client,
        string symbol,
        string start,
        string end,
        string adjustment = "raw",
        string interval = "daily")
    {
        _client = client;
        _symbol = symbol;
        _start = start;
        _end = end;
        _adjustment = adjustment;
        _interval = interval;
        ContextText.Text =
            $"{MarketLabels.FromSymbol(symbol)}  {_symbol}  |  {_start} ~ {_end}  |  {_interval} / {_adjustment}";
        StatusText.Text = "可先点「AI 建议网格参数」，再回测或保存到台账。";
        _ = RefreshPlansSafeAsync();
    }

    private async Task RefreshPlansSafeAsync()
    {
        try
        {
            await RefreshPlansAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载台账失败：{ex.Message}";
        }
    }

    private async void Suggest_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_client is null)
            {
                StatusText.Text = "核心未就绪。";
                return;
            }

            StatusText.Text = "正在根据历史K线建议网格…";
            int? lookback = ParseOptionalInt(LookbackBox.Text);
            int? gridCount = ParseOptionalInt(GridCountBox.Text);
            var orderCash = ParseDouble(OrderCashBox.Text, 1000);
            var result = await _client
                .GridSuggestAsync(
                    _symbol,
                    _start,
                    _end,
                    _adjustment,
                    _interval,
                    lookback,
                    gridCount,
                    orderCash)
                .ConfigureAwait(true);

            if (!IsOk(result))
            {
                SuggestResultBox.Text = GetError(result);
                StatusText.Text = "建议失败。";
                return;
            }

            if (result.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
            {
                LowerBox.Text = GetDouble(plan, "lower").ToString("0.####", CultureInfo.InvariantCulture);
                UpperBox.Text = GetDouble(plan, "upper").ToString("0.####", CultureInfo.InvariantCulture);
                GridCountBox.Text = plan.TryGetProperty("grid_count", out var gc) ? gc.GetInt32().ToString() : "10";
                OrderCashBox.Text = GetDouble(plan, "order_cash").ToString("0.##", CultureInfo.InvariantCulture);
                if (plan.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                {
                    var lv = levels.EnumerateArray().Select(x => x.GetDouble().ToString("0.####", CultureInfo.InvariantCulture));
                    LevelsPreviewBox.Text = string.Join("\n", lv);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("【启发式网格建议】（非大模型；基于分位与 ATR）");
            if (result.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine(
                    $"样本={GetInt(stats, "bar_count")}  最新价={GetDouble(stats, "last_close"):0.####}  ATR≈{GetDouble(stats, "atr"):0.####}");
            }

            sb.AppendLine($"下一买档: {FormatNullable(result, "next_buy_level")}");
            sb.AppendLine($"下一卖档: {FormatNullable(result, "next_sell_level")}");
            sb.AppendLine();
            if (result.TryGetProperty("rationale", out var rat) && rat.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in rat.EnumerateArray())
                {
                    sb.AppendLine("· " + (line.GetString() ?? ""));
                }
            }

            SuggestResultBox.Text = sb.ToString();
            StatusText.Text = "已填入建议参数，可修改后回测或保存台账。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"建议失败：{ex.Message}";
        }
    }

    private async void Backtest_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryReadPlan(out var lower, out var upper, out var gridCount, out var orderCash))
            {
                return;
            }

            if (_client is null)
            {
                StatusText.Text = "核心未就绪。";
                return;
            }

            double? initialCash = ParseOptionalDouble(InitialCashBox.Text);
            StatusText.Text = "正在运行网格回测…";
            var result = await _client
                .GridBacktestAsync(
                    _symbol,
                    _start,
                    _end,
                    lower,
                    upper,
                    gridCount,
                    orderCash,
                    initialCash,
                    _adjustment,
                    _interval)
                .ConfigureAwait(true);

            if (!IsOk(result))
            {
                SuggestResultBox.Text = GetError(result);
                StatusText.Text = "回测失败。";
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("【网格回测结果】");
            sb.AppendLine($"策略={GetString(result, "strategy")}  区间={_start} ~ {_end}");
            sb.AppendLine(
                $"收益={GetDouble(result, "total_return"):P2}  买入持有={GetDouble(result, "buy_hold_return"):P2}  最大回撤={GetDouble(result, "max_drawdown"):P2}");
            sb.AppendLine(
                $"成交={GetInt(result, "trades")} (买{GetInt(result, "buy_count")}/卖{GetInt(result, "sell_count")})  跳过={GetInt(result, "skipped_signals")}");
            sb.AppendLine(
                $"初始资金={GetDouble(result, "initial_cash"):0.##}  期末权益={GetDouble(result, "final_equity"):0.##}  持仓股数={GetDouble(result, "open_shares"):0.####}");
            if (result.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine(
                    $"网格 [{GetDouble(plan, "lower"):0.####}, {GetDouble(plan, "upper"):0.####}] × {GetInt(plan, "grid_count")}  格距={GetDouble(plan, "step"):0.####}");
            }

            if (result.TryGetProperty("open_inventory", out var inv) && inv.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("未平网格：");
                foreach (var item in inv.EnumerateArray())
                {
                    sb.AppendLine(
                        $"  档{GetInt(item, "grid_index")}: 买@{GetDouble(item, "buy_level"):0.####} → 卖@{GetDouble(item, "sell_level"):0.####} qty={GetDouble(item, "qty"):0.####}");
                }
            }

            if (result.TryGetProperty("trade_log", out var log) && log.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine();
                sb.AppendLine("最近成交（最多100）：");
                foreach (var t in log.EnumerateArray().TakeLast(20))
                {
                    sb.AppendLine(
                        $"  {GetString(t, "timestamp_utc")} {GetString(t, "side")} lv={GetDouble(t, "level"):0.####} px={GetDouble(t, "fill_price"):0.####} qty={GetDouble(t, "qty"):0.####}");
                }
            }

            sb.AppendLine();
            sb.AppendLine(GetString(result, "disclaimer") ?? "研究/模拟结论，非投资建议。");
            SuggestResultBox.Text = sb.ToString();
            StatusText.Text =
                $"回测完成：策略={GetDouble(result, "total_return"):P2}，买入持有={GetDouble(result, "buy_hold_return"):P2}";

            // optional equity chart reuse
            try
            {
                var win = new BacktestResultWindow(
                    _client,
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    _symbol,
                    _start,
                    _end,
                    result)
                {
                    Owner = Window.GetWindow(this),
                    Title = $"网格回测权益 · {_symbol}",
                };
                win.Show();
            }
            catch
            {
                // chart optional
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"回测失败：{ex.Message}";
        }
    }

    private async void SavePlan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryReadPlan(out var lower, out var upper, out var gridCount, out var orderCash))
            {
                return;
            }

            if (_client is null)
            {
                StatusText.Text = "核心未就绪。";
                return;
            }

            var name = $"{MarketLabels.FromSymbol(_symbol)} 网格 {DateTime.Now:MM-dd HH:mm}";
            var result = await _client
                .GridCreatePlanAsync(_symbol, lower, upper, gridCount, orderCash, name, note: "manual_ledger")
                .ConfigureAwait(true);
            if (!IsOk(result))
            {
                StatusText.Text = GetError(result);
                return;
            }

            StatusText.Text = "方案已保存到台账。可在「成交台账」页登记买卖。";
            await RefreshPlansAsync().ConfigureAwait(true);
            if (result.TryGetProperty("plan", out var plan) && plan.TryGetProperty("id", out var id))
            {
                _selectedPlanId = id.GetString();
                SelectPlanInList(_selectedPlanId);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private async void RefreshPlans_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshPlansAsync().ConfigureAwait(true);
            StatusText.Text = "台账已刷新。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"刷新失败：{ex.Message}";
        }
    }

    private async void DeactivatePlan_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedPlanId))
        {
            StatusText.Text = "请先选择方案。";
            return;
        }

        try
        {
            if (_client is null)
            {
                return;
            }

            await _client.GridDeactivatePlanAsync(_selectedPlanId).ConfigureAwait(true);
            _selectedPlanId = null;
            await RefreshPlansAsync().ConfigureAwait(true);
            SnapshotBox.Text = "";
            StatusText.Text = "方案已停用。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"停用失败：{ex.Message}";
        }
    }

    private async void PlanList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlanList.SelectedItem is PlanListItem item)
        {
            _selectedPlanId = item.Id;
            await LoadSnapshotAsync().ConfigureAwait(true);
        }
    }

    private async void AddFill_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedPlanId))
        {
            StatusText.Text = "请先选择方案。";
            return;
        }

        try
        {
            var side = (SideBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "buy";
            if (!double.TryParse(FillPriceBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                && !double.TryParse(FillPriceBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out price))
            {
                StatusText.Text = "请填写有效成交价格。";
                return;
            }

            if (!double.TryParse(FillQtyBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var qty)
                && !double.TryParse(FillQtyBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out qty))
            {
                StatusText.Text = "请填写有效数量。";
                return;
            }

            var fee = ParseDouble(FillFeeBox.Text, 0);
            double? level = ParseOptionalDouble(FillLevelBox.Text);
            if (_client is null)
            {
                StatusText.Text = "核心未就绪。";
                return;
            }

            var result = await _client
                .GridAddFillAsync(_selectedPlanId, side, price, qty, fee, level)
                .ConfigureAwait(true);
            if (!IsOk(result))
            {
                StatusText.Text = GetError(result);
                return;
            }

            StatusText.Text = $"已登记 {side} {qty}@{price}。";
            if (result.TryGetProperty("snapshot", out var snap))
            {
                RenderSnapshot(snap);
            }
            else
            {
                await LoadSnapshotAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"登记失败：{ex.Message}";
        }
    }

    private async void RefreshSnapshot_OnClick(object sender, RoutedEventArgs e)
    {
        await LoadSnapshotAsync().ConfigureAwait(true);
    }

    private async Task RefreshPlansAsync()
    {
        if (_client is null)
        {
            return;
        }

        var result = await _client.GridListPlansAsync(activeOnly: true).ConfigureAwait(true);
        var items = new List<PlanListItem>();
        if (result.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                var id = GetString(p, "id") ?? "";
                var sym = GetString(p, "symbol") ?? "";
                var name = GetString(p, "name") ?? sym;
                var lower = GetDouble(p, "lower");
                var upper = GetDouble(p, "upper");
                var gc = GetInt(p, "grid_count");
                items.Add(new PlanListItem(id, $"{name}\n{sym} [{lower:0.##}-{upper:0.##}]×{gc}"));
            }
        }

        PlanList.ItemsSource = items;
        if (_selectedPlanId is not null)
        {
            SelectPlanInList(_selectedPlanId);
        }
    }

    private void SelectPlanInList(string? id)
    {
        if (id is null || PlanList.ItemsSource is not IEnumerable<PlanListItem> items)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.Id == id)
            {
                PlanList.SelectedItem = item;
                break;
            }
        }
    }

    private async Task LoadSnapshotAsync()
    {
        if (string.IsNullOrEmpty(_selectedPlanId))
        {
            SnapshotBox.Text = "请选择左侧方案。";
            return;
        }

        try
        {
            if (_client is null)
            {
                return;
            }

            double? last = ParseOptionalDouble(LastPriceBox.Text);
            var snap = await _client.GridSnapshotAsync(_selectedPlanId, last).ConfigureAwait(true);
            RenderSnapshot(snap);
            StatusText.Text = IsOk(snap) ? "台账快照已更新。" : GetError(snap);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"快照失败：{ex.Message}";
        }
    }

    private void RenderSnapshot(JsonElement snap)
    {
        if (!IsOk(snap))
        {
            SnapshotBox.Text = GetError(snap);
            return;
        }

        var sb = new StringBuilder();
        if (snap.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine($"方案 {GetString(plan, "id")}  {GetString(plan, "symbol")}");
            sb.AppendLine(
                $"区间 [{GetDouble(plan, "lower"):0.####}, {GetDouble(plan, "upper"):0.####}] × {GetInt(plan, "grid_count")}  每格金额={GetDouble(plan, "order_cash"):0.##}");
            if (plan.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine(
                    "档位: " +
                    string.Join(
                        ", ",
                        levels.EnumerateArray().Select(x => x.GetDouble().ToString("0.####", CultureInfo.InvariantCulture))));
            }
        }

        sb.AppendLine();
        sb.AppendLine($"已实现盈亏: {GetDouble(snap, "realized_pnl"):0.####}");
        sb.AppendLine($"未实现盈亏: {FormatNullable(snap, "unrealized_pnl")}");
        sb.AppendLine($"持仓数量: {GetDouble(snap, "open_qty"):0.####}  均价: {FormatNullable(snap, "avg_cost")}");
        sb.AppendLine($"累计费用: {GetDouble(snap, "fees"):0.####}  参考价: {FormatNullable(snap, "last_price")}");

        if (snap.TryGetProperty("next_actions", out var na) && na.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine();
            sb.AppendLine("【下一操作建议】");
            sb.AppendLine($"下一买档: {FormatNullable(na, "next_buy_level")}");
            sb.AppendLine($"价格在网格内: {(na.TryGetProperty("in_band", out var ib) && ib.GetBoolean() ? "是" : "否")}");
            if (na.TryGetProperty("next_sell_candidates", out var sells) && sells.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sells.EnumerateArray())
                {
                    sb.AppendLine(
                        $"  持仓 qty={GetDouble(s, "qty"):0.####} 买档={GetDouble(s, "buy_level"):0.####} → 建议卖={GetDouble(s, "suggest_sell"):0.####} 浮盈参考={FormatNullable(s, "unrealized_ref")}");
                }
            }

            if (na.TryGetProperty("note", out var note))
            {
                sb.AppendLine(note.GetString());
            }
        }

        if (snap.TryGetProperty("fills", out var fills) && fills.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine();
            sb.AppendLine("成交记录：");
            foreach (var f in fills.EnumerateArray())
            {
                sb.AppendLine(
                    $"  {GetString(f, "ts")} {GetString(f, "side")} {GetDouble(f, "qty"):0.####}@{GetDouble(f, "price"):0.####} fee={GetDouble(f, "fee"):0.####}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(GetString(snap, "disclaimer") ?? "台账为人工记录，非券商成交。");
        SnapshotBox.Text = sb.ToString();
    }

    private bool TryReadPlan(out double lower, out double upper, out int gridCount, out double orderCash)
    {
        lower = upper = orderCash = 0;
        gridCount = 0;
        if (!TryParseDouble(LowerBox.Text, out lower) || !TryParseDouble(UpperBox.Text, out upper))
        {
            StatusText.Text = "请填写有效上下沿（可先点 AI 建议）。";
            return false;
        }

        if (!int.TryParse(GridCountBox.Text.Trim(), out gridCount) || gridCount < 2)
        {
            StatusText.Text = "网格数至少为 2。";
            return false;
        }

        orderCash = ParseDouble(OrderCashBox.Text, 1000);
        if (upper <= lower)
        {
            StatusText.Text = "上沿必须大于下沿。";
            return false;
        }

        return true;
    }

    private static bool IsOk(JsonElement el) =>
        el.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    private static string GetError(JsonElement el) =>
        el.TryGetProperty("error", out var err) ? err.GetString() ?? el.ToString() : el.ToString();

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double GetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return 0;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return 0;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetInt32(),
            JsonValueKind.String when int.TryParse(p.GetString(), out var i) => i,
            _ => 0,
        };
    }

    private static string FormatNullable(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "—";
        }

        if (p.ValueKind == JsonValueKind.Number)
        {
            return p.GetDouble().ToString("0.####", CultureInfo.InvariantCulture);
        }

        return p.ToString();
    }

    private static bool TryParseDouble(string text, out double value)
    {
        text = text.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static double ParseDouble(string text, double fallback) =>
        TryParseDouble(text, out var v) ? v : fallback;

    private static double? ParseOptionalDouble(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TryParseDouble(text, out var v) ? v : null;
    }

    private static int? ParseOptionalInt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var v) ? v : null;
    }

    private sealed record PlanListItem(string Id, string Display);
}
