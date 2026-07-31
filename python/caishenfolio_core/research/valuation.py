"""Where a valuation sits inside its own history.

"PE is 28" says almost nothing on its own. "PE is 28, the 12th percentile of its last ten
years" is a fact about position in a distribution, and it is what gives 低买 an objective ruler
instead of a feeling.

This module states positions. It does not say whether a percentile is a reason to act — a low
multiple can be low because the business is deteriorating, and this calculation cannot tell the
difference.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from typing import Any

from caishenfolio_core.data.models import ValuationPoint
from caishenfolio_core.research.indicators import percentile_rank

#: Below this many observations a percentile is noise dressed up as a number.
MIN_HISTORY_POINTS = 60


@dataclass(frozen=True, slots=True)
class MetricPosition:
    """One multiple: its current value and where that sits in its own history."""

    name: str
    current: float | None
    percentile: float | None
    low: float | None
    high: float | None
    median: float | None
    sample_size: int

    @property
    def is_reliable(self) -> bool:
        return self.percentile is not None and self.sample_size >= MIN_HISTORY_POINTS

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "current": self.current,
            "percentile": self.percentile,
            "low": self.low,
            "high": self.high,
            "median": self.median,
            "sample_size": self.sample_size,
            "reliable": self.is_reliable,
        }


@dataclass(frozen=True, slots=True)
class ValuationPosition:
    symbol: str
    as_of: date
    span_start: date | None
    span_end: date | None
    metrics: list[MetricPosition]
    notes: list[str]
    provider: str = ""

    def metric(self, name: str) -> MetricPosition | None:
        return next((m for m in self.metrics if m.name == name), None)

    def to_dict(self) -> dict[str, Any]:
        return {
            "symbol": self.symbol,
            "as_of": self.as_of.isoformat(),
            "span_start": self.span_start.isoformat() if self.span_start else None,
            "span_end": self.span_end.isoformat() if self.span_end else None,
            "metrics": [m.to_dict() for m in self.metrics],
            "notes": self.notes,
            "provider": self.provider,
            "disclaimer": "研究/模拟结论，非投资建议。分位只描述历史位置，不代表便宜或应当买入。",
        }


def position_from_history(
    symbol: str,
    history: list[ValuationPoint],
    provider: str = "",
) -> ValuationPosition:
    """Computes each multiple's position within the supplied history."""
    ordered = sorted(history, key=lambda p: p.as_of)
    latest = ordered[-1] if ordered else None
    notes: list[str] = []

    metrics = [
        _position("市盈率 PE", [p.pe for p in ordered], latest.pe if latest else None),
        _position("市净率 PB", [p.pb for p in ordered], latest.pb if latest else None),
        _position(
            "股息率",
            [p.dividend_yield for p in ordered],
            latest.dividend_yield if latest else None,
        ),
    ]

    for metric in metrics:
        if metric.current is None:
            notes.append(f"{metric.name}：当前值缺失，无法定位。")
        elif metric.sample_size < MIN_HISTORY_POINTS:
            notes.append(
                f"{metric.name}：历史样本仅 {metric.sample_size} 个，分位不可靠。"
            )

    if any(m.current is not None and m.current < 0 for m in metrics if m.name.startswith("市盈率")):
        # A negative PE means the company lost money; ranking it against profitable years is
        # arithmetic without meaning.
        notes.append("市盈率为负（亏损），其分位没有可比意义。")

    return ValuationPosition(
        symbol=symbol,
        as_of=latest.as_of if latest else date.today(),
        span_start=ordered[0].as_of if ordered else None,
        span_end=ordered[-1].as_of if ordered else None,
        metrics=metrics,
        notes=notes,
        provider=provider,
    )


def _position(name: str, values: list[float | None], current: float | None) -> MetricPosition:
    usable = [v for v in values if v is not None]
    if not usable or current is None:
        return MetricPosition(name, current, None, None, None, None, len(usable))

    ordered = sorted(usable)
    middle = len(ordered) // 2
    median = (
        ordered[middle]
        if len(ordered) % 2 == 1
        else (ordered[middle - 1] + ordered[middle]) / 2.0
    )

    return MetricPosition(
        name=name,
        current=current,
        percentile=percentile_rank(usable, current),
        low=ordered[0],
        high=ordered[-1],
        median=median,
        sample_size=len(usable),
    )
