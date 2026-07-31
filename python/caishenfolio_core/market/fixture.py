from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone

import math

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import (
    Adjustment,
    AssetClass,
    FinancialPeriod,
    FxQuote,
    MarketRegion,
    NavPoint,
    OhlcvBar,
    ProviderResult,
    Quote,
    SymbolId,
    ValuationPoint,
)

# Demo rates only — every value here is synthetic and must never inform a real decision.
_FIXTURE_FX: dict[tuple[str, str], float] = {
    ("USD", "CNY"): 7.2,
    ("USD", "HKD"): 7.8,
    ("USD", "JPY"): 150.0,
    ("HKD", "CNY"): 0.923,
}


@dataclass(frozen=True)
class SymbolSeed:
    symbol: str
    market: MarketRegion
    asset_class: AssetClass
    name: str
    currency: str
    base_close: float


@dataclass(frozen=True)
class SymbolHit:
    symbol: str
    market: MarketRegion
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
        SymbolSeed("SSE:600000", MarketRegion.CN, AssetClass.EQUITY, "浦发银行", "CNY", 10.0),
        SymbolSeed("SZSE:000001", MarketRegion.CN, AssetClass.EQUITY, "平安银行", "CNY", 12.5),
        SymbolSeed("SSE:510300", MarketRegion.CN, AssetClass.ETF, "沪深300ETF", "CNY", 4.2),
        SymbolSeed("SSE:113050", MarketRegion.CN, AssetClass.CONVERTIBLE_BOND, "南银转债", "CNY", 118.0),
        SymbolSeed("FUND:110022", MarketRegion.CN, AssetClass.MUTUAL_FUND, "易方达消费行业", "CNY", 3.5),
        SymbolSeed("HKEX:00700", MarketRegion.HK, AssetClass.EQUITY, "Tencent", "HKD", 320.0),
        SymbolSeed("NASDAQ:AAPL", MarketRegion.US, AssetClass.EQUITY, "Apple", "USD", 180.0),
        SymbolSeed("NYSE:SPY", MarketRegion.US, AssetClass.ETF, "SPDR S&P 500", "USD", 450.0),
        SymbolSeed("TSE:7203", MarketRegion.JP, AssetClass.EQUITY, "トヨタ自動車", "JPY", 2800.0),
        SymbolSeed("FX:USDCNY", MarketRegion.GLOBAL, AssetClass.FX, "美元/人民币", "CNY", 7.2),
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

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        seed = self._seed_for(symbol)
        if seed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"标的 '{symbol}' 不在演示池内。",
                warnings=("fail_closed",),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            Quote(
                symbol=seed.symbol,
                price=seed.base_close,
                currency=seed.currency,
                as_of=date.today(),
                provider=self.PROVIDER_CODE,
                provenance={"synthetic": "true"},
            ),
            warnings=("fixture_synthetic_data", "not_for_investment_decisions"),
        )

    def nav_series(self, symbol: str, start: date, end: date) -> ProviderResult[list[NavPoint]]:
        seed = self._seed_for(symbol)
        if seed is None or seed.asset_class is not AssetClass.MUTUAL_FUND:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"'{symbol}' 不是演示池内的场外基金。",
                warnings=("fail_closed",),
            )
        if end < start:
            return ProviderResult.failure(self.PROVIDER_CODE, "结束日期必须不早于开始日期。")

        points: list[NavPoint] = []
        day = start
        offset = 0
        while day <= end:
            if day.weekday() < 5:
                nav = round(seed.base_close + offset * 0.002, 4)
                points.append(
                    NavPoint(
                        as_of=day,
                        nav=nav,
                        accumulated_nav=round(nav + 0.5, 4),
                        currency=seed.currency,
                        provider=self.PROVIDER_CODE,
                        provenance={"synthetic": "true", "symbol": seed.symbol},
                    )
                )
                offset += 1
            day += timedelta(days=1)

        return ProviderResult.success(
            self.PROVIDER_CODE,
            points,
            warnings=("fixture_synthetic_data", "not_for_investment_decisions"),
        )

    def valuation_history(self, symbol: str, years: int = 10) -> ProviderResult[list[ValuationPoint]]:
        seed = self._seed_for(symbol)
        if seed is None or seed.asset_class not in {AssetClass.EQUITY, AssetClass.ETF}:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"'{symbol}' 不在演示池内或没有估值指标。",
                warnings=("fail_closed",),
            )

        # A slow sine so percentiles have a real distribution to rank against.
        today = date.today()
        points: list[ValuationPoint] = []
        span = max(1, min(years, 10)) * 250
        for i in range(span):
            day = today - timedelta(days=span - i)
            if day.weekday() >= 5:
                continue
            phase = math.sin(i / 180.0)
            points.append(
                ValuationPoint(
                    as_of=day,
                    pe=round(20.0 + 8.0 * phase, 4),
                    pb=round(2.5 + 0.8 * phase, 4),
                    dividend_yield=round(0.025 - 0.008 * phase, 6),
                )
            )

        return ProviderResult.success(
            self.PROVIDER_CODE,
            points,
            warnings=("fixture_synthetic_data", "not_for_investment_decisions"),
        )

    def financial_summary(self, symbol: str, periods: int = 5) -> ProviderResult[list[FinancialPeriod]]:
        seed = self._seed_for(symbol)
        if seed is None or seed.asset_class is not AssetClass.EQUITY:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"'{symbol}' 不在演示池内或不是股票。",
                warnings=("fail_closed",),
            )

        year = date.today().year
        result: list[FinancialPeriod] = []
        for offset in range(periods):
            revenue = 1_000_000_000.0 * (1.12 ** (periods - offset - 1))
            profit = revenue * 0.18
            result.append(
                FinancialPeriod(
                    period=f"{year - offset - 1}1231",
                    revenue=round(revenue, 2),
                    net_profit=round(profit, 2),
                    eps=round(profit / 1_000_000_000.0, 4),
                    roe=0.15,
                    revenue_growth=None if offset == periods - 1 else 0.12,
                    profit_growth=None if offset == periods - 1 else 0.12,
                )
            )

        return ProviderResult.success(
            self.PROVIDER_CODE,
            result,
            warnings=("fixture_synthetic_data", "not_for_investment_decisions"),
        )

    def fx_rate(self, base_currency: str, quote_currency: str) -> ProviderResult[FxQuote]:
        base = (base_currency or "").strip().upper()
        quote = (quote_currency or "").strip().upper()
        rate = _FIXTURE_FX.get((base, quote))
        if rate is None and (quote, base) in _FIXTURE_FX:
            rate = 1.0 / _FIXTURE_FX[(quote, base)]
        if rate is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"演示池内没有 {base}/{quote} 的汇率。",
                warnings=("fail_closed",),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            FxQuote(
                base_currency=base,
                quote_currency=quote,
                rate=rate,
                as_of=date.today(),
                provider=self.PROVIDER_CODE,
                provenance={"synthetic": "true"},
            ),
            warnings=("fixture_synthetic_data", "not_for_investment_decisions"),
        )

    def _seed_for(self, symbol: str) -> SymbolSeed | None:
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return None
        wanted = parsed.normalized().value
        return next((item for item in self._UNIVERSE if item.symbol == wanted), None)

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
