using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Caishenfolio.Host;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Python;
using Caishenfolio.Host.Security;
using Caishenfolio.Host.Tasks;
using Progress = System.Progress<string>;

namespace Caishenfolio.Desktop;

public partial class MainWindow : Window
{
    private readonly AnalyticsCoreProcessBroker _broker = new(port: 8765);
    private readonly PathRootPolicy _pathRoots = new();
    private readonly SqliteTaskStore _taskStore;
    private readonly TaskMirrorService _taskMirror;
    private readonly MarketCredentialsStore _credentials;
    private readonly WatchlistStore _watchlist;
    private readonly PricePlanStore _pricePlan;
    private AnalyticsCoreClient? _client;
    private IReadOnlyList<MarketBarDto> _lastBars = Array.Empty<MarketBarDto>();
    private string _lastName = "";
    private CandleChartPainter? _chart;
    private bool _watchCollapsed;
    private bool _barsExpanded;
    private string _currentPage = "market";

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{ProductInfo.Name}  v{ProductInfo.Version}";
        TitleText.Text = ProductInfo.Brand;
        VersionBadge.Text = $"v{ProductInfo.Version}\n{ProductInfo.Phase}";
        PhaseText.Text = $"{ProductInfo.Brand} · {ProductInfo.Phase} · {ProductInfo.ScopeSummary}";
        DisclaimerText.Text = ProductInfo.ResearchDisclaimer;
        VersionText.Text = $"{ProductInfo.Name}  版本 v{ProductInfo.Version}  阶段 {ProductInfo.Phase}";
        StatusText.Text = "分析核心未启动。正在自动准备依赖与核心…";
        ResearchStatusText.Text = "双击关注 = 最新区间秒开；搜索支持模糊名称。";
        MarketHintText.Text = MarketLabels.FormatHint();
        SetCoreStatus(false);

        var localApp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio");
        _pathRoots
            .Register(PathRootKind.Import, Path.Combine(localApp, "import"))
            .Register(PathRootKind.Artifact, Path.Combine(localApp, "artifact"))
            .Register(PathRootKind.Run, Path.Combine(localApp, "run"))
            .Register(PathRootKind.State, Path.Combine(localApp, "state"));
        _taskStore = SqliteTaskStore.UnderStateRoot(_pathRoots.GetRoot(PathRootKind.State));
        _taskMirror = new TaskMirrorService(_taskStore);
        _credentials = new MarketCredentialsStore(_pathRoots.GetRoot(PathRootKind.State));
        _watchlist = new WatchlistStore(_pathRoots.GetRoot(PathRootKind.State));
        _pricePlan = new PricePlanStore(_pathRoots.GetRoot(PathRootKind.State));

        _chart = new CandleChartPainter(ChartCanvas, CrosshairLabel);
        _chart.PricePicked += OnChartPricePicked;

        SymbolBox.TextChanged += (_, _) => UpdateSymbolMarketTag();
        IntervalCombo.SelectionChanged += (_, _) =>
        {
            ApplyDefaultDateRange(SelectedInterval());
            UpdateChartPeriodHint();
        };
        UpdateSymbolMarketTag();
        ApplyDefaultDateRange("daily");
        UpdateChartPeriodHint();
        RefreshWatchListUi();
        ShowPage("market");

        Loaded += async (_, _) =>
        {
            await EnsureCoreReadyAsync(auto: true).ConfigureAwait(true);
            await SyncWatchlistCacheQuietAsync().ConfigureAwait(true);
        };

