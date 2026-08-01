"""Builds a daily PE/PB series from prices and per-share fundamentals.

This is what a data vendor does internally: the multiple moves every day because the price does,
while the denominator only steps when a new report is published. Doing it here rather than
buying it means US and HK names get the same percentile treatment A-shares already had.

It sits in ``market`` rather than ``research`` because it *produces* market data. The research
layer is the consumer — it takes the resulting series and says where today sits inside it.

Two rules keep the result honest:

* A day uses only the report that was **public on that day**. Using the report that covers the
  period would let January prices be divided by earnings announced in April, which makes every
  historical valuation look better-informed than it was and biases the percentile.
* A negative or zero denominator produces no multiple. A loss-making quarter gives a negative
  PE, which is not "cheap" — it is undefined — and dropping it into a distribution corrupts
  every percentile taken from it.
"""

from __future__ import annotations

from datetime import date
from typing import Iterable, Sequence

from caishenfolio_core.data.fundamentals import PerShareFundamental
from caishenfolio_core.data.models import OhlcvBar, ValuationPoint


def reconstruct_valuation(
    bars: Sequence[OhlcvBar],
    fundamentals: Sequence[PerShareFundamental],
) -> list[ValuationPoint]:
    """One point per bar that has a published report behind it. Earlier bars are dropped."""
    if not bars or not fundamentals:
        return []

    published = sorted(fundamentals, key=lambda f: f.effective_date)
    points: list[ValuationPoint] = []
    cursor = 0
    current: PerShareFundamental | None = None

    for bar in sorted(bars, key=lambda b: b.timestamp_utc):
        day = bar.timestamp_utc.date()

        # Advance to the newest report published on or before this day.
        while cursor < len(published) and published[cursor].effective_date <= day:
            current = published[cursor]
            cursor += 1

        if current is None:
            # Nothing had been published yet; there is no multiple to state.
            continue

        points.append(
            ValuationPoint(
                as_of=day,
                pe=_ratio(bar.close, current.eps_ttm),
                pb=_ratio(bar.close, current.bps),
            )
        )

    return points


def _ratio(price: float, per_share: float | None) -> float | None:
    if per_share is None or per_share <= 0 or price <= 0:
        return None
    return price / per_share


def describe_method(fundamentals: Iterable[PerShareFundamental]) -> str:
    """One line the UI can show, so a computed multiple is never mistaken for a vendor's."""
    items = list(fundamentals)
    if not items:
        return ""

    estimated = any(item.effective_date_estimated for item in items)
    span = f"{items[0].period_end:%Y} 年起"
    base = f"PE/PB 由收盘价 ÷ 最近一期已公布的每股数据推算（{span}，共 {len(items)} 期报告）"
    return base + (
        "；该市场数据源未提供公告日期，按财年结束后 90 天估算，早期分位可能有偏差。"
        if estimated
        else "；使用财报公告日，未使用当时尚未公布的数据。"
    )
