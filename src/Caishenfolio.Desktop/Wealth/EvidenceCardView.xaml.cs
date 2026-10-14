using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Caishenfolio.Host;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One evidence row: the claim, its stance colour, and the basis that proves it.</summary>
public sealed record EvidenceRow(string StanceLabel, string Text, string Basis, Brush StanceBrush);

/// <summary>
/// The N4 evidence card page (plan §9). Renders the Python card's nine sections as
/// collapsible blocks: every claim is shown next to the source/formula that produced it,
/// and the conclusion banner states a status about the evidence — never a trade call.
/// </summary>
public partial class EvidenceCardView : UserControl
{
    private Func<AnalyticsCoreClient?>? _clientAccessor;
    private Func<PortfolioViewModel?>? _modelAccessor;
    private Func<decimal>? _singlePositionLimitAccessor;
    private Func<decimal>? _priceTolerancePercentAccessor;

    public EvidenceCardView()
    {
        InitializeComponent();
        DisclaimerText.Text = ProductInfo.ResearchDisclaimer;
    }

    public void Bind(
        Func<AnalyticsCoreClient?> clientAccessor,
        Func<PortfolioViewModel?> modelAccessor,
        Func<string>? baseCurrencyAccessor = null,
        Func<decimal>? singlePositionLimitAccessor = null,
        Func<decimal>? priceTolerancePercentAccessor = null)
    {
        _clientAccessor = clientAccessor;
        _modelAccessor = modelAccessor;
        _singlePositionLimitAccessor = singlePositionLimitAccessor;
        _priceTolerancePercentAccessor = priceTolerancePercentAccessor;
        if (baseCurrencyAccessor is not null)
        {
            CandidateCurrencyLabel.Text = baseCurrencyAccessor();
        }
    }

