"""Currency exposure, interest-rate differentials, and where a rate sits in its own history.

What this can honestly say about holding JPY versus USD is the carry — the interest-rate gap —
and where today's rate sits in its own range. What it cannot say is when to convert. The
2022-2024 yen is the standing counterexample: the differential was at a multi-decade extreme
and the currency still moved 30% one way, then gave a year of carry back in three days when the
trade unwound. Carry describes a long-run tendency; it does not time anything.

So this module reports positions and differentials, and says nothing about what to do with them.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date
from typing import Any, Mapping

from caishenfolio_core.data.policy_rate import FALLBACK_POLICY_RATES, PolicyRate
from caishenfolio_core.research.indicators import percentile_rank

#: Plain-number view of the built-in rates, kept so callers that only want numbers still work.
#: These are the last-resort values; :mod:`caishenfolio_core.market.policy_rates` fetches the
#: real ones. Anything served from here is reported as stale.
DEFAULT_POLICY_RATES: dict[str, float] = {
    currency: item.rate for currency, item in FALLBACK_POLICY_RATES.items()
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
    #: Where each rate came from and when, so a stale differential can be labelled as such.
    base_rate_info: PolicyRate | None = None
    quote_rate_info: PolicyRate | None = None

    @property
    def carry(self) -> float | None:
        """Annual rate advantage of holding the base currency. Negative means it costs."""
        if self.base_rate is None or self.quote_rate is None:
            return None
        return self.base_rate - self.quote_rate

    @property
    def rates_stale(self) -> bool:
        """True when either side could not be refreshed, so the gap may not be today's."""
        return any(item.stale for item in (self.base_rate_info, self.quote_rate_info) if item)

    def to_dict(self) -> dict[str, Any]:
        return {
            "pair": f"{self.base_currency}/{self.quote_currency}",
            "base_currency": self.base_currency,
            "quote_currency": self.quote_currency,
            "base_rate": self.base_rate,
            "quote_rate": self.quote_rate,
            "base_rate_info": self.base_rate_info.to_dict() if self.base_rate_info else None,
            "quote_rate_info": self.quote_rate_info.to_dict() if self.quote_rate_info else None,
            "rates_stale": self.rates_stale,
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
    policy_rates: Mapping[str, float | None | PolicyRate] | None = None,
    exposures: dict[str, float] | None = None,
    as_of: date | None = None,
) -> CarryPanel:
    """Assembles the panel.

    ``rates`` maps a currency to units of ``base_currency`` per unit of it; ``rate_history``
    holds past values of the same quote so today's can be placed within it. ``policy_rates``
    accepts either bare numbers or :class:`PolicyRate` records; the records carry the source and
    the as-of date, which is what lets the panel say when a differential is out of date.
    """
    base = base_currency.upper()
    policy = _normalize_policy_rates(policy_rates)
    history = rate_history or {}
    notes: list[str] = []

    base_info = policy.get(base)
    legs: list[CarryLeg] = []
    for currency in sorted(rates):
        currency = currency.upper()
        if currency == base:
            continue

        series = [v for v in history.get(currency, []) if v is not None]
        current = rates.get(currency)
        leg_info = policy.get(currency)
        legs.append(
            CarryLeg(
                base_currency=currency,
                quote_currency=base,
                base_rate=leg_info.rate if leg_info else None,
                quote_rate=base_info.rate if base_info else None,
                rate=current,
                percentile=percentile_rank(series, current) if series and current else None,
                low=min(series) if series else None,
                high=max(series) if series else None,
                sample_size=len(series),
                base_rate_info=leg_info,
                quote_rate_info=base_info,
            )
        )

        if leg_info is None:
            notes.append(f"{currency}：缺少利率数据，无法计算利差。")
        elif not series:
            notes.append(f"{currency}/{base}：缺少历史汇率，无法定位当前水平。")

    stale = sorted({
        item.currency
        for item in policy.values()
        if item.stale and (item.currency == base or item.currency in {leg.base_currency for leg in legs})
    })
    if stale:
        notes.append(
            f"{'、'.join(stale)} 的利率未能从数据源刷新，使用内置值，可能已过期，"
            "利差仅供参考。"
        )

    return CarryPanel(
        base_currency=base,
        as_of=as_of or date.today(),
        legs=legs,
        exposures=_exposures(base, exposures or {}, rates),
        notes=notes,
    )


def _normalize_policy_rates(
    supplied: Mapping[str, float | None | PolicyRate] | None,
) -> dict[str, PolicyRate]:
    """Accepts numbers or records, and fills the gaps from the built-ins marked stale.

    A currency explicitly supplied as ``None`` stays absent: the caller is saying it has no rate,
    and substituting the built-in would present a guess as an answer.
    """
    out: dict[str, PolicyRate] = dict(FALLBACK_POLICY_RATES)
    for currency, value in (supplied or {}).items():
        key = currency.upper()
        if value is None:
            out.pop(key, None)
        elif isinstance(value, PolicyRate):
            out[key] = value
        else:
            out[key] = PolicyRate(key, float(value), "利率", "调用方提供", None, False)
    return out


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
