from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import date, datetime
from enum import StrEnum
from typing import Generic, TypeVar

from caishenfolio_core.data.markets import AssetClass, MarketRegion, canonical_exchange

_SYMBOL_RE = re.compile(r"^(?P<exchange>[A-Za-z0-9.]+):(?P<code>[A-Za-z0-9.\-]+)$")

T = TypeVar("T")

__all__ = [
    "Adjustment",
    "AssetClass",
    "FinancialPeriod",
    "FxQuote",
    "MarketRegion",
    "NavPoint",
    "OhlcvBar",
    "ProviderResult",
    "Quote",
    "SymbolId",
    "ValuationPoint",
]


class Adjustment(StrEnum):
    RAW = "raw"
    FORWARD = "forward"
    BACKWARD = "backward"
    UNKNOWN = "unknown"


@dataclass(frozen=True, slots=True)
class SymbolId:
    exchange: str
    code: str

    @property
    def value(self) -> str:
        return f"{self.exchange}:{self.code}"

    @classmethod
    def parse(cls, raw: str) -> SymbolId:
        symbol = cls.try_parse(raw)
        if symbol is None:
            raise ValueError(
                f"Invalid symbol '{raw}'. Expected EXCHANGE:SYMBOL (e.g. SSE:600000, NASDAQ:AAPL)."
            )
        return symbol

    @classmethod
    def try_parse(cls, raw: str | None) -> SymbolId | None:
        if raw is None or not str(raw).strip():
            return None
        match = _SYMBOL_RE.match(str(raw).strip())
        if not match:
            return None
        return cls(
            exchange=match.group("exchange").upper(),
            code=match.group("code").upper(),
        )

    def normalized(self) -> SymbolId:
        """Resolves venue aliases so the ledger keeps one identity per instrument
        (``SH:600000`` and ``SSE:600000`` collapse). Unknown venues are preserved as-is."""
        canonical = canonical_exchange(self.exchange)
        if canonical is None or canonical == self.exchange:
            return self
        return SymbolId(exchange=canonical, code=self.code)

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True, slots=True)
class OhlcvBar:
    timestamp_utc: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float
    currency: str
    adjustment: Adjustment
    provider: str
    amount: float | None = None
    provenance: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class Quote:
    """Latest observed price for one instrument. Valuation consumes this, not a full bar series."""

    symbol: str
    price: float
    currency: str
    as_of: date
    provider: str
    provenance: dict[str, str] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        provenance = dict(self.provenance)
        return {
            "symbol": self.symbol,
            "price": self.price,
            "currency": self.currency,
            "as_of": self.as_of.isoformat(),
            "provider": self.provider,
            # Lifted out of provenance so the desktop can read them without parsing a bag of strings.
            "source_count": int(provenance.get("cross_check_count", 0) or 0),
            "spread_pct": float(provenance.get("cross_check_spread_pct", 0) or 0),
            "sources": provenance.get("cross_check_sources", ""),
            "outliers": provenance.get("cross_check_outliers", ""),
            "provenance": provenance,
        }


@dataclass(frozen=True, slots=True)
class NavPoint:
    """One daily NAV observation for an off-exchange fund.

    Kept separate from :class:`OhlcvBar` on purpose: a fund has no open/high/low and no
    volume, so forcing it into a bar shape produced three fabricated fields per day.
    """

    as_of: date
    nav: float
    currency: str
    provider: str
    accumulated_nav: float | None = None
    daily_return: float | None = None
    provenance: dict[str, str] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        return {
            "as_of": self.as_of.isoformat(),
            "nav": self.nav,
            "accumulated_nav": self.accumulated_nav,
            "daily_return": self.daily_return,
            "currency": self.currency,
            "provider": self.provider,
            "provenance": dict(self.provenance),
        }


@dataclass(frozen=True, slots=True)
class ValuationPoint:
    """One day's valuation multiples. The history of these is what a percentile is taken over."""

    as_of: date
    pe: float | None = None
    pb: float | None = None
    dividend_yield: float | None = None

    def to_dict(self) -> dict[str, object]:
        return {
            "as_of": self.as_of.isoformat(),
            "pe": self.pe,
            "pb": self.pb,
            "dividend_yield": self.dividend_yield,
        }


@dataclass(frozen=True, slots=True)
class FinancialPeriod:
    """One reporting period's headline figures, as filed."""

    period: str
    revenue: float | None = None
    net_profit: float | None = None
    eps: float | None = None
    roe: float | None = None
    revenue_growth: float | None = None
    profit_growth: float | None = None

    def to_dict(self) -> dict[str, object]:
        return {
            "period": self.period,
            "revenue": self.revenue,
            "net_profit": self.net_profit,
            "eps": self.eps,
            "roe": self.roe,
            "revenue_growth": self.revenue_growth,
            "profit_growth": self.profit_growth,
        }


@dataclass(frozen=True, slots=True)
class FxQuote:
    """``rate`` units of ``quote_currency`` per single unit of ``base_currency``."""

    base_currency: str
    quote_currency: str
    rate: float
    as_of: date
    provider: str
    provenance: dict[str, str] = field(default_factory=dict)

    @property
    def symbol(self) -> str:
        return f"FX:{self.base_currency}{self.quote_currency}"

    def to_dict(self) -> dict[str, object]:
        return {
            "symbol": self.symbol,
            "base_currency": self.base_currency,
            "quote_currency": self.quote_currency,
            "rate": self.rate,
            "as_of": self.as_of.isoformat(),
            "provider": self.provider,
            "provenance": dict(self.provenance),
        }


@dataclass(frozen=True, slots=True)
class ProviderResult(Generic[T]):
    ok: bool
    provider: str
    data: T | None = None
    warnings: tuple[str, ...] = ()
    error: str | None = None
    latency_ms: float | None = None
    from_cache: bool = False

    @classmethod
    def success(
        cls,
        provider: str,
        data: T,
        *,
        warnings: tuple[str, ...] = (),
        latency_ms: float | None = None,
        from_cache: bool = False,
    ) -> ProviderResult[T]:
        return cls(
            ok=True,
            provider=provider,
            data=data,
            warnings=warnings,
            latency_ms=latency_ms,
            from_cache=from_cache,
        )

    @classmethod
    def failure(
        cls,
        provider: str,
        error: str,
        *,
        warnings: tuple[str, ...] = (),
    ) -> ProviderResult[T]:
        return cls(ok=False, provider=provider, error=error, warnings=warnings)
