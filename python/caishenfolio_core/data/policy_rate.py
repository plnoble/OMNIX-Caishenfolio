"""The policy-rate model, shared by the fetchers in ``market`` and the carry panel in ``research``.

It lives in ``data`` so neither of those packages has to import the other.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from typing import Any


@dataclass(frozen=True, slots=True)
class PolicyRate:
    """One central-bank rate as an annual decimal (0.0363 is 3.63%).

    ``stale`` marks a value that could not be refreshed from its source. Such a rate is still
    usable — the panel has to render — but every layer above must be able to say it is old
    rather than present it as today's number.
    """

    currency: str
    rate: float
    name: str
    source: str
    as_of: date | None = None
    stale: bool = False
    note: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "currency": self.currency,
            "rate": self.rate,
            "name": self.name,
            "source": self.source,
            "as_of": self.as_of.isoformat() if self.as_of else None,
            "stale": self.stale,
            "note": self.note,
        }


#: Last-resort values, used only when a live fetch fails. Each records the date it was last
#: checked against the real rate, so its age is visible instead of implied.
FALLBACK_POLICY_RATES: dict[str, PolicyRate] = {
    "CNY": PolicyRate("CNY", 0.030, "1年期LPR", "内置默认值", date(2026, 8, 1), True,
                      "未取得实时利率，使用内置值。"),
    "USD": PolicyRate("USD", 0.0363, "联邦基金有效利率(EFFR)", "内置默认值", date(2026, 8, 1), True,
                      "未取得实时利率，使用内置值。"),
    "HKD": PolicyRate("HKD", 0.0400, "金管局基本利率", "内置默认值", date(2026, 8, 1), True,
                      "未取得实时利率，使用内置值。"),
    "JPY": PolicyRate("JPY", 0.010, "日本央行政策利率", "内置默认值", date(2026, 8, 1), True,
                      "未取得实时利率，使用内置值。"),
    "EUR": PolicyRate("EUR", 0.024, "欧央行主要再融资利率(MRO)", "内置默认值", date(2026, 8, 1), True,
                      "未取得实时利率，使用内置值。"),
}
