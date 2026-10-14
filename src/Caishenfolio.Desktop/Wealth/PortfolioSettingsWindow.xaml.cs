using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>One target-allocation row: an asset class and the percentage the user wants in it.</summary>
public sealed class TargetRow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Percent { get; set; } = "";
}

/// <summary>
/// One configured notification channel as the grid shows it.
///
/// The secret is held but never displayed: the grid binds to the masked form, so a screenshot
/// or a shared screen cannot leak a webhook that would let anyone post into the user's group.
/// </summary>
public sealed class ChannelRow
{
    public required WebhookFlavor Flavor { get; init; }
    public required string Secret { get; init; }
    public string ChatId { get; init; } = "";
    public bool Enabled { get; set; } = true;

    public string FlavorName => WebhookNotificationChannel.DisplayName(Flavor);
    public string MaskedSecret => SecretProtector.Mask(Secret);

    public static ChannelRow From(WebhookTarget target) => new()
    {
        Flavor = target.Flavor,
        Secret = target.Secret,
        ChatId = target.ChatId,
        Enabled = target.Enabled,
    };

    public WebhookTarget ToTarget() => new()
    {
        Flavor = Flavor,
        Secret = Secret,
        ChatId = ChatId,
        Enabled = Enabled,
    };
}

/// <summary>One FX watch rule as the editable grid shows it. Enabled toggles in place.</summary>
public sealed class FxRuleRow
{
    /// <summary>Settable so the grid's checkbox can flip Enabled without rebuilding the row.</summary>
    public required FxAlertRule Rule { get; set; }

    public string KindLabel => Rule.Kind switch
    {
        FxAlertRuleKind.RateBand => "汇率进入区间",
        FxAlertRuleKind.HeldAdvantageAbove => "路径优势达阈值",
        FxAlertRuleKind.NetArrivalAbove => "净到账达目标",
        FxAlertRuleKind.BreakevenCrossed => "越过盈亏平衡",
        _ => "未知",
    };

    public string Describe => Rule.Describe();

    public bool Enabled
    {
        get => Rule.Enabled;
        set => Rule = Rule with { Enabled = value };
    }
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
    private readonly ObservableCollection<ChannelRow> _channels = [];
    private readonly ObservableCollection<FxRuleRow> _fxRules = [];
    private readonly NotificationSettingsStore _notifications;

