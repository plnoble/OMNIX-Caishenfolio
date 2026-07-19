from __future__ import annotations

from datetime import date
from typing import Any

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import Adjustment, OhlcvBar, ProviderResult
from caishenfolio_core.market.fixture import SymbolHit


class CompositeMarketDataProvider:
    """Try real providers in order. Never falls back to synthetic fixture."""

    PROVIDER_CODE = "auto"

    def __init__(self, providers: list[Any]) -> None:
        self._providers = list(providers)

    @property
    def ready(self) -> bool:
        return any(getattr(p, "ready", True) for p in self._providers)

    @property
    def children(self) -> list[Any]:
        return list(self._providers)

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        """Fast path: prefer providers that answer quickly; skip slow failures."""
        limit = max(1, min(limit, 50))
        merged: list[SymbolHit] = []
        seen: set[str] = set()
        # Prefer akshare/yfinance first for search (same order as list)
        for provider in self._providers:
            code = getattr(provider, "PROVIDER_CODE", "")
            if hasattr(provider, "ready") and not provider.ready:
                continue
            # Skip key-based providers for search unless query looks exact
            if code in {"tushare", "alphavantage"} and ":" not in (query or ""):
                continue
            try:
                hits = provider.search(query, limit=limit)
            except Exception:  # noqa: BLE001
                continue
            for hit in hits:
                if hit.symbol in seen:
                    continue
                seen.add(hit.symbol)
                merged.append(hit)
                if len(merged) >= limit:
                    return merged
            # Enough hits from first successful free source — stop (avoid 60s timeouts)
            if merged and code in {"akshare", "yfinance", "fixture"}:
                return merged
        return merged

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        errors: list[str] = []
        for provider in self._providers:
            code = getattr(provider, "PROVIDER_CODE", type(provider).__name__)
            if hasattr(provider, "ready") and not provider.ready:
                errors.append(f"{code}: not_ready")
                continue
            try:
                result = provider.historical_bars(symbol, start, end, adjustment, interval)
            except TypeError:
                # older provider signature without interval
                try:
                    result = provider.historical_bars(symbol, start, end, adjustment)
                except Exception as exc:  # noqa: BLE001
                    errors.append(f"{code}: {exc}")
                    continue
            except Exception as exc:  # noqa: BLE001
                errors.append(f"{code}: {exc}")
                continue
            if result.ok and result.data:
                warnings = list(result.warnings) + [f"resolved_by:{code}", f"interval:{interval.value}"]
                return ProviderResult.success(
                    code,
                    list(result.data),
                    warnings=tuple(warnings),
                )
            errors.append(f"{code}: {result.error or 'empty'}")

        detail = " | ".join(errors) if errors else "无可用数据源"
        return ProviderResult.failure(
            self.PROVIDER_CODE,
            f"全部真实行情源均失败（fail-closed，未生成数据）：{detail}",
            warnings=("all_providers_failed", "fail_closed"),
        )
