using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One entry in the "如果我想做点什么" card: a research entry point with its honest badge.</summary>
public sealed record SimulationEntry(string Badge, string Title, string Detail, string TargetPage);

/// <summary>
/// The novice home page (plan §6). Answers four questions in order: how much do I have and
/// which numbers are uncertain, where is my money, what needs handling today, and what would
/// an action do — with research clearly badged as simulation and never touching the ledger.
/// </summary>
public partial class NoviceHomeView : UserControl
{
    private PortfolioViewModel? _model;
    private PortfolioWorkspace? _workspace;

    /// <summary>Page key the host should navigate to (ledger / overview / market / …).</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>The user wants to (re)open the first-run profile guide.</summary>
    public event Action? OnboardingRequested;

    /// <summary>The user wants the full professional navigation.</summary>
    public event Action? ModeSwitchRequested;

    private static readonly IReadOnlyList<SimulationEntry> SimulationEntries =
    [
        new("研究模拟", "看行情、在图上标买卖价位",
            "打开K线，在图上点选计划买/卖价位；到了价位软件会提醒你，但不会下单。",
            "market"),
        new("研究模拟", "对比几只关注标的",
            "把关注列表里的标的按同一区间归一化叠线对比，看谁强谁弱。", "compare"),
        new("历史回测", "跑一个均线交叉回测",
            "用历史数据检验规则，结果包含成本、回撤和样本外表现——历史不代表未来。", "backtest"),
        new("历史回测", "网格策略回测",
            "区间震荡时的低买高卖规则模拟；参数由规则估算，不是大模型。", "grid"),
    ];

    public NoviceHomeView()
    {
        InitializeComponent();
        DisclaimerText.Text = ProductInfo.ResearchDisclaimer;
        SimulationEntryList.ItemsSource = SimulationEntries;
        LessonTierList.ItemsSource = FinanceLessons.Tiers;
        AttachTermTooltips();
    }

    public void Bind(PortfolioViewModel model, PortfolioWorkspace workspace)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        _workspace = workspace;
        _model.PropertyChanged += OnModelChanged;
        Render();
    }

    /// <summary>Re-renders after out-of-band changes (e.g. the profile guide saved new settings).</summary>
    public void Reload() => Render();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void AttachTermTooltips()
    {
        XirrCaption.ToolTip = TermGlossary.Tooltip("xirr");
        DrawdownCaption.ToolTip = TermGlossary.Tooltip("drawdown");
        PnlCaption.ToolTip = "已实现盈亏 + 浮动盈亏（含分红与票息，扣费税后）。";
    }

    private void Render()
    {
        if (_model is null || _workspace is null)
        {
            return;
        }

        var model = _model;
        var settings = _workspace.Settings;

        TotalValueText.Text = model.TotalValueText;
        CashText.Text = model.CashText;
        TotalPnlText.Text = model.TotalPnlText;
        XirrText.Text = model.XirrText;
        DrawdownText.Text = model.DrawdownText;
        AllocationAsOfText.Text = $"截至 {model.AsOfText}";

        CompletenessText.Text = model.RiskCoverageIsUnknown
            ? "⚠ 无法估值：所有持仓都缺价格（合计显示为未知，不会按 0 计算）"
            : !model.IsComplete
                ? "⚠ 估值不完整：部分持仓缺价格，合计与占比只基于已定价部分"
                : "全部持仓已定价";

        CompletenessText.Foreground = model.IsComplete
            ? (TryFindResource("BrushFgMuted") as System.Windows.Media.Brush)
            : (TryFindResource("BrushDanger") as System.Windows.Media.Brush);

        StatusText.Text = model.StatusText;
        RefreshButton.IsEnabled = !model.IsBusy;

        // Today's tasks: derived from pure, tested logic.
        var unpriced = model.Positions.Count(p => !p.Priced);
        TaskList.ItemsSource = NoviceTaskList.Derive(
            settings.OnboardingCompleted,
            model.Accounts.Count,
            model.Positions.Count,
            unpriced,
            model.Alerts.Count,
            model.IsComplete,
            model.RiskCoverageIsUnknown);

        // Profile card.
        var profileLines = NoviceTaskList.ProfileLines(settings);
        ProfileList.ItemsSource = profileLines;
        ProfileEmptyText.Visibility = profileLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Allocation summary: every dimension, top rows only.
        AllocationList.ItemsSource = model.Allocation.Take(12).ToList();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void TaskCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } && page.Length > 0)
        {
            NavigateRequested?.Invoke(page);
        }
        else if (sender is Button { Tag: null })
        {
            // In-place action (完善你的档案) — open the profile guide right here.
            OnboardingRequested?.Invoke();
        }
    }

    private void SimulationEntry_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } && page.Length > 0)
        {
            NavigateRequested?.Invoke(page);
        }
    }

    private void EditProfileButton_OnClick(object sender, RoutedEventArgs e) =>
        OnboardingRequested?.Invoke();

    private void SwitchModeButton_OnClick(object sender, RoutedEventArgs e) =>
        ModeSwitchRequested?.Invoke();
}