    private async void GenerateButton_OnClick(object sender, RoutedEventArgs e)
    {
        var client = _clientAccessor?.Invoke();
        if (client is null)
        {
            StatusText.Text = "分析核心尚未就绪。可到「系统」页启动核心。";
            return;
        }

        var symbolInput = SymbolBox.Text.Trim();
        if (symbolInput.Length == 0)
        {
            StatusText.Text = "请先输入标的（代码 / 名称 / 拼音首字母都可以）。";
            return;
        }

        string SelectedSide() =>
            (SideCombo.SelectedItem as ComboBoxItem)?.Tag as string == "sell" ? "sell" : "buy";

        string SelectedFunding() =>
            (FundingCombo.SelectedItem as ComboBoxItem)?.Tag as string == "external"
                ? "external"
                : "existing_cash";

        // The ledger's own resolver first: what the user typed becomes an internal symbol
        // the same way the ledger form does it, so the card and the ledger agree.
        var model = _modelAccessor?.Invoke();
        var resolution = SymbolResolver.Resolve(symbolInput, model?.Snapshot?.Instruments ?? []);
        if (resolution.Resolved is null)
        {
            StatusText.Text = $"标的「{symbolInput}」无法识别。{resolution.Reason}";
            return;
        }

        if (resolution.IsAmbiguous)
        {
            StatusText.Text =
                $"标的「{symbolInput}」有 {resolution.Ambiguities.Count} 种解读（{string.Join("、", resolution.Ambiguities)}），请用完整形式。";
            return;
        }

        var symbol = resolution.Resolved;
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddYears(-2);

        decimal? candidateAmount = null;
        if (decimal.TryParse(CandidateAmountBox.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmount)
            && parsedAmount > 0m)
        {
            candidateAmount = parsedAmount;
        }

        int? horizonDays = null;
        if (int.TryParse(HorizonDaysBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDays)
            && parsedDays > 0)
        {
            horizonDays = parsedDays;
        }

        // UI contract: the user types a PERCENT (20 = 20%). API contract: a 0~1 fraction.
        // One parser shared with the tests (remediation WP5) — no mirrored algorithm.
        decimal? maxLossFraction = null;
        if (MaxLossPctBox.Text.Trim().Length > 0)
        {
            var parsed = PercentInputParser.ParseRequiredFraction(MaxLossPctBox.Text, "可承受最大损失");
            if (!parsed.Ok)
            {
                StatusText.Text = parsed.Error ?? "输入无效。";
                return;
            }

            maxLossFraction = parsed.Fraction;
        }

        // Portfolio facts come from the live snapshot — the card's impact section is about
        // THIS user's portfolio, not a generic one. Absent snapshot → impact skipped.
        decimal? portfolioValue = null;
        decimal? currentWeight = null;
        decimal? cashAvailable = null;
        decimal? singleLimit = null;
        string? portfolioCurrency = null;
        Dictionary<string, double>? regionWeights = null;
        Dictionary<string, double>? currencyWeights = null;
        string instrumentRegion = "";
        string instrumentCurrency = "";
        var snapshot = model?.Snapshot;
        if (candidateAmount is not null && snapshot is { IsEmpty: false })
        {
            portfolioValue = snapshot.Valuation.TotalValue.Amount;
            portfolioCurrency = snapshot.BaseCurrency;
            var existing = snapshot.Valuation.Positions
                .FirstOrDefault(p => p.Position.Symbol == symbol);
            currentWeight = existing?.Weight;
            cashAvailable = snapshot.Valuation.CashValue.Amount;
            singleLimit = _singlePositionLimitAccessor?.Invoke();

            // Real shape facts (P2-R02): the after-weights are only honest when the
            // before-weights and the instrument's own region/currency come from the ledger.
            regionWeights = snapshot.Valuation.ByRegion
                .ToDictionary(s => s.Key, s => (double)s.Weight);
            currencyWeights = snapshot.Valuation.ByCurrency
                .ToDictionary(s => s.Key, s => (double)s.Weight);
            var instrument = snapshot.Instruments.FirstOrDefault(i => i.Symbol == symbol);
            instrumentRegion = instrument?.Region.ToCode()
                ?? (SymbolId.TryParse(symbol, out var parsed)
                    && ExchangeRegistry.TryGetRegion(parsed.Exchange, out var region)
                        ? region.ToCode()
                        : "");
            instrumentCurrency = instrument?.Currency
                ?? (ExchangeRegistry.TryGetQuoteCurrency(
                        SymbolId.Parse(symbol), out var quoteCcy)
                    ? quoteCcy
                    : "");
        }

        GenerateButton.IsEnabled = false;
        StatusText.Text = "正在汇集行情、估值与财务证据…";
        try
        {
            var card = await client.PostEvidenceCardAsync(
                symbol,
                start.ToString("yyyy-MM-dd"),
                end.ToString("yyyy-MM-dd"),
                candidateAmount: candidateAmount,
                candidateCurrency: portfolioCurrency,
                portfolioValue: portfolioValue,
                portfolioCurrency: portfolioCurrency,
                currentWeightOfSymbol: currentWeight,
                singlePositionLimit: singleLimit,
                cashAvailable: cashAvailable,
                horizonDays: horizonDays,
                regionWeights: regionWeights,
                currencyWeights: currencyWeights,
                instrumentRegion: instrumentRegion,
                instrumentCurrency: instrumentCurrency,
                side: SelectedSide(),
                funding: SelectedFunding(),
                // The form expresses amounts in the base currency, so the funding side of
                // an existing-cash buy is the base currency — stated, not left blank (P1-01).
                fundingCurrency: portfolioCurrency,
                maxLossPct: maxLossFraction,
                priceTolerancePct: _priceTolerancePercentAccessor?.Invoke()).ConfigureAwait(true);

            RenderCard(card, maxLossFraction * 100m);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"生成证据卡失败：{ex.Message}";
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private void RenderCard(JsonElement payload, decimal? maxLossPct)
    {
        CardSections.Children.Clear();
        ConclusionBanner.Visibility = Visibility.Collapsed;

        if (!payload.TryGetProperty("card", out var card))
        {
            StatusText.Text = payload.TryGetProperty("error", out var error)
                ? $"生成失败：{error.GetString()}"
                : "生成失败：响应中没有证据卡。";
            return;
        }

        RenderConclusion(card);
        AddSection("1 · 数据质量", card, "data_quality", RenderDataQuality);
        AddSection("2 · 当前估值", card, "valuation", RenderValuation);
        AddSection("3 · 基本面", card, "fundamentals", RenderFundamentals);
        AddSection("4 · 市场行为", card, "market_behaviour", RenderBehaviour);
        AddSection("5 · 历史检验", card, "historical_check", RenderHistorical);
        if (card.TryGetProperty("portfolio_impact", out var impact) && impact.ValueKind == JsonValueKind.Object)
        {
            AddSection("6 · 组合影响（交易前后）", card, "portfolio_impact", RenderImpact);
        }

        AddEvidenceSection("7 · 证据", card);
        AddPlainSection("8 · 失效条件（什么事实出现后应重新评估）", card, "invalidation");

        if (maxLossPct is { } lossPct && card.TryGetProperty("market_behaviour", out var behaviour)
            && behaviour.TryGetProperty("max_drawdown_pct", out var dd) && dd.ValueKind == JsonValueKind.Number)
        {
            var historicalDd = dd.GetDecimal();
            var warning = historicalDd <= -lossPct
                ? $"注意：该标的窗口内历史最大回撤 {historicalDd:0.#}%，已深于你填写的可承受损失 {lossPct:0.#}%。历史深度不设上限，未来可能更深。"
                : $"对照：窗口内历史最大回撤 {historicalDd:0.#}%，你填写可承受 {lossPct:0.#}%。历史深度不设上限，未来可能更深。";
            AddNoteBlock(warning, isWarning: historicalDd <= -lossPct);
        }

        if (card.TryGetProperty("disclaimer", out var disclaimer))
        {
            DisclaimerText.Text = disclaimer.GetString() ?? ProductInfo.ResearchDisclaimer;
        }

        // No valid market date → say UNKNOWN, never echo a rejected raw value (R3-02).
        var marketDate = GetString(card, "market_as_of");
        StatusText.Text = marketDate.Length > 0
            ? $"证据卡已生成（最新行情截至 {marketDate}）。各条目的依据随条目展示。"
            : "证据卡已生成（行情日期未知：K线/报价/估值日期均缺失或格式异常，见数据质量区）。";
    }

    private void RenderConclusion(JsonElement card)
    {
        if (!card.TryGetProperty("conclusion", out var conclusion))
        {
            return;
        }

        var status = GetString(conclusion, "status");
        var (label, brush) = StatusStyle(status);
        ConclusionStatusText.Text = label;
        ConclusionStatusText.Foreground = brush;
        ConclusionSummaryText.Text = GetString(conclusion, "summary");
        // Remediation WP4: the headline is the MARKET date and says so; each channel's own
        // date is listed beside it — one fresh quote must never imply the valuations and
        // filings behind it are equally fresh.
        var marketDate = GetString(card, "market_as_of");
        var metaParts = new List<string>();
        if (marketDate.Length > 0)
        {
            metaParts.Add($"最新行情截至 {marketDate}");
        }
        else
        {
            metaParts.Add("行情日期未知");
        }

        if (card.TryGetProperty("data_quality", out var quality)
            && quality.TryGetProperty("channel_as_of", out var channels))
        {
            foreach (var (channelKey, channelLabel) in (ReadOnlySpan<(string, string)>)[
                         ("bars", "K线"), ("quote", "报价"), ("valuation", "估值"), ("financials", "财务期")])
            {
                if (channels.TryGetProperty(channelKey, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } channelDate)
                {
                    metaParts.Add($"{channelLabel} {channelDate}");
                }
            }
        }

        ConclusionMetaText.Text = string.Join("　·　", metaParts);

        var reasons = new List<string>();
        if (conclusion.TryGetProperty("reasons", out var reasonArray)
            && reasonArray.ValueKind == JsonValueKind.Array)
        {
            reasons.AddRange(reasonArray.EnumerateArray().Select(r => r.GetString() ?? ""));
        }

        ConclusionReasonList.ItemsSource = reasons;
        ConclusionBanner.Visibility = Visibility.Visible;
    }

    private static (string Label, Brush Brush) StatusStyle(string status) => status switch
    {
        "positive" => ("证据偏正面", (Brush)Application.Current.FindResource("BrushAccent")),
        "negative" => ("证据偏反面", (Brush)Application.Current.FindResource("BrushDanger")),
        "conflicting" => ("证据冲突", (Brush)Application.Current.FindResource("BrushDanger")),
        "risk_exceeded" => ("风险超限", (Brush)Application.Current.FindResource("BrushDanger")),
        _ => ("证据不足", (Brush)Application.Current.FindResource("BrushFgMuted")),
    };

    // --- section renderers -----------------------------------------------------------

    private void RenderDataQuality(JsonElement block, StackPanel target)
    {
        var lines = new List<string>
        {
            $"数据源：{GetString(block, "provider")}；数据截至：{GetString(block, "data_as_of")}；K线 {GetInt(block, "bar_count")} 根；复权：{GetString(block, "adjustment")}",
            $"最新价来源数：{GetInt(block, "quote_sources")}" +
            (block.TryGetProperty("quote_spread_pct", out var spread) && spread.ValueKind == JsonValueKind.Number
                ? $"（价差 {spread.GetDecimal():0.##}%）"
                : ""),
        };

        foreach (var conflict in EnumerateStrings(block, "conflicts"))
        {
            lines.Add("冲突：" + conflict);
        }

        if (block.TryGetProperty("missing", out var missing) && missing.GetArrayLength() > 0)
        {
            lines.Add("缺失：" + string.Join("、", missing.EnumerateArray().Select(m => m.GetString())));
        }

        foreach (var warning in EnumerateStrings(block, "warnings"))
        {
            lines.Add("警告：" + warning);
        }

        AddLines(lines, target);
    }

    private void RenderValuation(JsonElement block, StackPanel target)
    {
        if (!GetBool(block, "available"))
        {
            AddLines([GetString(block, "reason")], target);
            return;
        }

        var lines = new List<string>();
        if (block.TryGetProperty("metrics", out var metrics)
            && metrics.ValueKind == JsonValueKind.Array)
        {
            foreach (var metric in metrics.EnumerateArray())
            {
                var current = metric.TryGetProperty("current", out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetDecimal().ToString("0.##", CultureInfo.InvariantCulture)
                    : "—";
                var band = GetString(metric, "band");
                var percentile = metric.TryGetProperty("percentile", out var pct) && pct.ValueKind == JsonValueKind.Number
                    ? $"，历史分位 {pct.GetDecimal():0.#}%"
                    : "";
                lines.Add($"{GetString(metric, "metric")}：{current}{(band.Length > 0 ? $"（{band}{percentile}）" : "")}");
            }
        }

        AddLines(lines, target, "分位只描述位置；低分位不等于便宜，高分位不等于贵。");
    }

    private void RenderFundamentals(JsonElement block, StackPanel target)
    {
        if (!GetBool(block, "available"))
        {
            AddLines([GetString(block, "reason")], target);
            return;
        }

        var lines = new List<string>();
        if (block.TryGetProperty("periods", out var periods)
            && periods.ValueKind == JsonValueKind.Array)
        {
            foreach (var period in periods.EnumerateArray())
            {
                var revenueGrowth = period.TryGetProperty("revenue_growth", out var growth)
                    && growth.ValueKind == JsonValueKind.Number
                    ? growth.GetDecimal()
                    : (decimal?)null;
                lines.Add(
                    $"{GetString(period, "period")}：收入 {(revenueGrowth is null ? "—" : $"{(revenueGrowth * 100m):+0.#;-0.#}%")}（同比）" +
                    $"，净利润 {(period.TryGetProperty("net_profit", out var profit) && profit.ValueKind == JsonValueKind.Number ? profit.GetDecimal().ToString("#,0") : "—")}");
            }
        }

        AddLines(lines, target, "财务数据按披露期呈现；未披露字段显示为空，不由模型补写。");
    }

    private void RenderBehaviour(JsonElement block, StackPanel target)
    {
        if (!GetBool(block, "available"))
        {
            AddLines([GetString(block, "reason")], target);
            return;
        }

        var lines = new List<string>
        {
            $"现价相对 60 日均价：{GetNumber(block, "trend_vs_mean60_pct"):+0.#;-0.#;0}%",
            $"年化波动率：{GetNumber(block, "annualized_volatility_pct"):0.#}%",
            $"窗口内最大回撤：{GetNumber(block, "max_drawdown_pct"):0.#}%",
            $"最新成交量 / 60 日均量：{(block.TryGetProperty("volume_vs_mean60", out var ratio) && ratio.ValueKind == JsonValueKind.Number ? ratio.GetDecimal().ToString("0.##") : "—")}",
            $"样本：{GetInt(block, "sample")} 根K线（截至 {GetString(block, "as_of")}）",
        };
        AddLines(lines, target);
    }

    private void RenderHistorical(JsonElement block, StackPanel target)
    {
        if (!GetBool(block, "available"))
        {
            AddLines([GetString(block, "reason")], target);
            return;
        }

        var lines = new List<string>
        {
            $"现价处于窗口内 {GetNumber(block, "current_price_percentile"):0.#}% 分位",
            $"可比样本 {GetInt(block, "comparable_sample")} 次相近价位，其后 {GetInt(block, "forward_window_days")} 个交易日：",
            $"　中位数 {GetNumber(block, "median_forward_return_pct"):+0.##;-0.##;0}%　最差 {GetNumber(block, "worst_forward_return_pct"):+0.##;-0.##;0}%　最好 {GetNumber(block, "best_forward_return_pct"):+0.##;-0.##;0}%",
        };
        AddLines(lines, target, GetString(block, "caveat"));
    }

    private void RenderImpact(JsonElement block, StackPanel target)
    {
        var currency = GetString(block, "currency");
        var before = GetNullableNumber(block, "single_position_weight_before");
        var after = GetNullableNumber(block, "single_position_weight_after");
        var limit = GetNullableNumber(block, "single_position_limit_pct");

        var lines = new List<string>
        {
            $"候选金额：{GetNumber(block, "candidate_amount"):#,0.##} {currency}",
            $"该标的单一持仓占比：交易前 {Percent(before)} → 交易后 {Percent(after)}"
                + (limit is { } l ? $"（你设的上限 {l:0.#}%）" : ""),
            $"现金占比：交易前 {Percent(GetNullableNumber(block, "cash_weight_before"))} → "
                + $"交易后 {Percent(GetNullableNumber(block, "cash_weight_after"))}",
        };

        // Funding currency is a first-class fact of the simulation (recheck P2-01).
        if (block.TryGetProperty("funding_currency", out var fundingCcy)
            && fundingCcy.ValueKind == JsonValueKind.String
            && fundingCcy.GetString() is { Length: > 0 } ccy)
        {
            lines.Add($"资金来源币种：{ccy}");
        }

        // Region and currency: BEFORE → AFTER per slice, not after-only — without the
        // baseline the user cannot see the size of the move.
        lines.AddRange(BeforeAfterLines(block, "region_weight_before", "region_weight_after", "地区分布"));
        lines.AddRange(BeforeAfterLines(block, "currency_weight_before", "currency_weight_after", "货币分布"));

        foreach (var note in EnumerateStrings(block, "notes"))
        {
            lines.Add("· " + note);
        }

        AddLines(lines, target, "占比全部由你的账本数字计算；这里的数字不构成操作依据。");
    }

    /// <summary>“label：k 前→后、…” lines for one weight dimension; empty when absent.</summary>
    private static IEnumerable<string> BeforeAfterLines(
        JsonElement block, string beforeProp, string afterProp, string label)
    {
        if (!block.TryGetProperty(afterProp, out var after)
            || after.ValueKind != JsonValueKind.Object
            || !after.EnumerateObject().Any())
        {
            yield break;
        }

        block.TryGetProperty(beforeProp, out var before);
        var parts = new List<string>();
        foreach (var entry in after.EnumerateObject()
                     .OrderByDescending(p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDecimal() : 0m))
        {
            var afterValue = entry.Value.ValueKind == JsonValueKind.Number ? entry.Value.GetDecimal() : 0m;
            var beforeValue = 0m;
            if (before.ValueKind == JsonValueKind.Object
                && before.TryGetProperty(entry.Name, out var beforeEntry)
                && beforeEntry.ValueKind == JsonValueKind.Number)
            {
                beforeValue = beforeEntry.GetDecimal();
            }

            var moved = Math.Abs(afterValue - beforeValue) > 0.00005m;
            parts.Add(moved
                ? $"{entry.Name} {beforeValue * 100m:0.#}%→{afterValue * 100m:0.#}%"
                : $"{entry.Name} {afterValue * 100m:0.#}%");
        }

        yield return $"{label}（交易前 → 交易后）：{string.Join("、", parts)}";
    }

    private void AddEvidenceSection(string title, JsonElement card)
    {
        var panel = NewSection(title, out var content);
        var rows = new List<EvidenceRow>();

        void Collect(JsonElement list, string stanceLabel, string text)
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in list.EnumerateArray())
            {
                rows.Add(new EvidenceRow(
                    stanceLabel,
                    item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    item.TryGetProperty("basis", out var b) ? b.GetString() ?? "" : "",
                    (Brush)Application.Current.FindResource(
                        stanceLabel == "正面" ? "BrushAccent"
                        : stanceLabel == "反面" ? "BrushDanger"
                        : "BrushFgMuted")));
            }
        }

        if (card.TryGetProperty("evidence_for", out var forList))
        {
            Collect(forList, "正面", "");
        }

        if (card.TryGetProperty("evidence_against", out var againstList))
        {
            Collect(againstList, "反面", "");
        }

        if (card.TryGetProperty("unknowns", out var unknownList))
        {
            Collect(unknownList, "未知", "");
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Style = (Style)FindResource("WealthGrid"),
            ItemsSource = rows,
            MaxHeight = 220,
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "立场",
            Binding = new System.Windows.Data.Binding("StanceLabel"),
            Width = 54,
            ElementStyle = (Style)FindResource("WealthCellText"),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "事实",
            Binding = new System.Windows.Data.Binding("Text"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = (Style)FindResource("WealthCellText"),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "依据（来源 / 公式）",
            Binding = new System.Windows.Data.Binding("Basis"),
            Width = new DataGridLength(320),
            ElementStyle = (Style)FindResource("WealthCellText"),
        });

        content.Children.Add(grid);
        CardSections.Children.Add(panel);
    }

    // --- building blocks --------------------------------------------------------------

    private void AddSection(
        string title,
        JsonElement card,
        string property,
        Action<JsonElement, StackPanel> render)
    {
        if (!card.TryGetProperty(property, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var panel = NewSection(title, out var content);
        render(block, content);
        CardSections.Children.Add(panel);
    }

    private void AddPlainSection(string title, JsonElement card, string property)
    {
        if (!card.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var panel = NewSection(title, out var content);
        foreach (var item in list.EnumerateArray())
        {
            content.Children.Add(new TextBlock
            {
                Text = "· " + (item.GetString() ?? ""),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("BrushFgSecondary"),
            });
        }

        CardSections.Children.Add(panel);
    }

    private void AddNoteBlock(string text, bool isWarning)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("BrushBgPanel"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            BorderBrush = (Brush)FindResource(isWarning ? "BrushDanger" : "BrushBorder"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = (Brush)FindResource(isWarning ? "BrushDanger" : "BrushFgMuted"),
            },
        };
        CardSections.Children.Add(border);
    }

    private Border NewSection(string title, out StackPanel content)
    {
        content = new StackPanel();
        var header = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = (Brush)FindResource("BrushFgPrimary"),
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(header);
        panel.Children.Add(content);
        return new Border
        {
            Background = (Brush)FindResource("BrushBgPanel"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 2),
            BorderBrush = (Brush)FindResource("BrushBorder"),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    private void AddLines(IReadOnlyList<string> lines, StackPanel target, string? footnote = null)
    {
        foreach (var line in lines)
        {
            target.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("BrushFgSecondary"),
            });
        }

        if (!string.IsNullOrEmpty(footnote))
        {
            target.Children.Add(new TextBlock
            {
                Text = footnote,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("BrushFgMuted"),
            });
        }
    }

    // --- JSON helpers ----------------------------------------------------------------

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static decimal GetNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : 0m;

    private static decimal? GetNullableNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static IEnumerable<string> EnumerateStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray().Select(i => i.GetString() ?? "");
    }

    private static string Percent(decimal? fraction) =>
        fraction is null ? "—" : (fraction.Value * 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
