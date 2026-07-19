from __future__ import annotations

from datetime import date
from typing import Protocol

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import Adjustment, ProviderResult, OhlcvBar
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