        Closed += (_, _) =>
        {
            _client?.Dispose();
            _broker.Dispose();
            _taskStore.Dispose();
        };
    }

    private void NavMarket_OnClick(object sender, RoutedEventArgs e) => ShowPage("market");
    private void NavPlan_OnClick(object sender, RoutedEventArgs e) => ShowPage("plan");
    private void NavGrid_OnClick(object sender, RoutedEventArgs e) => ShowPage("grid");
    private void NavBacktest_OnClick(object sender, RoutedEventArgs e) => ShowPage("backtest");
    private void NavCompare_OnClick(object sender, RoutedEventArgs e) => ShowPage("compare");
    private void NavSystem_OnClick(object sender, RoutedEventArgs e) => ShowPage("system");

    private void ShowPage(string page)
    {
        _currentPage = page;
        PageMarket.Visibility = page == "market" ? Visibility.Visible : Visibility.Collapsed;
        PagePlan.Visibility = page == "plan" ? Visibility.Visible : Visibility.Collapsed;
        PageGrid.Visibility = page == "grid" ? Visibility.Visible : Visibility.Collapsed;
        PageBacktest.Visibility = page == "backtest" ? Visibility.Visible : Visibility.Collapsed;
        PageCompare.Visibility = page == "compare" ? Visibility.Visible : Visibility.Collapsed;
        PageSystem.Visibility = page == "system" ? Visibility.Visible : Visibility.Collapsed;

        SetNavStyle(NavMarket, page == "market");
        SetNavStyle(NavPlan, page == "plan");
        SetNavStyle(NavGrid, page == "grid");
        SetNavStyle(NavBacktest, page == "backtest");
        SetNavStyle(NavCompare, page == "compare");
        SetNavStyle(NavSystem, page == "system");

        if (page == "plan")
        {
            BindPlanView();
        }
        else if (page == "grid")
        {
            BindGridView();
        }
        else if (page == "market")
        {
            // chart may need redraw after page shown
            Dispatcher.BeginInvoke(new Action(RedrawChart), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (page == "compare")
        {
            var n = _watchlist.Load().Count;
            CompareHintText.Text = $"当前关注 {n} 只；对比至少需要 2 只。";
        }
    }

    private static void SetNavStyle(Button btn, bool active)
    {
        btn.Style = (Style)btn.FindResource(active ? "NavBtnActive" : "NavBtn");
    }

    private void BindPlanView()
    {
        PlanView.Bind(
            _pricePlan,
            SymbolBox.Text.Trim(),
            LastCloseOrNull(),
            onChanged: ApplyPriceMarkersToChart,
            onRequestPick: kind =>
            {
                ShowPage("market");
                if (_chart is null || _lastBars.Count == 0)
                {
                    StatusText.Text = "请先加载K线再点选。";
                    return;
                }

                _chart.Mode = kind switch
                {
                    "plan_buy" => ChartDrawMode.PickPlanBuy,
                    "plan_sell" => ChartDrawMode.PickPlanSell,
                    "fill_buy" => ChartDrawMode.PickFillBuy,
                    "fill_sell" => ChartDrawMode.PickFillSell,
                    _ => ChartDrawMode.Crosshair,
                };
                StatusText.Text = "已切到行情页，请在K线上点选价格…";
            });
    }

    private void BindGridView()
    {
        try
        {
            var client = EnsureClient();
            GridViewControl.Bind(
                client,
                SymbolBox.Text.Trim(),
                StartDateBox.Text.Trim(),
                EndDateBox.Text.Trim(),
                SelectedAdjustment(),
                SelectedInterval());
        }
        catch (Exception ex)
        {
            StatusText.Text = $"网格页绑定失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private void ToggleWatch_OnClick(object sender, RoutedEventArgs e)
    {
        _watchCollapsed = !_watchCollapsed;
        WatchColumn.Width = _watchCollapsed ? new GridLength(0) : new GridLength(240);
        StatusText.Text = _watchCollapsed ? "关注栏已折叠。" : "关注栏已展开。";
    }

    private void ToggleBars_OnClick(object sender, RoutedEventArgs e)
    {
        _barsExpanded = !_barsExpanded;
        BarsRow.Height = _barsExpanded ? new GridLength(160) : new GridLength(0);
        StatusText.Text = _barsExpanded ? "K线明细已展开。" : "K线明细已收起（主图更大）。";
        RedrawChart();
    }

    private void SetCoreStatus(bool running)
    {
        CoreStatusDot.Background = new System.Windows.Media.SolidColorBrush(
            running
                ? System.Windows.Media.Color.FromRgb(0x3D, 0xDC, 0x97)
                : System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        CoreStatusText.Text = running ? "核心 · 运行中" : "核心 · 未连接";
    }

    private async void StartCoreButton_OnClick(object sender, RoutedEventArgs e) =>
        await EnsureCoreReadyAsync(auto: false).ConfigureAwait(true);

    private async Task EnsureCoreReadyAsync(bool auto)
    {
        try
        {
            if (_broker.IsRunning && _client is not null)
            {
                if (!auto)
                {
                    await RefreshHealthAsync().ConfigureAwait(true);
                }

                return;
            }

            var prefix = auto ? "启动时自动" : "";
            StatusText.Text = $"{prefix}准备中：检测/安装 Python 行情依赖…";
            var progress = new Progress(msg => StatusText.Text = msg);
            var bootstrap = await PythonDependencyBootstrap
                .EnsureMarketDependenciesAsync("python", progress)
                .ConfigureAwait(true);
            if (!bootstrap.Ok)
            {
                StatusText.Text = $"依赖未就绪：{bootstrap.Message}";
                return;
            }

            var repoRoot = FindRepoRoot();
            StatusText.Text = $"{prefix}启动分析核心…";
            _broker.Start("python", repoRoot, _credentials);
            _client?.Dispose();
            _client = new AnalyticsCoreClient(_broker.BaseAddress);
            SetCoreStatus(true);
            await RefreshHealthAsync().ConfigureAwait(true);
            if (auto)
            {
                StatusText.Text += "（已自动启动分析核心）";
            }
        }
        catch (Exception ex)
        {
            SetCoreStatus(false);
            StatusText.Text = $"启动失败：{HumanizeUiError(ex.Message)}" +
                              (auto ? " 可到「系统」页手动启动核心。" : "");
        }
    }

    private async void HealthButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshHealthAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"健康检查失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private void StopCoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        _broker.Stop();
        _client?.Dispose();
        _client = null;
        SetCoreStatus(false);
        StatusText.Text = "分析核心已停止。";
    }

    private void DataSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new DataSourceSettingsWindow(_credentials) { Owner = this };
        window.ShowDialog();
        StatusText.Text = "数据源配置已打开过。若已保存，请停止并重新启动分析核心。";
    }

    private async void SearchButton_OnClick(object sender, RoutedEventArgs e) =>
        await SearchSymbolsAsync().ConfigureAwait(true);

    private async void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchSymbolsAsync().ConfigureAwait(true);
        }
    }

    private void SymbolList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SymbolList.SelectedItem is SymbolRow item)
        {
            ApplySymbolSelection(item.Symbol, item.Name);
        }
    }

    private void WatchList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WatchList.SelectedItem is SymbolRow item)
        {
            ApplySymbolSelection(item.Symbol, item.Name);
        }
    }

    private async void WatchList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WatchList.SelectedItem is SymbolRow item)
        {
            ApplySymbolSelection(item.Symbol, item.Name);
            // 关注双击：回到最新推荐区间 + 当前周期，直接加载
            ApplyDefaultDateRange(SelectedInterval());
            await LoadBarsAsync().ConfigureAwait(true);
        }
    }

    private void ResetLatestRangeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyDefaultDateRange(SelectedInterval());
        StatusText.Text = $"已重置为最新推荐区间：{StartDateBox.Text} ~ {EndDateBox.Text}（{IntervalLabel(SelectedInterval())}）";
    }

    private async void QuickRange1M_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyQuickRange(months: 1);
        await LoadBarsAsync().ConfigureAwait(true);
    }

    private async void QuickRange3M_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyQuickRange(months: 3);
        await LoadBarsAsync().ConfigureAwait(true);
    }

    private async void QuickRange1Y_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyQuickRange(months: 12);
        await LoadBarsAsync().ConfigureAwait(true);
    }

    private void ApplyQuickRange(int months)
    {
        var end = TradingCalendar.LastWeekdayOnOrBefore(TradingCalendar.TodayLocal());
        var start = end.AddMonths(-months);
        StartDateBox.Text = TradingCalendar.Format(start);
        EndDateBox.Text = TradingCalendar.Format(end);
    }

    private void ApplyDefaultDateRange(string interval)
    {
        var (start, end) = TradingCalendar.DefaultRange(interval);
        StartDateBox.Text = TradingCalendar.Format(start);
        EndDateBox.Text = TradingCalendar.Format(end);
    }

    private async void SyncWatchCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SyncWatchlistCacheQuietAsync(forceStatus: true).ConfigureAwait(true);
    }

    private async void ClearCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var client = EnsureClient();
            await client.ClearBarsCacheAsync().ConfigureAwait(true);
            StatusText.Text = "已清理本地 K 线缓存。下次加载将重新从上游拉取。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清理缓存失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private async Task SyncWatchlistCacheQuietAsync(bool forceStatus = false)
    {
        try
        {
            if (_client is null || !_broker.IsRunning)
            {
                return;
            }

            var symbols = _watchlist.Load().Select(x => x.Symbol).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (symbols.Count == 0)
            {
                if (forceStatus)
                {
                    StatusText.Text = "关注列表为空，无需同步缓存。";
                }

                return;
            }

            if (forceStatus)
            {
                StatusText.Text = $"正在增量同步关注列表缓存（{symbols.Count} 只）…";
            }

            var result = await _client.SyncWatchlistCacheAsync(symbols, years: 10).ConfigureAwait(true);
            if (forceStatus || true)
            {
                StatusText.Text = $"关注缓存同步完成：{result}";
            }
        }
        catch (Exception ex)
        {
            if (forceStatus)
            {
                StatusText.Text = $"缓存同步失败：{HumanizeUiError(ex.Message)}";
            }
        }
    }

    private void AddWatchButton_OnClick(object sender, RoutedEventArgs e)
    {
        var symbol = SymbolBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusText.Text = "请先填写或选择标的，再加入关注。";
            return;
        }

        var name = string.IsNullOrWhiteSpace(_lastName) ? symbol : _lastName;
        // try take name from selected search row
        if (SymbolList.SelectedItem is SymbolRow searchRow
            && string.Equals(searchRow.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        {
            name = searchRow.Name;
        }

        if (WatchList.SelectedItem is SymbolRow watchRow
            && string.Equals(watchRow.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        {
            name = watchRow.Name;
        }

        var marketLabel = MarketLabels.FromSymbol(symbol);
        _watchlist.Add(new WatchlistItem
        {
            Symbol = symbol,
            Name = name,
            MarketLabel = marketLabel,
            Market = marketLabel,
            Note = "",
        });
        RefreshWatchListUi();
        StatusText.Text = $"已加入关注：{MarketLabels.FormatRow(symbol, name)}";
    }

    private void RemoveWatchButton_OnClick(object sender, RoutedEventArgs e)
    {
        string? symbol = null;
        if (WatchList.SelectedItem is SymbolRow row)
        {
            symbol = row.Symbol;
        }
        else if (!string.IsNullOrWhiteSpace(SymbolBox.Text))
        {
            symbol = SymbolBox.Text.Trim();
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusText.Text = "请先在关注列表中选中一项，或填写要取消的标的。";
            return;
        }

        _watchlist.Remove(symbol);
        RefreshWatchListUi();
        StatusText.Text = $"已取消关注：{symbol}";
    }

    private async void LoadBarsButton_OnClick(object sender, RoutedEventArgs e) =>
        await LoadBarsAsync().ConfigureAwait(true);

    private async void ResearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var client = EnsureClient();
            var symbol = SymbolBox.Text.Trim();
            var start = StartDateBox.Text.Trim();
            var end = EndDateBox.Text.Trim();
            StatusText.Text = $"正在运行研究快照：{MarketLabels.FormatRow(symbol, _lastName)} …";
            var result = await client.RunSymbolSnapshotAsync(symbol, start, end).ConfigureAwait(true);
            var mirrored = _taskMirror.MirrorResearchSnapshot(result);

            if (result.Ok)
            {
                await LoadBarsAsync().ConfigureAwait(true);
            }

            var artifactId = result.Artifact?.Id ?? mirrored.ArtifactIds.FirstOrDefault() ?? "无";
            ResearchStatusText.Text =
                $"研究成功={YesNo(result.Ok)}；任务={result.Task?.Id ?? "无"}；状态={result.Task?.Status ?? "无"}；" +
                $"镜像={mirrored.Id}；产物={artifactId}；摘要={result.Task?.Summary ?? result.Error ?? "无"}";
            StatusText.Text = result.Ok
                ? $"研究快照成功（{MarketLabels.FromSymbol(symbol)} {symbol}）。"
                : $"研究快照失败：{HumanizeUiError(result.Error ?? "未知错误")}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"研究失败：{HumanizeUiError(ex.Message)}";
            ResearchStatusText.Text = HumanizeUiError(ex.Message);
        }
    }

    private void ChartCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) => RedrawChart();

    private void ChartModeCrosshair_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            return;
        }

        _chart.Mode = ChartDrawMode.Crosshair;
        CrosshairLabel.Text = "模式：十字光标（移动鼠标查看 OHLC；滚轮缩放）";
    }

    private void ChartModePan_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            return;
        }

        _chart.Mode = ChartDrawMode.Pan;
        CrosshairLabel.Text = "模式：平移（按住左键左右拖；也可用右键拖）";
    }

    private void ChartModeTrend_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            return;
        }

        _chart.Mode = ChartDrawMode.TrendLine;
        CrosshairLabel.Text = "模式：趋势线（点一下起点，再点一下终点）";
    }

    private void ChartModeHoriz_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            return;
        }

        _chart.Mode = ChartDrawMode.HorizLine;
        CrosshairLabel.Text = "模式：水平线（点一下定位，再点一下确认）";
    }

    private void ChartPickPlanBuy_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            return;
        }

        if (_lastBars.Count == 0)
        {
            StatusText.Text = "请先加载K线，再点选买点。";
            return;
        }

        _chart.Mode = ChartDrawMode.PickPlanBuy;
        StatusText.Text = "点选计划买：在K线价格区点击，立即添加绿色计划买横线（可连续点多个）。";
    }

    private void ChartPickPlanSell_OnClick(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            return;
        }

        if (_lastBars.Count == 0)
        {
            StatusText.Text = "请先加载K线，再点选卖点。";
            return;
        }

        _chart.Mode = ChartDrawMode.PickPlanSell;
        StatusText.Text = "点选计划卖：在K线价格区点击，立即添加红色计划卖横线（可连续点多个）。";
    }

    private void OnChartPricePicked(double price, string kind)
    {
        try
        {
            var symbol = SymbolBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                StatusText.Text = "无标的，无法记录价位。";
                return;
            }

            price = Math.Round(price, 4, MidpointRounding.AwayFromZero);
            if (kind is "plan_buy" or "plan_sell")
            {
                var side = kind == "plan_buy" ? "buy" : "sell";
                var note = "图上点选";
                _pricePlan.AddLevel(symbol, side, price, note);
                ApplyPriceMarkersToChart();
                PlanView.NotifyExternalChange(lastPrice: LastCloseOrNull());
                var zh = side == "buy" ? "买" : "卖";
                StatusText.Text = $"已添加计划{zh}点 {price:0.####}（可继续点选；十字结束）";
                CrosshairLabel.Text =
                    $"已添加计划{zh} {price:0.####} · 继续点击可再加 · 十字光标结束点选";
                return;
            }

            if (kind is "fill_buy" or "fill_sell")
            {
                var side = kind == "fill_buy" ? "buy" : "sell";
                ShowPage("plan");
                PlanView.ApplyPickedFillPrice(price, side);
                StatusText.Text = $"已从图取成交价 {price:0.####}，请在计划页确认数量后登记。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"点选失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private double? LastCloseOrNull() =>
        _lastBars.Count > 0 ? (double)_lastBars[^1].Close : null;

    private void ChartClearDraw_OnClick(object sender, RoutedEventArgs e)
    {
        _chart?.ClearDrawings();
        CrosshairLabel.Text = "已清除手动画线（不影响计划买/卖与成交线）";
    }

    private void ApplyPriceMarkersToChart()
    {
        if (_chart is null)
        {
            return;
        }

        var symbol = SymbolBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            _chart.ClearPriceMarkers();
            return;
        }

        double? last = _lastBars.Count > 0 ? (double)_lastBars[^1].Close : null;
        var markers = new List<ChartPriceMarker>();
        foreach (var lvl in _pricePlan.ListLevels(symbol, activeOnly: true))
        {
            markers.Add(new ChartPriceMarker
            {
                Id = lvl.Id,
                Kind = lvl.Side == "buy" ? "plan_buy" : "plan_sell",
                Price = lvl.Price,
                Label = string.IsNullOrWhiteSpace(lvl.Note) ? "" : lvl.Note,
            });
        }

        foreach (var fill in _pricePlan.ListFills(symbol).Take(40))
        {
            markers.Add(new ChartPriceMarker
            {
                Id = fill.Id,
                Kind = fill.Side == "buy" ? "fill_buy" : "fill_sell",
                Price = fill.Price,
                Label = $"{fill.Qty:0.####}",
            });
        }

        _chart.SetPriceMarkers(markers, last);
    }

    private void ChartResetZoom_OnClick(object sender, RoutedEventArgs e)
    {
        _chart?.ResetView();
        CrosshairLabel.Text = "已重置缩放，显示全部已加载K线。";
    }

    private async void CompareWatch_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var client = EnsureClient();
            var symbols = _watchlist.Load().Select(x => x.Symbol).Take(4).ToList();
            if (symbols.Count < 2)
            {
                if (!string.IsNullOrWhiteSpace(SymbolBox.Text))
                {
                    symbols.Insert(0, SymbolBox.Text.Trim());
                    symbols = symbols.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
                }
            }

            if (symbols.Count < 2)
            {
                StatusText.Text = "多股对比至少需要 2 个标的：请先在关注列表加入 2 只以上。";
                return;
            }

            StatusText.Text = $"正在对比并绘制叠线图：{string.Join("、", symbols)} …";
            var result = await client
                .CompareSymbolsAsync(
                    symbols,
                    StartDateBox.Text.Trim(),
                    EndDateBox.Text.Trim(),
                    SelectedAdjustment(),
                    SelectedInterval())
                .ConfigureAwait(true);
            ResearchStatusText.Text = result.ToString();

            var win = new CompareWindow(
                client,
                _pathRoots.GetRoot(PathRootKind.Artifact),
                StartDateBox.Text.Trim(),
                EndDateBox.Text.Trim())
            {
                Owner = this,
            };
            win.LoadFromCompareJson(result);
            win.Show();

            StatusText.Text = result.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                ? $"对比叠线图已打开：{string.Join("、", symbols)}（可点「导出对比报告」）"
                : $"对比失败：{(result.TryGetProperty("error", out var err) ? err.GetString() : "未知")}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"对比失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private async void MaBacktest_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(BtFastBox.Text.Trim(), out var fast) || fast < 1)
            {
                StatusText.Text = "快线 MA 请填正整数。";
                return;
            }

            if (!int.TryParse(BtSlowBox.Text.Trim(), out var slow) || slow <= fast)
            {
                StatusText.Text = "慢线 MA 必须大于快线。";
                return;
            }

            if (!TryParseRate(BtCommissionBox.Text, out var commission)
                || !TryParseRate(BtStampBox.Text, out var stamp)
                || !TryParseRate(BtSlippageBox.Text, out var slip)
                || !TryParseRate(BtLimitUpBox.Text, out var up)
                || !TryParseRate(BtLimitDownBox.Text, out var down))
            {
                StatusText.Text = "费率/幅度请填数字，例如 0.0003 或 0.10。";
                return;
            }

            var costs = new
            {
                commission_rate = commission,
                commission_min = 0.0,
                stamp_duty_rate = stamp,
                slippage_rate = slip,
                limit_up_pct = up,
                limit_down_pct = down,
                enforce_limit = BtEnforceLimitCheck.IsChecked == true,
            };

            var client = EnsureClient();
            var symbol = SymbolBox.Text.Trim();
            var start = StartDateBox.Text.Trim();
            var end = EndDateBox.Text.Trim();
            StatusText.Text = $"正在回测 MA{fast}/MA{slow}：{symbol} …";
            BacktestHintText.Text = "回测运行中…";
            var result = await client
                .RunMaBacktestAsync(
                    symbol,
                    start,
                    end,
                    fast: fast,
                    slow: slow,
                    adjustment: SelectedAdjustment(),
                    interval: SelectedInterval(),
                    costs: costs)
                .ConfigureAwait(true);
            ResearchStatusText.Text = result.ToString();
            if (result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                var total = result.TryGetProperty("total_return", out var tr) ? tr.GetDouble() : 0;
                var bh = result.TryGetProperty("buy_hold_return", out var br) ? br.GetDouble() : 0;
                var trades = result.TryGetProperty("trades", out var t) ? t.GetInt32() : 0;
                var dd = result.TryGetProperty("max_drawdown", out var md) ? md.GetDouble() : 0;
                var skipped = result.TryGetProperty("skipped_signals", out var sk) ? sk.GetInt32() : 0;
                StatusText.Text =
                    $"回测完成 {symbol}：策略={total:P2}，买入持有={bh:P2}，成交={trades}，跳过={skipped}，最大回撤={dd:P2}";
                BacktestHintText.Text = StatusText.Text;

                var resultWin = new BacktestResultWindow(
                    client,
                    _pathRoots.GetRoot(PathRootKind.Artifact),
                    symbol,
                    start,
                    end,
                    result)
                {
                    Owner = this,
                };
                resultWin.Show();
            }
            else
            {
                StatusText.Text =
                    $"回测失败：{(result.TryGetProperty("error", out var err) ? err.GetString() : "未知")}";
                BacktestHintText.Text = StatusText.Text;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"回测失败：{HumanizeUiError(ex.Message)}";
            BacktestHintText.Text = StatusText.Text;
        }
    }

    private static bool TryParseRate(string text, out double value) =>
        double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value)
        || double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out value);

    private async void ExportReport_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var client = EnsureClient();
            var symbol = SymbolBox.Text.Trim();
            var artifactRoot = _pathRoots.GetRoot(PathRootKind.Artifact);
            StatusText.Text = "正在导出研究报告…";
            var sections = new List<object>
            {
                new
                {
                    heading = "标的与区间",
                    body = new Dictionary<string, string>
                    {
                        ["symbol"] = symbol,
                        ["market"] = MarketLabels.FromSymbol(symbol),
                        ["start"] = StartDateBox.Text.Trim(),
                        ["end"] = EndDateBox.Text.Trim(),
                        ["interval"] = IntervalLabel(SelectedInterval()),
                        ["adjustment"] = AdjustmentLabel(SelectedAdjustment()),
                    },
                },
                new
                {
                    heading = "运行摘要",
                    body = ResearchStatusText.Text,
                },
                new
                {
                    heading = "说明",
                    body = new List<string>
                    {
                        "本报告由本地研究工作台生成。",
                        "行情可能来自公开源或本地缓存，失败不会伪造数据。",
                        ProductInfo.ResearchDisclaimer,
                    },
                },
            };
            var result = await client
                .ExportReportAsync(
                    artifactRoot,
                    $"研究报告_{symbol.Replace(':', '_')}",
                    symbol,
                    sections)
                .ConfigureAwait(true);
            ResearchStatusText.Text = result.ToString();
            StatusText.Text = result.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                ? $"报告已导出到：{(result.TryGetProperty("markdown_path", out var p) ? p.GetString() : artifactRoot)}"
                : $"导出失败：{(result.TryGetProperty("error", out var err) ? err.GetString() : "未知")}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出报告失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private async void ExportParquet_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var client = EnsureClient();
            var symbol = SymbolBox.Text.Trim();
            StatusText.Text = $"正在导出 {symbol} 到 Parquet/JSONL…";
            var result = await client
                .ExportParquetAsync(
                    symbol,
                    StartDateBox.Text.Trim(),
                    EndDateBox.Text.Trim(),
                    SelectedAdjustment(),
                    SelectedInterval())
                .ConfigureAwait(true);
            ResearchStatusText.Text = result.ToString();
            StatusText.Text = result.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                ? $"导出成功：{(result.TryGetProperty("path", out var p) ? p.GetString() : "")}" +
                  (result.TryGetProperty("warning", out var w) ? "；" + w.GetString() : "")
                : $"导出失败：{(result.TryGetProperty("error", out var err) ? err.GetString() : "未知")}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private async Task LoadBarsAsync()
    {
        try
        {
            var client = EnsureClient();
            var symbol = SymbolBox.Text.Trim();
            var start = StartDateBox.Text.Trim();
            var end = EndDateBox.Text.Trim();
            var market = MarketLabels.FromSymbol(symbol);
            var interval = SelectedInterval();
            var adjustment = SelectedAdjustment();
            var intervalZh = IntervalLabel(interval);
            StatusText.Text = $"正在加载【{market}】{intervalZh}：{symbol} …";
            var bars = await client
                .GetMarketBarsAsync(symbol, start, end, adjustment: adjustment, interval: interval)
                .ConfigureAwait(true);
            if (!bars.Ok || bars.Data is null)
            {
                BarsGrid.ItemsSource = null;
                _lastBars = Array.Empty<MarketBarDto>();
                RedrawChart();
                StatusText.Text = $"行情加载失败：{HumanizeUiError(bars.Error ?? "未知错误")}";
                return;
            }

            _lastBars = bars.Data;
            BarsGrid.ItemsSource = bars.Data.Select(BarRow.FromDto).ToList();
            var label = string.IsNullOrWhiteSpace(bars.IntervalLabel) ? intervalZh : bars.IntervalLabel;
            var cacheHint = bars.FromCache ? "·本地缓存" : "·在线";
            ChartPeriodHint.Text =
                $"当前：{label} / {AdjustmentLabel(adjustment)} {cacheHint}（{DescribeInterval(interval)}；MA5/10/20+量+十字光标）";
            RedrawChart();
            var planN = _pricePlan.ListLevels(symbol, activeOnly: true).Count;
            var fillN = _pricePlan.ListFills(symbol).Count;
            var planHint = planN + fillN > 0 ? $"；计划线{planN}/成交{fillN}" : "";
            var warnings = bars.Warnings.Count == 0 ? "" : "；警告=" + string.Join("，", bars.Warnings);
            StatusText.Text =
                $"已加载【{market}】{label} {bars.Data.Count} 根：{symbol}，数据源={bars.Provider}{cacheHint}{planHint}{warnings}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"行情加载失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private string SelectedInterval()
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "daily";
    }

    private string SelectedAdjustment()
    {
        if (AdjustmentCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "raw";
    }

    private static string IntervalLabel(string interval) => interval switch
    {
        "1m" => "1分钟",
        "5m" => "5分钟",
        "15m" => "15分钟",
        "30m" => "30分钟",
        "60m" => "60分钟",
        "weekly" => "周K",
        "monthly" => "月K",
        "quarterly" => "季K",
        "yearly" => "年K",
        _ => "日K",
    };

    private static string AdjustmentLabel(string adj) => adj switch
    {
        "forward" => "前复权",
        "backward" => "后复权",
        _ => "不复权",
    };

    private static string DescribeInterval(string interval) => interval switch
    {
        "1m" => "每根≈1分钟",
        "5m" => "每根≈5分钟",
        "15m" => "每根≈15分钟",
        "30m" => "每根≈30分钟",
        "60m" => "每根≈60分钟",
        "weekly" => "每根≈1周",
        "monthly" => "每根≈1月",
        "quarterly" => "每根≈1季（由日K聚合）",
        "yearly" => "每根≈1年（由日K聚合）",
        _ => "每根=1个交易日",
    };

    private void UpdateChartPeriodHint()
    {
        var interval = SelectedInterval();
        ChartPeriodHint.Text =
            $"当前选择：{IntervalLabel(interval)} / {AdjustmentLabel(SelectedAdjustment())}（{DescribeInterval(interval)}；加载后刷新）";
    }

    private async Task SearchSymbolsAsync()
    {
        try
        {
            var client = EnsureClient();
            var query = SearchBox.Text;
            StatusText.Text = $"正在搜索：{query} …";
            var response = await client.SearchSymbolsAsync(query).ConfigureAwait(true);
            SymbolList.ItemsSource = response.Items
                .Select(item => new SymbolRow(item.Symbol, item.Name, item.Market, item.AssetClass))
                .ToList();
            StatusText.Text =
                $"搜索「{query}」：{response.Items.Count} 条（列表已带【A股/港股/美股】标签），数据源={response.Provider}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"搜索失败：{HumanizeUiError(ex.Message)}";
        }
    }

    private void ApplySymbolSelection(string symbol, string name)
    {
        SymbolBox.Text = symbol;
        _lastName = name;
        UpdateSymbolMarketTag();
    }

    private void UpdateSymbolMarketTag()
    {
        var symbol = SymbolBox.Text.Trim();
        var label = MarketLabels.FromSymbol(symbol);
        SymbolMarketTag.Text = $"【{label}】";
    }

    private void RefreshWatchListUi()
    {
        WatchList.ItemsSource = _watchlist.Load()
            .Select(item => new SymbolRow(
                item.Symbol,
                string.IsNullOrWhiteSpace(item.Name) ? item.Symbol : item.Name,
                item.Market,
                item.AssetClass))
            .ToList();
    }

    private void RedrawChart()
    {
        if (ChartCanvas.ActualWidth <= 1 || ChartCanvas.ActualHeight <= 1)
        {
            return;
        }

        _chart ??= new CandleChartPainter(ChartCanvas, CrosshairLabel);
        _chart.SetBars(_lastBars);
        // SetBars redraws; re-apply markers so plan lines stay after resize/reload.
        ApplyPriceMarkersToChart();
    }

    private async Task RefreshHealthAsync()
    {
        var client = EnsureClient();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                var health = await client.GetHealthAsync().ConfigureAwait(true);
                var provider = string.IsNullOrWhiteSpace(health.MarketProvider) ? "未知" : health.MarketProvider;
                var providerOk = health.MarketProviderReady ? "可用" : "不可用";
                var synthetic = health.MarketDataSynthetic ? "是（演示）" : "否（真实）";
                SetCoreStatus(true);
                StatusText.Text =
                    $"状态={health.Status}；阶段={health.Phase}；行情源={provider}（{providerOk}）；" +
                    $"合成数据={synthetic}；声明={health.Disclaimer}";
                return;
            }
            catch (HttpRequestException) when (attempt < 29)
            {
                await Task.Delay(200).ConfigureAwait(true);
            }
            catch (TaskCanceledException) when (attempt < 29)
            {
                await Task.Delay(200).ConfigureAwait(true);
            }
        }

        SetCoreStatus(false);
        StatusText.Text = "健康检查失败：核心未响应。请到「系统」页启动分析核心。";
    }

    private AnalyticsCoreClient EnsureClient()
    {
        _client ??= AnalyticsCoreClient.ForLoopback(_broker.Port);
        return _client;
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string HumanizeUiError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "未知错误";
        }

        if (message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase)
            || message.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("10061", StringComparison.Ordinal))
        {
            return "无法连接分析核心（127.0.0.1:8765）。请到左侧「系统」页启动分析核心。";
        }

        if (message.Contains("Proxy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("代理", StringComparison.Ordinal))
        {
            return message + " —— 可在「数据源与密钥」取消「遵循系统代理」，保存后重启核心。";
        }

        if (message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("超时", StringComparison.Ordinal))
        {
            return "请求超时。可能是网络/代理慢，或分析核心卡在上游。可：1) 检查核心已启动；2) 数据源关闭系统代理；3) 稍后重试。原错误：" + message;
        }

        return message;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pythonDir = Path.Combine(dir.FullName, "python", "caishenfolio_core");
            var solution = Path.Combine(dir.FullName, "Caishenfolio.slnx");
            if (Directory.Exists(pythonDir) && File.Exists(solution))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 Caishenfolio 仓库根目录。");
    }

    private sealed class SymbolRow
    {
        public SymbolRow(string symbol, string name, string market, string assetClass)
        {
            Symbol = symbol;
            Name = name;
            Market = market;
            AssetClass = assetClass;
            Display = MarketLabels.FormatRow(symbol, name, market, assetClass);
        }

        public string Symbol { get; }
        public string Name { get; }
        public string Market { get; }
        public string AssetClass { get; }
        public string Display { get; }
    }

    private sealed class BarRow
    {
        public string DateText { get; init; } = "";
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public decimal Volume { get; init; }

        public static BarRow FromDto(MarketBarDto bar)
        {
            var dateText = bar.TimestampUtc;
            if (DateTimeOffset.TryParse(bar.TimestampUtc, out var dto))
            {
                dateText = dto.UtcDateTime.ToString("yyyy-MM-dd");
            }

            return new BarRow
            {
                DateText = dateText,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
            };
        }
    }
}
