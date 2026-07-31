using Caishenfolio.Host.Data;

namespace Caishenfolio.Host.Portfolio;

/// <summary>A dated amount. The sign convention depends on the metric — see each method.</summary>
public sealed record DatedAmount(DateOnly Date, decimal Amount);

/// <summary>Portfolio value observed on a date, in one currency.</summary>
public sealed record ValuationPoint(DateOnly Date, decimal Value);

/// <summary>
/// Return metrics over a ledger.
///
/// Money stays decimal everywhere; a solved *rate* is a double, because the root finding needs
/// fractional powers. Every method returns null rather than a fabricated number when the inputs
/// cannot support an answer.
/// </summary>
public static class ReturnMetrics
{
    private const double DaysPerYear = 365.0;
    private const int MaxIterations = 128;
    private const double Tolerance = 1e-9;

    /// <summary>
    /// Money-weighted return (internal rate of return on irregular flows).
    /// Amounts are from your wallet's view: paying money in is negative, receiving is positive,
    /// and the closing portfolio value is the final positive amount.
    /// Returns null when the flows never change sign or the solver does not converge.
    /// </summary>
    public static double? Xirr(IReadOnlyList<DatedAmount> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        if (flows.Count < 2)
        {
            return null;
        }

        var ordered = flows.OrderBy(f => f.Date).ToArray();
        if (!ordered.Any(f => f.Amount > 0m) || !ordered.Any(f => f.Amount < 0m))
        {
            return null;
        }

        var start = ordered[0].Date;
        var years = ordered.Select(f => (f.Date.DayNumber - start.DayNumber) / DaysPerYear).ToArray();
        var amounts = ordered.Select(f => (double)f.Amount).ToArray();

        return SolveNewton(amounts, years) ?? SolveBisection(amounts, years);
    }

    /// <summary>
    /// XIRR straight from a replayed ledger: external flows (positive when money entered the
    /// portfolio) are inverted to the wallet's view, then the closing value closes the series.
    /// All amounts must already be in <paramref name="finalValue"/>'s currency.
    /// </summary>
    public static double? Xirr(IEnumerable<ExternalFlow> externalFlows, Money finalValue, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(externalFlows);

        var flows = new List<DatedAmount>();
        foreach (var flow in externalFlows)
        {
            if (!string.Equals(flow.Amount.Currency, finalValue.Currency, StringComparison.Ordinal))
            {
                throw new LedgerException(
                    $"计算收益率前需先折算为同一货币：现金流是 {flow.Amount.Currency}，期末价值是 {finalValue.Currency}。");
            }

            flows.Add(new DatedAmount(flow.Date, -flow.Amount.Amount));
        }

        flows.Add(new DatedAmount(asOf, finalValue.Amount));
        return Xirr(flows);
    }

    /// <summary>
    /// Modified Dietz period return — the practical stand-in for true time-weighted return when
    /// you do not have a valuation on every flow date. Flows are positive when money entered the
    /// portfolio. Returns null when there is nothing invested to earn a return on.
    /// </summary>
    public static double? ModifiedDietz(
        decimal beginValue,
        decimal endValue,
        IReadOnlyList<DatedAmount> flows,
        DateOnly start,
        DateOnly end)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var totalDays = end.DayNumber - start.DayNumber;
        if (totalDays <= 0)
        {
            return null;
        }

        var netFlow = 0m;
        var weightedFlow = 0m;
        foreach (var flow in flows)
        {
            if (flow.Date < start || flow.Date > end)
            {
                continue;
            }

            netFlow += flow.Amount;
            var weight = (decimal)((totalDays - (flow.Date.DayNumber - start.DayNumber)) / (double)totalDays);
            weightedFlow += weight * flow.Amount;
        }

        var denominator = beginValue + weightedFlow;
        if (denominator == 0m)
        {
            return null;
        }

        return (double)((endValue - beginValue - netFlow) / denominator);
    }

    /// <summary>
    /// True time-weighted return, linking sub-period returns between valuation points.
    /// A flow dated on a valuation point is treated as arriving immediately after that valuation.
    /// Returns null when fewer than two valuation points exist or a sub-period starts from zero.
    /// </summary>
    public static double? TimeWeighted(
        IReadOnlyList<ValuationPoint> points,
        IReadOnlyList<DatedAmount> flows)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(flows);
        if (points.Count < 2)
        {
            return null;
        }

        var ordered = points.OrderBy(p => p.Date).ToArray();
        var linked = 1.0;

        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            var inflow = flows
                .Where(f => f.Date >= previous.Date && f.Date < current.Date)
                .Sum(f => f.Amount);

            var invested = previous.Value + inflow;
            if (invested == 0m)
            {
                return null;
            }

            linked *= (double)(current.Value / invested);
        }

        return linked - 1.0;
    }

    /// <summary>Converts a cumulative return over <paramref name="days"/> into an annual rate.</summary>
    public static double? Annualize(double totalReturn, int days)
    {
        if (days <= 0 || totalReturn <= -1.0)
        {
            return null;
        }

        return Math.Pow(1.0 + totalReturn, DaysPerYear / days) - 1.0;
    }

    private static double? SolveNewton(double[] amounts, double[] years)
    {
        var rate = 0.1;
        for (var i = 0; i < MaxIterations; i++)
        {
            var (value, derivative) = NpvAndDerivative(amounts, years, rate);
            if (Math.Abs(derivative) < double.Epsilon)
            {
                return null;
            }

            var next = rate - value / derivative;
            if (double.IsNaN(next) || double.IsInfinity(next) || next <= -1.0)
            {
                return null;
            }

            if (Math.Abs(next - rate) < Tolerance)
            {
                return Math.Abs(Npv(amounts, years, next)) < 1e-6 ? next : null;
            }

            rate = next;
        }

        return null;
    }

    private static double? SolveBisection(double[] amounts, double[] years)
    {
        var low = -0.9999999;
        var high = 100.0;
        var lowValue = Npv(amounts, years, low);
        var highValue = Npv(amounts, years, high);
        if (double.IsNaN(lowValue) || double.IsNaN(highValue) || lowValue * highValue > 0)
        {
            return null;
        }

        for (var i = 0; i < MaxIterations * 4; i++)
        {
            var mid = (low + high) / 2.0;
            var midValue = Npv(amounts, years, mid);
            if (Math.Abs(midValue) < 1e-9 || (high - low) / 2.0 < Tolerance)
            {
                return mid;
            }

            if (lowValue * midValue < 0)
            {
                high = mid;
            }
            else
            {
                low = mid;
                lowValue = midValue;
            }
        }

        return null;
    }

    private static double Npv(double[] amounts, double[] years, double rate)
    {
        var total = 0.0;
        for (var i = 0; i < amounts.Length; i++)
        {
            total += amounts[i] / Math.Pow(1.0 + rate, years[i]);
        }

        return total;
    }

    private static (double Value, double Derivative) NpvAndDerivative(double[] amounts, double[] years, double rate)
    {
        var value = 0.0;
        var derivative = 0.0;
        for (var i = 0; i < amounts.Length; i++)
        {
            var discount = Math.Pow(1.0 + rate, years[i]);
            value += amounts[i] / discount;
            derivative -= years[i] * amounts[i] / (discount * (1.0 + rate));
        }

        return (value, derivative);
    }
}