    public PortfolioSettingsWindow(PortfolioWorkspace workspace, PricePlanStore? planStore = null)
    {
        InitializeComponent();
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _planStore = planStore;
        _notifications = new NotificationSettingsStore(workspace.Store);

        Load(workspace.Settings);
        TargetList.ItemsSource = _targets;
        UpdateTargetTotal();
        LoadAccounts();
        LoadNotifications();
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

            // Kept out of PortfolioSettings on purpose — it carries credentials, which the
            // preference record is not built to protect.
            _notifications.Save(ReadNotificationForm());

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

    // --- notifications ---------------------------------------------------------------

    private void LoadNotifications()
    {
        foreach (var flavor in Enum.GetValues<WebhookFlavor>())
        {
            NotifyFlavorCombo.Items.Add(new ComboBoxItem
            {
                Content = WebhookNotificationChannel.DisplayName(flavor),
                Tag = flavor,
            });
        }

        NotifyFlavorCombo.SelectedIndex = 0;
        ChannelGrid.ItemsSource = _channels;
        FxRuleGrid.ItemsSource = _fxRules;

        var settings = _notifications.Load();
        NotifyEnabledBox.IsChecked = settings.Enabled;
        NotifyRoutineBox.IsChecked = settings.IncludeRoutineAlerts;
        BackgroundExtendedBox.IsChecked = settings.BackgroundExtendedChecks;
        AlertCooldownBox.Text = settings.AlertCooldownHours.ToString(CultureInfo.InvariantCulture);
        foreach (var target in settings.Webhooks)
        {
            _channels.Add(ChannelRow.From(target));
        }

        foreach (var rule in settings.FxAlertRules)
        {
            _fxRules.Add(new FxRuleRow { Rule = rule });
        }

        NotifyStatusText.Text = ScheduledCheckInstaller.IsInstalled()
            ? "每日后台检查已注册。"
            : "尚未注册后台检查——软件关着时不会有任何提醒。";
        RenderBackgroundCheckStatus();
    }

    /// <summary>
    /// Shows what the last background run actually did, straight from the log it wrote. A task
    /// that exists but never fired, or fired and failed, reads differently from a healthy one —
    /// and that difference is exactly what the user needs to see before relying on it.
    /// </summary>
    private void RenderBackgroundCheckStatus()
    {
        var status = NotifyLogReader.Read(
            ScheduledCheckInstaller.IsInstalled(),
            extendedChecks: BackgroundExtendedBox.IsChecked == true);
        BackgroundScopeText.Text = "后台检查范围：" + status.Scope;
        BackgroundCheckText.Text = !status.Scheduled
            ? "后台检查：未注册。"
            : status.LastRunAt is { } at
                ? $"最近一次后台检查：{at:yyyy-MM-dd HH:mm}。结果：{status.LastRunSummary ?? status.LastRun ?? "（日志为空）"}"
                : "后台检查：已注册，但还没有运行记录（下次到点后这里会显示结果）。";
    }

    /// <summary>The capability statement must follow the switch the moment it flips.</summary>
    private void BackgroundExtendedBox_OnChanged(object sender, RoutedEventArgs e) =>
        RenderBackgroundCheckStatus();

    // --- FX watch rules (N2.4) ---------------------------------------------------------

    private void FxRuleKind_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FxRuleBandPanel is null || FxRulePathPanel is null)
        {
            return;
        }

        var kind = SelectedFxRuleKind();
        var isBand = kind == FxAlertRuleKind.RateBand;
        FxRuleBandPanel.Visibility = isBand ? Visibility.Visible : Visibility.Collapsed;
        FxRulePathPanel.Visibility = isBand ? Visibility.Collapsed : Visibility.Visible;
        ShowFxRuleCostPanel(kind);

