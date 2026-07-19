from __future__ import annotations

from datetime import date, datetime, timedelta, timezone
from typing import Any

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import Adjustment, OhlcvBar, ProviderResult
from caishenfolio_core.market.bar_cache import BarsSqliteCache
from caishenfolio_core.market.fixture import SymbolHit


def _aggregate(bars: list[OhlcvBar], mode: str) -> list[OhlcvBar]:
    if not bars:
        return bars
    groups: dict[tuple, list[OhlcvBar]] = {}
    for bar in bars:
        d = bar.timestamp_utc.date()
        if mode == "quarter":
            key = (d.year, (d.month - 1) // 3 + 1)
        elif mode == "year":
            key = (d.year,)
        else:
            key = (d.year, d.month)
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
                amount=sum((b.amount or 0.0) for b in chunk) or None,
                provenance={
                    **dict(last.provenance),
                    "aggregated": mode,
                    "synthetic": last.provenance.get("synthetic", "false"),
                },
            )
        )
    return out


class CachingMarketFacade:
    """Provider facade: disk cache + incremental fetch. Never invents bars."""

    PROVIDER_CODE = "cache+upstream"

    def __init__(self, upstream: Any, cache: BarsSqliteCache | None = None) -> None:
        self.upstream = upstream
        self.cache = cache or BarsSqliteCache()

    @property
    def ready(self) -> bool:
        return bool(getattr(self.upstream, "ready", True))

    @property
    def children(self) -> list[Any]:
        return getattr(self.upstream, "children", [self.upstream])

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        return self.upstream.search(query, limit=limit)

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        # Quarterly/yearly: use daily cache/upstream then aggregate
        if interval.is_aggregate_from_daily:
            daily = self.historical_bars(symbol, start, end, adjustment, BarInterval.DAILY)
            if not daily.ok or not daily.data:
                return daily
            mode = "quarter" if interval is BarInterval.QUARTERLY else "year"
            agg = _aggregate(list(daily.data), mode)
            # filter window by end of period date
            agg = [b for b in agg if start <= b.timestamp_utc.date() <= end]
            if not agg:
                return ProviderResult.failure(
                    daily.provider,
                    f"聚合{interval.label_zh}后窗口内无数据。",
                    warnings=("empty_window", "fail_closed"),
                )
            warnings = list(daily.warnings) + [f"aggregated:{mode}", "from_cache_or_upstream"]
            return ProviderResult.success(daily.provider, agg, warnings=tuple(warnings))

        # Intraday: prefer direct upstream (short retention in cache)
        if interval.is_intraday:
            result = self._fetch_upstream(symbol, start, end, adjustment, interval)
            if result.ok and result.data:
                self.cache.upsert_bars(symbol, interval, adjustment, result.data)
            return result

        # Daily/weekly/monthly: cache-first incremental
        cached = self.cache.get_bars(symbol, interval, adjustment, start, end)
        max_d = self.cache.max_date(symbol, interval, adjustment)

        need_fetch = False
        fetch_start = start
        if max_d is None:
            need_fetch = True
            fetch_start = start
        else:
            # refresh from day after max (or re-fetch last day for partial updates)
            if max_d < end:
                need_fetch = True
                fetch_start = max_d  # include last day for corrections
            # also fill leading gap
            if cached and cached[0].timestamp_utc.date() > start:
                need_fetch = True
                fetch_start = start
            if not cached:
                need_fetch = True
                fetch_start = start

        warnings: list[str] = []
        provider = getattr(self.upstream, "PROVIDER_CODE", "upstream")

        if need_fetch:
            result = self._fetch_upstream(symbol, fetch_start, end, adjustment, interval)
            if result.ok and result.data:
                self.cache.upsert_bars(symbol, interval, adjustment, result.data)
                provider = result.provider
                warnings.extend(result.warnings)
                warnings.append("cache_updated")
            elif not cached:
                return result
            else:
                warnings.append("upstream_failed_served_cache")
                if result.error:
                    warnings.append(f"upstream_error:{result.error[:120]}")

        merged = self.cache.get_bars(symbol, interval, adjustment, start, end)
        if not merged:
            return ProviderResult.failure(
                provider,
                "本地缓存与上游均无可用K线（fail-closed）。",
                warnings=("empty_cache", "fail_closed"),
            )
        warnings.append("served_with_disk_cache")
        return ProviderResult.success(provider, merged, warnings=tuple(dict.fromkeys(warnings)))

    def _fetch_upstream(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment,
        interval: BarInterval,
    ) -> ProviderResult[list[OhlcvBar]]:
        try:
            return self.upstream.historical_bars(symbol, start, end, adjustment, interval)
        except TypeError:
            return self.upstream.historical_bars(symbol, start, end, adjustment)

    def sync_symbol(
        self,
        symbol: str,
        *,
        years: int = 10,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> dict[str, object]:
        end = date.today()
        start = date(end.year - years, end.month, end.day) if end.month != 2 or end.day != 29 else date(end.year - years, 2, 28)
        max_d = self.cache.max_date(symbol, interval, adjustment)
        if max_d is not None:
            start = max_d
        result = self.historical_bars(symbol, start, end, adjustment, interval)
        return {
            "symbol": symbol,
            "ok": result.ok,
            "provider": result.provider,
            "bars": 0 if not result.data else len(result.data),
            "error": result.error,
            "from": start.isoformat(),
            "to": end.isoformat(),
            "interval": interval.value,
        }
