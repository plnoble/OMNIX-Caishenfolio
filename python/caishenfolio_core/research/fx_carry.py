"""Currency exposure, interest-rate differentials, and where a rate sits in its own history.

What this can honestly say about holding JPY versus USD is the carry — the interest-rate gap —
and where today's rate sits in its own range. What it cannot say is when to convert. The
2022-2024 yen is the standing counterexample: the differential was at a multi-decade extreme
and the currency still moved 30% one way, then gave a year of carry back in three days when the
trade unwound. Carry describes a long-run tendency; it does not time anything.

So this module reports positions and differentials, and says nothing about what to do with them.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from typing import Any

from caishenfolio_core.research.indicators import percentile_rank

#: Policy rates as annual decimals. Supplied by the caller in production; these are only the
#: fallback so the panel renders before any rate feed is configured, and they are labelled stale.
DEFAULT_POLICY_RATES: dict[str, float] = {
    "CNY": 0.030,
    "USD": 0.045,
    "HKD": 0.045,
    "JPY": 0.005,
}


@dataclass(frozen=True, slots=True)
class CarryLeg:
    """Holding ``base`` funded in ``quote``: the rate gap and where the pair trades."""

    base_currency: str
    quote_currency: str
    base_rate: float | None
    quote_rate: float | None
    rate: float | None
    percentile: float | None
    low: float | None
    high: float | None
    sample_size: int

    @property
    def carry(self) -> float | None:
        """Annual rate advantage of holding the base currency. Negative means it costs."""
        if self.base_rate is None or self.quote_rate is None:
            return None
        return self.base_rate - self.quote_rate

    def to_dict(self) -> dict[str, Any]:
        return {
            "pair": f"{self.base_currency}/{self.quote_currency}",
            "base_currency": self.base_currency,
            "quote_currency": self.quote_currency,
            "base_rate": self.base_rate,
            "quote_rate": self.quote_rate,
            "carry": self.carry,
            "rate": self.rate,
            "percentile": self.percentile,
            "low": self.low,
            "high": self.high,
            "sample_size": self.sample_size,
        }


@dataclass(frozen=True, slots=True)
class CurrencyExposure:
    currency: str
    amount: float
    value_in_base: float | None
    weight: float | None

    def to_dict(self) -> dict[str, Any]:
        return {
            "currency": self.currency,
            "amount": self.amount,
            "value_in_base": self.value_in_base,
            "weight": self.weight,
        }


@dataclass(frozen=True, slots=True)
class CarryPanel:
    base_currency: str
    as_of: date
    legs: list[CarryLeg]
    exposures: list[CurrencyExposure]
    notes: list[str]

    def to_dict(self) -> dict[str, Any]:
        return {
            "base_currency": self.base_currency,
            "as_of": self.as_of.isoformat(),
            "legs": [leg.to_dict() for leg in self.legs],
            "exposures": [item.to_dict() for item in self.exposures],
            "notes": self.notes,
            "disclaimer": (
                "研究/模拟结论，非投资建议。利差描述长期倾向，不预测汇率走向，也不构成换汇时点判断。"
            ),
        }


def build_panel(
    base_currency: str,
    rates: dict[str, float | None],
    rate_history: dict[str, list[float]] | None = None,
    policy_rates: dict[str, float] | None = None,
    exposures: dict[str, float] | None = None,
    as_of: date | None = None,
) -> CarryPanel:
    """Assembles the panel.

    ``rates`` maps a currency to units of ``base_currency`` per unit of it; ``rate_history``
    holds past values of the same quote so today's can be placed within it.
    """
    base = base_currency.upper()
    policy = dict(DEFAULT_POLICY_RATES)
    policy.update(policy_rates or {})
    history = rate_history or {}
    notes: list[str] = []

    legs: list[CarryLeg] = []
    for currency in sorted(rates):
        currency = currency.upper()
        if currency == base:
            continue

        series = [v for v in history.get(currency, []) if v is not None]
        current = rates.get(currency)
        legs.append(
            CarryLeg(
                base_currency=currency,
                quote_currency=base,
                base_rate=policy.get(currency),
                quote_rate=policy.get(base),
                rate=current,
                percentile=percentile_rank(series, current) if series and current else None,
                low=min(series) if series else None,
                high=max(series) if series else None,
                sample_size=len(series),
            )
        )

        if policy.get(currency) is None:
            notes.append(f"{currency}：缺少利率数据，无法计算利差。")
        elif not series:
            notes.append(f"{currency}/{base}：缺少历史汇率，无法定位当前水平。")

    if policy_rates is None:
        notes.append(
            "利率使用内置默认值，可能已过期；接入利率数据源后此项会自动更新。"
        )

    return CarryPanel(
        base_currency=base,
        as_of=as_of or date.today(),
        legs=legs,
        exposures=_exposures(base, exposures or {}, rates),
        notes=notes,
    )


def _exposures(
    base: str,
    balances: dict[str, float],
    rates: dict[str, float | None],
) -> list[CurrencyExposure]:
    converted: dict[str, float | None] = {}
    for currency, amount in balances.items():
        currency = currency.upper()
        if currency == base:
            converted[currency] = amount
            continue
        rate = rates.get(currency)
        converted[currency] = None if rate is None else amount * rate

    total = sum(v for v in converted.values() if v is not None)
    return [
        CurrencyExposure(
            currency=currency,
            amount=balances.get(currency, balances.get(currency.lower(), 0.0)),
            value_in_base=value,
            # An unconvertible balance has no weight rather than a weight of zero.
            weight=None if value is None or total <= 0 else value / total,
        )
        for currency, value in sorted(converted.items())
    ]
