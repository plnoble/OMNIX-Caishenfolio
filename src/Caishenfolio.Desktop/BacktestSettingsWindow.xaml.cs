using System.Globalization;
using System.Windows;

namespace Caishenfolio.Desktop;

public partial class BacktestSettingsWindow : Window
{
    public BacktestSettingsWindow()
    {
        InitializeComponent();
    }

    public int Fast { get; private set; } = 5;
    public int Slow { get; private set; } = 20;
    public object Costs { get; private set; } = new { };

    public bool Confirmed { get; private set; }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FastBox.Text.Trim(), out var fast) || fast < 1)
        {
            MessageBox.Show(this, "快线 MA 请填正整数。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(SlowBox.Text.Trim(), out var slow) || slow <= fast)
        {
            MessageBox.Show(this, "慢线 MA 必须大于快线。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryRate(CommissionBox.Text, out var commission)
            || !TryRate(StampBox.Text, out var stamp)
            || !TryRate(SlippageBox.Text, out var slip)
            || !TryRate(LimitUpBox.Text, out var up)
            || !TryRate(LimitDownBox.Text, out var down))
        {
            MessageBox.Show(this, "费率/幅度请填数字，例如 0.0003 或 0.10。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Fast = fast;
        Slow = slow;
        Costs = new
        {
            commission_rate = commission,
            commission_min = 0.0,
            stamp_duty_rate = stamp,
            slippage_rate = slip,
            limit_up_pct = up,
            limit_down_pct = down,
            enforce_limit = EnforceLimitCheck.IsChecked == true,
        };
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }

    private static bool TryRate(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
