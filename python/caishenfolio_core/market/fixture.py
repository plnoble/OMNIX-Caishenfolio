from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import (
    Adjustment,
    AssetClass,
    Market,
    OhlcvBar,
    ProviderResult,
    SymbolId,
)


@dataclass(frozen=True)
class SymbolSeed:
    symbol: str
    market: Market
    asset_class: AssetClass
    name: str
    currency: str
    base_close: float


@dataclass(frozen=True)
class SymbolHit:
    symbol: str
    market: Market
    asset_class: AssetClass
    name: str
    provider: str

    def to_dict(self) -> dict[str, object]:
        return {
            "symbol": self.symbol,
            "market": self.market.value,
            "asset_class": self.asset_class.value,
            "name": self.name,
            "provider": self.provider,
        }


class FixtureMarketDataProvider:
    PROVIDER_CODE = "fixture"

    _UNIVERSE: tuple[SymbolSeed, ...] = (
        SymbolSeed("SSE:600000", Market.ASHARE, AssetClass.EQUITY, "浦发银行", "CNY", 10.0),
        SymbolSeed("SZSE:000001", Market.ASHARE, AssetClass.EQUITY, "平安银行", "CNY", 12.5),
        SymbolSeed("HKEX:00700", Market.HK, AssetClass.EQUITY, "Tencent", "HKD", 320.0),
        SymbolSeed("NASDAQ:AAPL", Market.US, AssetClass.EQUITY, "Apple", "USD", 180.0),
        SymbolSeed("NYSE:SPY", Market.US, AssetClass.ETF, "SPDR S&P 500", "USD", 450.0),
    )

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        limit = max(1, min(limit, 50))
        q = (query or "").strip().lower()
        matches = self._UNIVERSE
        if q:
            matches = tuple(
                item
                for item in self._UNIVERSE
                if q in item.symbol.lower() or q in item.name.lower()
            )
        return [
            SymbolHit(item.symbol, item.market, item.asset_class, item.name, self.PROVIDER_CODE)
            for item in matches[:limit]
        ]

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Invalid symbol '{symbol}'. Expected EXCHANGE:SYMBOL.",
            )
        if end < start:
            return ProviderResult.failure(self.PROVIDER_CODE, "end date must be on or after start date.")

        seed = next((item for item in self._UNIVERSE if item.symbol == parsed.value), None)
        if seed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Symbol '{parsed.value}' is not in the fixture universe.",
                warnings=("fail_closed",),
            )

        bars: list[OhlcvBar] = []
        day = start
        offset = 0
        while day <= end:
            if day.weekday() < 5:
                close = seed.base_close + offset * 0.15
                bars.append(
                    OhlcvBar(
                        timestamp_utc=datetime(day.year, day.month, day.day, tzinfo=timezone.utc),
                        open=close - 0.05,
                        high=close + 0.20,
                        low=close - 0.25,
                        close=close,
                        volume=1_000_000 + offset * 1000,
                        currency=seed.currency,
                        adjustment=adjustment,
                        provider=self.PROVIDER_CODE,
                        amount=close * (1_000_000 + offset * 1000),
                        provenance={
                            "source": self.PROVIDER_CODE,
                            "symbol": seed.symbol,
                            "synthetic": "true",
                            "interval": interval.value,
                        },
                    )
                )
                offset += 1
            day += timedelta(days=1)

        if interval is BarInterval.WEEKLY:
            bars = _resample_ohlc(bars, kind="week")
        elif interval is BarInterval.MONTHLY:
            bars = _resample_ohlc(bars, kind="month")

        warnings = ["fixture_synthetic_data", "not_for_investment_decisions", f"interval:{interval.value}"]
        if adjustment is Adjustment.UNKNOWN:
            warnings.append("adjustment_unknown")
        return ProviderResult.success(self.PROVIDER_CODE, bars, warnings=tuple(warnings))


def _resample_ohlc(bars: list[OhlcvBar], *, kind: str) -> list[OhlcvBar]:
    if not bars:
        return bars
    groups: dict[tuple[int, int], list[OhlcvBar]] = {}
    for bar in bars:
        d = bar.timestamp_utc.date()
        key = (d.isocalendar()[0], d.isocalendar()[1]) if kind == "week" else (d.year, d.month)
        groups.setdefault(key, []).append(bar)
    out: list[OhlcvBar] = []
    for key in sorted(groups):
        chunk = groups[key]
        first, last = chunk[0], chunk[-1]
        out.append(
            OhlcvBar(
                timestamp_utc=last.timestamp_utc,
                open=first.open,
                high=max(b.high for b in chunk),
                low=min(b.low for b in chunk),
                close=last.close,
                volume=sum(b.volume for b in chunk),
                currency=last.currency,
                adjustment=last.adjustment,
                provider=last.provider,
                amount=sum((b.amount or 0) for b in chunk),
                provenance=dict(last.provenance),
            )
        )
    return out
