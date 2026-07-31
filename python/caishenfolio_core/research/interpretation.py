"""Turning numbers into something a newcomer can actually read.

Three things happen here, in increasing order of usefulness:

1. A percentile becomes a band with a name — 历史低位, 偏低, 中性, 偏高, 历史高位.
2. Each indicator gets a plain-language explanation of what it measures and how to read it.
3. Most importantly, a *conditional distribution*: what actually happened next, historically,
   whenever this indicator sat where it sits today.

Point 3 is deliberately offered instead of a "buy" label. "Cheap, consider buying" hides the
part that matters; "of the 240 past days this cheap, holding a year made money 71% of the time,
median +18%, worst -22%" shows the odds *and* the downside, which is what lets someone decide
for themselves. Every distribution reports its sample size and its worst case, because a median
without a worst case is a sales pitch.

Percentiles here are computed on an expanding window — only data available at the time — so a
historical reading never benefits from knowing the future distribution.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from typing import Any

#: Band edges, in percentile terms.
_BANDS: tuple[tuple[float, str, str], ...] = (
    (20.0, "历史低位", "过去十年里，只有约两成的时间比现在更低。"),
    (40.0, "偏低", "低于历史中位水平。"),
    (60.0, "中性", "大致处在历史中间位置。"),
    (80.0, "偏高", "高于历史中位水平。"),
    (100.0, "历史高位", "过去十年里，只有约两成的时间比现在更高。"),
)

#: Below this many observations a conditional distribution is anecdote, not statistics.
MIN_CONDITIONAL_SAMPLES = 30

_EXPLANATIONS: dict[str, dict[str, str]] = {
    "市盈率 PE": {
        "what": "股价 ÷ 每股盈利。可以理解为「按当前盈利水平，多少年能回本」。",
        "read": "同一家公司自己比才有意义——不同行业的合理 PE 差别很大。分位低说明现在比它自己历史上大多数时候便宜。",
        "caveat": "盈利下滑时 PE 会被动变高，业绩暴雷前 PE 也常常显得很低。低 PE 不等于安全。",
    },
    "市净率 PB": {
        "what": "股价 ÷ 每股净资产。衡量你为公司账面资产付了几倍价钱。",
        "read": "重资产行业（银行、地产、钢铁）常用 PB；轻资产公司 PB 高是正常的。",
        "caveat": "净资产是会计口径，可能包含难以变现或已减值的资产。",
    },
    "股息率": {
        "what": "每股分红 ÷ 股价。相当于「只拿分红」的年化回报。",
        "read": "分位高说明当前股价对应的分红回报，比它自己历史上大多数时候更划算。",
        "caveat": "股息率高可能是因为股价跌了，也可能是一次性分红，不代表未来还能分这么多。",
    },
    "carry": {
        "what": "两种货币的利率差。持有高息货币、以低息货币计价时，每年大致能多拿这个百分比。",
        "read": "利差 4% 意味着持有 100 万等值资产，一年利息差约 4 万——前提是汇率不动。",
        "caveat": "汇率一天波动 1% 很常见，几天就能抵消一年的利差。利差说明长期倾向，不说明何时该换。",
    },
}


@dataclass(frozen=True, slots=True)
class Band:
    label: str
    description: str
    percentile: float

    def to_dict(self) -> dict[str, Any]:
        return {"label": self.label, "description": self.description, "percentile": self.percentile}


def band_for(percentile: float | None) -> Band | None:
    """Names the region a percentile falls in. A name, not a judgement about what to do."""
    if percentile is None:
        return None
    for edge, label, description in _BANDS:
        if percentile <= edge:
            return Band(label, description, percentile)
    return Band(_BANDS[-1][1], _BANDS[-1][2], percentile)


def explain(metric_name: str) -> dict[str, str]:
    """What the indicator measures, how to read it, and what it hides."""
    return dict(_EXPLANATIONS.get(metric_name, {}))


@dataclass(frozen=True, slots=True)
class ForwardOutcome:
    """What happened next, historically, from a comparable starting point."""

    horizon_days: int
    samples: int
    win_rate: float | None
    median_return: float | None
    best_return: float | None
    worst_return: float | None
    average_return: float | None

    @property
    def is_reliable(self) -> bool:
        return self.samples >= MIN_CONDITIONAL_SAMPLES

    def to_dict(self) -> dict[str, Any]:
        return {
            "horizon_days": self.horizon_days,
            "samples": self.samples,
            "win_rate": self.win_rate,
            "median_return": self.median_return,
            "best_return": self.best_return,
            "worst_return": self.worst_return,
            "average_return": self.average_return,
            "reliable": self.is_reliable,
        }

    def summary(self) -> str:
        if self.samples == 0:
            return f"历史上没有出现过可比的情形，无法给出 {self.horizon_days} 天后的参考。"
        if not self.is_reliable:
            return (
                f"历史上只出现过 {self.samples} 次可比情形，样本太少，"
                f"{self.horizon_days} 天后的结果不具统计意义。"
            )
        return (
            f"历史上出现过 {self.samples} 次可比情形，{self.horizon_days} 天后："
            f"{self.win_rate:.0%} 的情况是上涨的，涨跌幅中位数 {self.median_return:+.1%}，"
            f"最好 {self.best_return:+.1%}，最差 {self.worst_return:+.1%}。"
        )


def expanding_percentiles(values: list[float | None], minimum: int = 60) -> list[float | None]:
    """Percentile of each point against only the points before it.

    Using the full history would let a past reading know its own future — the resulting
    statistics would look far better than anything achievable in real time.
    """
    out: list[float | None] = []
    seen: list[float] = []
    for value in values:
        if value is None:
            out.append(None)
            continue
        if len(seen) < minimum:
            out.append(None)
        else:
            below = sum(1 for v in seen if v < value)
            equal = sum(1 for v in seen if v == value)
            out.append((below + equal / 2.0) / len(seen) * 100.0)
        seen.append(value)
    return out


def conditional_forward_outcome(
    metric_values: list[float | None],
    closes: list[float],
    percentile_low: float,
    percentile_high: float,
    horizon_days: int,
    minimum_history: int = 60,
) -> ForwardOutcome:
    """Returns achieved ``horizon_days`` later, on every past day whose percentile fell in range.

    ``metric_values`` and ``closes`` must be the same length and in the same date order.
    """
    if len(metric_values) != len(closes):
        raise ValueError("指标序列与价格序列长度必须一致。")
    if horizon_days < 1:
        raise ValueError(f"持有天数必须至少为 1（收到 {horizon_days}）。")

    percentiles = expanding_percentiles(metric_values, minimum_history)
    returns: list[float] = []

    for i, percentile in enumerate(percentiles):
        forward = i + horizon_days
        if percentile is None or forward >= len(closes):
            continue
        if not percentile_low <= percentile <= percentile_high:
            continue
        entry = closes[i]
        if entry <= 0:
            continue
        returns.append(closes[forward] / entry - 1.0)

    if not returns:
        return ForwardOutcome(horizon_days, 0, None, None, None, None, None)

    ordered = sorted(returns)
    middle = len(ordered) // 2
    median = (
        ordered[middle]
        if len(ordered) % 2 == 1
        else (ordered[middle - 1] + ordered[middle]) / 2.0
    )

    return ForwardOutcome(
        horizon_days=horizon_days,
        samples=len(returns),
        win_rate=sum(1 for r in returns if r > 0) / len(returns),
        median_return=median,
        best_return=ordered[-1],
        worst_return=ordered[0],
        average_return=sum(returns) / len(returns),
    )


@dataclass(frozen=True, slots=True)
class MetricReading:
    """One indicator, fully explained: where it stands, what it means, what followed before."""

    name: str
    current: float | None
    percentile: float | None
    band: Band | None
    explanation: dict[str, str]
    outcomes: list[ForwardOutcome]
    notes: list[str]

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "current": self.current,
            "percentile": self.percentile,
            "band": self.band.to_dict() if self.band else None,
            "explanation": self.explanation,
            "outcomes": [o.to_dict() for o in self.outcomes],
            "outcome_summaries": [o.summary() for o in self.outcomes],
            "notes": self.notes,
        }


def read_metric(
    name: str,
    current: float | None,
    percentile: float | None,
    metric_values: list[float | None] | None = None,
    closes: list[float] | None = None,
    horizons: tuple[int, ...] = (60, 250),
    band_width: float = 10.0,
) -> MetricReading:
    """Assembles the full reading for one indicator.

    The conditional outcomes look at days whose percentile was within ``band_width`` of today's,
    so "what happened from here before" means from a genuinely comparable starting point.
    """
    band = band_for(percentile)
    notes: list[str] = []
    outcomes: list[ForwardOutcome] = []

    if percentile is not None and metric_values and closes:
        low = max(0.0, percentile - band_width)
        high = min(100.0, percentile + band_width)
        for horizon in horizons:
            outcome = conditional_forward_outcome(metric_values, closes, low, high, horizon)
            outcomes.append(outcome)
            if outcome.samples and not outcome.is_reliable:
                notes.append(
                    f"{horizon} 天区间的可比样本只有 {outcome.samples} 个，不足以支撑结论。"
                )

    if percentile is None:
        notes.append("缺少足够的历史数据，无法判断当前处于什么位置。")

    notes.append(
        "以上是历史统计，不是预测。同样的位置，未来完全可能走出历史上没出现过的结果。"
    )

    return MetricReading(
        name=name,
        current=current,
        percentile=percentile,
        band=band,
        explanation=explain(name),
        outcomes=outcomes,
        notes=notes,
    )


@dataclass(frozen=True, slots=True)
class InstrumentReading:
    symbol: str
    as_of: date
    metrics: list[MetricReading]

    def to_dict(self) -> dict[str, Any]:
        return {
            "symbol": self.symbol,
            "as_of": self.as_of.isoformat(),
            "metrics": [m.to_dict() for m in self.metrics],
            "disclaimer": (
                "研究/模拟结论，非投资建议。分档与历史统计只描述位置与过往结果，"
                "不预测未来，也不构成买卖依据。"
            ),
        }