        // The threshold only exists for the two threshold kinds; the label and unit differ.
        var hasThreshold = kind is FxAlertRuleKind.HeldAdvantageAbove or FxAlertRuleKind.NetArrivalAbove;
        FxRuleThresholdPanel.Visibility = hasThreshold ? Visibility.Visible : Visibility.Collapsed;
        if (kind == FxAlertRuleKind.NetArrivalAbove)
        {
            FxRuleThresholdLabel.Text = "净到账≥";
            FxRuleThresholdUnit.Text = "";
            FxRuleThresholdBox.ToolTip = "目标金额（换成目标货币后的净到账），如 1000 表示 1000 目标货币";
        }
        else
        {
            FxRuleThresholdLabel.Text = "优势≥";
            FxRuleThresholdUnit.Text = "%";
            FxRuleThresholdBox.ToolTip = "阈值（百分比），如 3 表示 +3%";
        }
    }

    private void ShowFxRuleCostPanel(FxAlertRuleKind kind)
    {
        FxRuleCostPanel.Visibility = kind == FxAlertRuleKind.NetArrivalAbove
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private FxAlertRuleKind SelectedFxRuleKind() =>
        Enum.TryParse<FxAlertRuleKind>(
            (FxRuleKindCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var parsed)
            ? parsed
            : FxAlertRuleKind.RateBand;

    private void AddFxRuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = BuildFxRule();
            _fxRules.Add(new FxRuleRow { Rule = rule });
            NotifyStatusText.Text = $"已添加规则：{rule.Describe()}。记得点「保存」。";
        }
        catch (Exception ex) when (ex is LedgerException or ArgumentException or FormatException)
        {
            NotifyStatusText.Text = ex.Message;
        }
    }

    private FxAlertRule BuildFxRule()
    {
        var kind = SelectedFxRuleKind();
        if (kind == FxAlertRuleKind.RateBand)
        {
            var pair = (FxRulePairBox.Text ?? "").Trim().ToUpperInvariant();
            var parts = pair.Split('/');
            if (parts.Length != 2 || parts[0].Length != 3 || parts[1].Length != 3)
            {
                throw new LedgerException("货币对应形如 USD/CNY（两个 3 位代码，中间一条斜线）。");
            }

            var lower = ParseOptionalDecimal(FxRuleLowerBox.Text, "区间下限");
            var upper = ParseOptionalDecimal(FxRuleUpperBox.Text, "区间上限");
            if (lower is null && upper is null)
            {
                throw new LedgerException("区间上下限至少填一个。");
            }

            if (lower.HasValue && upper.HasValue && lower.Value > upper.Value)
            {
                throw new LedgerException("区间下限不能大于上限。");
            }

            return new FxAlertRule { Kind = kind, Pair = pair, Lower = lower, Upper = upper };
        }

        var base_ = SelectedCurrency(FxRuleBaseCombo);
        var held = SelectedCurrency(FxRuleHeldCombo);
        var target = SelectedCurrency(FxRuleTargetCombo);
        if (held == base_ || target == base_ || held == target)
        {
            throw new LedgerException("三个货币必须互不相同（基准 / 已持有 / 目标）。");
        }

        var thresholdText = (FxRuleThresholdBox.Text ?? "").Trim();
        if (kind == FxAlertRuleKind.BreakevenCrossed)
        {
            return new FxAlertRule
            {
                Kind = kind,
                BaseCurrency = base_,
                HeldCurrency = held,
                TargetCurrency = target,
            };
        }

        if (kind == FxAlertRuleKind.NetArrivalAbove)
        {
            if (thresholdText.Length == 0
                || !decimal.TryParse(thresholdText, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0m)
            {
                throw new LedgerException($"净到账目标必须是大于 0 的数字（目标货币 {target} 的金额）。");
            }

            var spread = ParseOptionalDecimal(FxRuleSpreadBox.Text, "点差");
            var fixedFee = ParseOptionalDecimal(FxRuleFixedFeeBox.Text, "固定费");
            if (spread is null || fixedFee is null)
            {
                throw new LedgerException(
                    "净到账规则需要完整的费用模型：点差与固定费都要显式填写" +
                    "（填 0 表示确认无此项；留空表示未知，未知的费用不能当零算）。");
            }

            return new FxAlertRule
            {
                Kind = kind,
                BaseCurrency = base_,
                HeldCurrency = held,
                TargetCurrency = target,
                ThresholdAmount = amount,
                SpreadPct = spread,
                FixedFee = fixedFee,
            };
        }

        if (thresholdText.Length == 0
            || !decimal.TryParse(thresholdText, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold)
            || threshold <= 0m)
        {
            throw new LedgerException("优势阈值必须是大于 0 的数字（百分比，如 3 表示 +3%）。");
        }

        return new FxAlertRule
        {
            Kind = FxAlertRuleKind.HeldAdvantageAbove,
            BaseCurrency = base_,
            HeldCurrency = held,
            TargetCurrency = target,
            ThresholdPct = threshold,
        };
    }

    private static string SelectedCurrency(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private static decimal? ParseOptionalDecimal(string text, string label)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new LedgerException($"{label}「{trimmed}」不是有效数字。");
    }

    private void RemoveFxRuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (FxRuleGrid.SelectedItem is not FxRuleRow row)
        {
            NotifyStatusText.Text = "请先在规则列表里选中要删除的一行。";
            return;
        }

        _fxRules.Remove(row);
        NotifyStatusText.Text = $"已移除规则：{row.Describe}。记得点「保存」。";
    }

    private void AddChannelButton_OnClick(object sender, RoutedEventArgs e)
    {
        var flavor = (NotifyFlavorCombo.SelectedItem as ComboBoxItem)?.Tag as WebhookFlavor?
                     ?? WebhookFlavor.WeCom;
        var secret = NotifyWebhookBox.Text.Trim();
        var chatId = NotifyChatIdBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            NotifyStatusText.Text = "请先填写机器人地址（Telegram 填 Bot Token）。";
            return;
        }

        if (flavor == WebhookFlavor.Telegram && string.IsNullOrWhiteSpace(chatId))
        {
            // Without a chat id there is nowhere to post, so this would fail silently later.
            NotifyStatusText.Text = "Telegram 还需要填 Chat ID，否则不知道发到哪个对话。";
            return;
        }

        _channels.Add(ChannelRow.From(new WebhookTarget
        {
            Flavor = flavor,
            Secret = secret,
            ChatId = chatId,
        }));

        NotifyWebhookBox.Text = "";
        NotifyChatIdBox.Text = "";
        NotifyStatusText.Text = $"已添加 {WebhookNotificationChannel.DisplayName(flavor)}，"
                                + "共 " + _channels.Count + " 个渠道。记得点「保存」。";
    }

    private void RemoveChannelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ChannelGrid.SelectedItem is not ChannelRow row)
        {
            NotifyStatusText.Text = "请先在上面的列表里选中要删除的渠道。";
            return;
        }

        _channels.Remove(row);
        NotifyStatusText.Text = $"已移除 {row.FlavorName}。记得点「保存」。";
    }

    /// <summary>Reads the form. Saved on its own, not with the preference record: it holds credentials.</summary>
    private NotificationSettings ReadNotificationForm() => _notifications.Load() with
    {
        Enabled = NotifyEnabledBox.IsChecked == true,
        IncludeRoutineAlerts = NotifyRoutineBox.IsChecked == true,
        BackgroundExtendedChecks = BackgroundExtendedBox.IsChecked == true,
        AlertCooldownHours = ReadCooldown(),
        FxAlertRules = _fxRules.Select(row => row.Rule).ToList(),
        Webhooks = _channels.Select(row => row.ToTarget()).ToList(),
    };

    private int ReadCooldown()
    {
        var text = (AlertCooldownBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return 20;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || hours is < 1 or > 24 * 30)
        {
            throw new LedgerException($"冷却时间必须是 1–720 之间的整数小时（收到 {text}）。");
        }

        return hours;
    }

    private async void TestNotifyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = _notifications.Save(ReadNotificationForm());
        if (!settings.HasUsableChannel)
        {
            NotifyStatusText.Text = "还没有可用的渠道：请勾选「启用通知推送」并填写机器人地址。";
            return;
        }

        NotifyStatusText.Text = "正在发送测试消息…";
        try
        {
            var report = await NotificationDispatcher.From(settings).SendTestAsync().ConfigureAwait(true);
            NotifyStatusText.Text = report.AllDelivered
                ? "测试消息已发出，请到对应的群或应用里确认收到。"
                : report.Describe();
        }
        catch (Exception ex)
        {
            NotifyStatusText.Text = $"发送失败：{ex.Message}";
        }
    }

    private void ScheduleButton_OnClick(object sender, RoutedEventArgs e)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            NotifyStatusText.Text = "找不到程序路径，无法注册后台检查。";
            return;
        }

        // Registering a scheduled task changes what the machine does while the app is closed,
        // so it happens only on this click, and the exact command is shown either way.
        var runAt = new TimeOnly(9, 0);
        var result = ScheduledCheckInstaller.Install(executable, runAt);
        var scope = BackgroundExtendedBox.IsChecked == true
            ? "打新时限 + 价格与集中度（扩展检查）"
            : "打新时限";
        NotifyStatusText.Text = result.Ok
            ? $"已注册：每天 {runAt:HH\\:mm} 检查一次（{scope}）。命令：{result.Command}"
            : $"注册失败：{result.Output}。可手动执行：{result.Command}";
        RenderBackgroundCheckStatus();
    }

    private void UnscheduleButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = ScheduledCheckInstaller.Uninstall();
        NotifyStatusText.Text = result.Ok
            ? "已取消每日后台检查。"
            : $"取消失败：{result.Output}";
        RenderBackgroundCheckStatus();
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
