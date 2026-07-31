from __future__ import annotations

from datetime import date
from typing import Protocol, runtime_checkable

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import (
    Adjustment,
    FxQuote,
    NavPoint,
    OhlcvBar,
    ProviderResult,
    Quote,
)
from caishenfolio_core.market.fixture import SymbolHit


class MarketDataProvider(Protocol):
    PROVIDER_CODE: str

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        ...

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        ...


@runtime_checkable
class QuoteCapable(Protocol):
    """Latest price for one instrument — what portfolio valuation needs."""

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        ...


@runtime_checkable
class NavCapable(Protocol):
    """Daily NAV series for off-exchange open-end funds (场外公募基金)."""

    def nav_series(self, symbol: str, start: date, end: date) -> ProviderResult[list[NavPoint]]:
        ...


@runtime_checkable
class FxCapable(Protocol):
    """Exchange rate between two currencies."""

    def fx_rate(self, base_currency: str, quote_currency: str) -> ProviderResult[FxQuote]:
        ...


def supports_quotes(provider: object) -> bool:
    return callable(getattr(provider, "latest_quote", None))


def supports_nav(provider: object) -> bool:
    return callable(getattr(provider, "nav_series", None))


def supports_fx(provider: object) -> bool:
    return callable(getattr(provider, "fx_rate", None))
