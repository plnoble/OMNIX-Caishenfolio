"""Honest evaluation of a backtest.

A total return on its own flatters every rule. What decides whether a rule is worth anything is
the rest: what it cost to hold through the worst stretch, how often it was wrong, how long the
losing runs were, whether it beat simply holding after costs — and above all whether it still
works on data it was not chosen on.

Nothing here recommends a strategy. It reports what a rule did and states plainly when the
evidence does not support it.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime
from typing import Any, Callable

#: Trading days in a year, for annualising.
_TRADING_DAYS = 252

#: An out-of-sample result this much worse than in-sample is the classic overfitting signature.
_DEGRADATION_ALARM = 0.5


@dataclass(frozen=True, slots=True)
class PerformanceMetrics:
    bars: int
    trades: int
    total_return: float | None
    annualized_return: float | None
    buy_hold_return: float | None
    excess_over_buy_hold: float | None
    max_drawdown: float | None
    max_drawdown_bars: int
    volatility: float | None
    return_over_max_drawdown: float | None
    win_rate: float | None
    profit_factor: float | None
    max_consecutive_losses: int
    average_win: float | None
    average_loss: float | None

    def to_dict(self) -> dict[str, Any]:
        return {
            "bars": self.bars,
            "trades": self.trades,
            "total_return": self.total_return,
            "annualized_return": self.annualized_return,
            "buy_hold_return": self.buy_hold_return,
            "excess_over_buy_hold": self.excess_over_buy_hold,
            "max_drawdown": self.max_drawdown,
            "max_drawdown_bars": self.max_drawdown_bars,
            "volatility": self.volatility,
            "return_over_max_drawdown": self.return_over_max_drawdown,
            "win_rate": self.win_rate,
            "profit_factor": self.profit_factor,
            "max_consecutive_losses": self.max_consecutive_losses,
            "average_win": self.average_win,
            "average_loss": self.average_loss,
        }


def evaluate(
    equity_curve: list[dict[str, Any]],
    trade_log: list[dict[str, Any]] | None = None,
    buy_hold_return: float | None = None,
) -> PerformanceMetrics:
    """Turns an equity curve and trade log into the numbers that decide whether a rule holds up."""
    equities = [float(point["equity"]) for point in equity_curve if "equity" in point]
    trades = _closed_trades(trade_log or [])

    if len(equities) < 2:
        return PerformanceMetrics(
            bars=len(equities),
            trades=len(trades),
            total_return=None,
            annualized_return=None,
            buy_hold_return=buy_hold_return,
            excess_over_buy_hold=None,
            max_drawdown=None,
            max_drawdown_bars=0,
            volatility=None,
            return_over_max_drawdown=None,
            win_rate=None,
            profit_factor=None,
            max_consecutive_losses=0,
            average_win=None,
            average_loss=None,
        )

    start, end = equities[0], equities[-1]
    total_return = (end / start) - 1.0 if start > 0 else None
    drawdown, drawdown_bars = _max_drawdown(equities)
    volatility = _volatility(equities)
    annualized = _annualize(total_return, len(equities))

    wins = [t for t in trades if t > 0]
    losses = [t for t in trades if t < 0]
    gross_win = sum(wins)
    gross_loss = -sum(losses)

    return PerformanceMetrics(
        bars=len(equities),
        trades=len(trades),
        total_return=total_return,
        annualized_return=annualized,
        buy_hold_return=buy_hold_return,
        excess_over_buy_hold=(
            None if total_return is None or buy_hold_return is None
            else total_return - buy_hold_return
        ),
        max_drawdown=drawdown,
        max_drawdown_bars=drawdown_bars,
        volatility=volatility,
        # How much return each unit of worst-case pain bought.
        return_over_max_drawdown=(
            None if total_return is None or not drawdown else abs(total_return / drawdown)
        ),
        win_rate=(len(wins) / len(trades)) if trades else None,
        profit_factor=(gross_win / gross_loss) if gross_loss > 0 else None,
        max_consecutive_losses=_max_consecutive_losses(trades),
        average_win=(gross_win / len(wins)) if wins else None,
        average_loss=(-gross_loss / len(losses)) if losses else None,
    )


@dataclass(frozen=True, slots=True)
class OutOfSampleReport:
    """In-sample versus out-of-sample. The gap between them is the point."""

    split_index: int
    split_date: str
    in_sample: PerformanceMetrics
    out_of_sample: PerformanceMetrics
    degradation: float | None
    findings: list[str] = field(default_factory=list)

    @property
    def survives_out_of_sample(self) -> bool:
        """True only when the rule still made money out of sample and did not collapse."""
        oos = self.out_of_sample.annualized_return
        if oos is None or oos <= 0:
            return False
        return self.degradation is None or self.degradation < _DEGRADATION_ALARM

    def to_dict(self) -> dict[str, Any]:
        return {
            "split_index": self.split_index,
            "split_date": self.split_date,
            "in_sample": self.in_sample.to_dict(),
            "out_of_sample": self.out_of_sample.to_dict(),
            "degradation": self.degradation,
            "survives_out_of_sample": self.survives_out_of_sample,
            "findings": self.findings,
            "disclaimer": "研究/模拟结论，非投资建议。历史表现不代表未来。",
        }


def split_bars(bars: list[Any], ratio: float = 0.7) -> tuple[list[Any], list[Any]]:
    """Chronological split. Never shuffled: a rule tested on shuffled time sees the future."""
    if not 0.0 < ratio < 1.0:
        raise ValueError(f"切分比例必须在 0 与 1 之间（收到 {ratio}）。")
    cut = int(len(bars) * ratio)
    return bars[:cut], bars[cut:]


def out_of_sample_report(
    bars: list[Any],
    run: Callable[[list[Any]], Any],
    ratio: float = 0.7,
    minimum_bars: int = 60,
) -> OutOfSampleReport | None:
    """Runs ``run`` on the earlier slice, then on the later one it never saw.

    Returns None when either slice is too short to say anything — reporting a number from
    twenty bars would be worse than reporting nothing.
    """
    in_bars, out_bars = split_bars(bars, ratio)
    if len(in_bars) < minimum_bars or len(out_bars) < minimum_bars:
        return None

    in_result = run(in_bars)
    out_result = run(out_bars)

    in_metrics = evaluate(
        getattr(in_result, "equity_curve", []),
        getattr(in_result, "trade_log", []),
        getattr(in_result, "buy_hold_return", None),
    )
    out_metrics = evaluate(
        getattr(out_result, "equity_curve", []),
        getattr(out_result, "trade_log", []),
        getattr(out_result, "buy_hold_return", None),
    )

    degradation = _degradation(in_metrics.annualized_return, out_metrics.annualized_return)
    report = OutOfSampleReport(
        split_index=len(in_bars),
        split_date=_bar_date(out_bars[0]),
        in_sample=in_metrics,
        out_of_sample=out_metrics,
        degradation=degradation,
        findings=[],
    )
    return OutOfSampleReport(
        report.split_index,
        report.split_date,
        in_metrics,
        out_metrics,
        degradation,
        describe(report),
    )


def describe(report: OutOfSampleReport) -> list[str]:
    """Plain statements about what the numbers show. Facts and cautions, never instructions."""
    findings: list[str] = []
    ins, oos = report.in_sample, report.out_of_sample

    if oos.trades == 0:
        findings.append("样本外区间内没有产生任何交易，无法判断规则是否有效。")
        return findings

    if oos.annualized_return is not None and oos.annualized_return <= 0:
        findings.append(
            f"样本外年化为 {oos.annualized_return:.1%}，即在未参与调参的数据上是亏损的。"
        )

    if report.degradation is not None and report.degradation >= _DEGRADATION_ALARM:
        findings.append(
            f"样本外表现比样本内衰减 {report.degradation:.0%}，这是参数过拟合的典型特征。"
        )

    if oos.excess_over_buy_hold is not None and oos.excess_over_buy_hold <= 0:
        findings.append(
            f"扣除成本后样本外跑输买入持有 {abs(oos.excess_over_buy_hold):.1%}，"
            "即这套规则没有跑赢什么都不做。"
        )

    if oos.trades < 10:
        findings.append(f"样本外只有 {oos.trades} 笔交易，样本太小，结论不稳健。")

    if oos.max_consecutive_losses >= 5:
        findings.append(
            f"样本外最多连续亏损 {oos.max_consecutive_losses} 笔——实盘时需要能扛住这段。"
        )

    if oos.max_drawdown is not None and oos.max_drawdown <= -0.2:
        findings.append(
            f"样本外最大回撤 {oos.max_drawdown:.1%}，持续 {oos.max_drawdown_bars} 根K线。"
        )

    if not findings:
        findings.append(
            f"样本外年化 {oos.annualized_return:.1%}，未出现明显衰减；"
            "但单次回测不构成证据，换标的或换区间需重新检验。"
        )

    return findings


# --- internals ---------------------------------------------------------------------


def _closed_trades(trade_log: list[dict[str, Any]]) -> list[float]:
    """Per-trade returns, taken from the closing side of each round trip."""
    returns: list[float] = []
    for entry in trade_log:
        if entry.get("side") != "sell":
            continue
        value = entry.get("trade_return_grossish")
        if value is None:
            continue
        try:
            returns.append(float(value))
        except (TypeError, ValueError):
            continue
    return returns


def _max_drawdown(equities: list[float]) -> tuple[float | None, int]:
    """Worst peak-to-trough fall and how many bars it lasted."""
    peak = equities[0]
    peak_index = 0
    worst = 0.0
    worst_bars = 0

    for i, value in enumerate(equities):
        if value > peak:
            peak = value
            peak_index = i
            continue
        if peak <= 0:
            continue
        decline = (value / peak) - 1.0
        if decline < worst:
            worst = decline
            worst_bars = i - peak_index

    return (worst if worst < 0 else 0.0), worst_bars


def _volatility(equities: list[float]) -> float | None:
    """Annualised standard deviation of bar-to-bar returns."""
    changes = [
        (equities[i] / equities[i - 1]) - 1.0
        for i in range(1, len(equities))
        if equities[i - 1] > 0
    ]
    if len(changes) < 2:
        return None
    mean = sum(changes) / len(changes)
    variance = sum((c - mean) ** 2 for c in changes) / (len(changes) - 1)
    return (variance ** 0.5) * (_TRADING_DAYS ** 0.5)


def _annualize(total_return: float | None, bars: int) -> float | None:
    if total_return is None or bars < 2 or total_return <= -1.0:
        return None
    years = bars / _TRADING_DAYS
    if years <= 0:
        return None
    return (1.0 + total_return) ** (1.0 / years) - 1.0


def _degradation(in_sample: float | None, out_of_sample: float | None) -> float | None:
    """How much of the in-sample edge disappeared. 1.0 means all of it."""
    if in_sample is None or out_of_sample is None or in_sample <= 0:
        return None
    return max(0.0, (in_sample - out_of_sample) / in_sample)


def _max_consecutive_losses(trades: list[float]) -> int:
    longest = 0
    current = 0
    for value in trades:
        if value < 0:
            current += 1
            longest = max(longest, current)
        else:
            current = 0
    return longest


def _bar_date(bar: Any) -> str:
    timestamp = getattr(bar, "timestamp_utc", None)
    if isinstance(timestamp, (datetime, date)):
        return timestamp.strftime("%Y-%m-%d")
    return str(timestamp or "")
