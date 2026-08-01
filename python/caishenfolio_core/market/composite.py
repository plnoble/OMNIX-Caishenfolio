from __future__ import annotations

import time
from datetime import date
from typing import Any, Callable

from caishenfolio_core.market.errors import classify

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import (
    Adjustment,
    FinancialPeriod,
    FxQuote,
    NavPoint,
    OhlcvBar,
    ProviderResult,
    Quote,
    ValuationPoint,
)
from caishenfolio_core.market.fixture import SymbolHit


#: Percent spread between sources beyond which a quote is flagged as disputed.
DEFAULT_PRICE_TOLERANCE_PCT = 2.0


def _median(values: list[float]) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    mid = len(ordered) // 2
    if len(ordered) % 2 == 1:
        return ordered[mid]
    return (ordered[mid - 1] + ordered[mid]) / 2.0


def _majority_currency(quotes: list[tuple[str, Quote]]) -> str:
    counts: dict[str, int] = {}
    for _, quote in quotes:
        counts[quote.currency] = counts.get(quote.currency, 0) + 1
    # Ties fall back to the first provider's currency, which follows the configured priority.
    best = max(counts.values())
    for _, quote in quotes:
        if counts[quote.currency] == best:
            return quote.currency
    return quotes[0][1].currency


class CompositeMarketDataProvider:
    """Try real providers in order. Never falls back to synthetic fixture."""

    PROVIDER_CODE = "auto"

    def __init__(
        self,
        providers: list[Any],
        rate_limit_retries: int = 2,
        base_backoff_seconds: float = 0.5,
        max_backoff_seconds: float = 5.0,
        sleep: Callable[[float], None] | None = None,
    ) -> None:
        self._providers = list(providers)
        self._rate_limit_retries = max(0, rate_limit_retries)
        self._base_backoff_seconds = base_backoff_seconds
        self._max_backoff_seconds = max_backoff_seconds
        # Injectable so tests exercise the backoff logic without actually waiting.
        self._sleep = sleep or time.sleep

        # A child that derives one series from another (reconstructed US/HK valuation needs
        # prices) should reach for the whole chain, not just its own feed: the source holding
        # the fundamentals is often not the one that can reach the prices.
        for provider in self._providers:
            if hasattr(provider, "bar_source"):
                provider.bar_source = self

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

    def latest_quote(
        self,
        symbol: str,
        cross_check: bool = False,
        tolerance_pct: float = DEFAULT_PRICE_TOLERANCE_PCT,
    ) -> ProviderResult[Quote]:
        if not cross_check:
            return self._first_success(
                "latest_quote",
                lambda provider: provider.latest_quote(symbol),
                f"未取得 {symbol} 的最新价格",
            )
        return self._cross_checked_quote(symbol, tolerance_pct)

    def _cross_checked_quote(self, symbol: str, tolerance_pct: float) -> ProviderResult[Quote]:
        """Asks every capable provider and reports when they disagree.

        Taking the first answer is fine for a chart, but a wrong price silently mis-values a
        whole portfolio. This keeps the median (robust to one bad source) and attaches every
        source's price so a disagreement is visible instead of averaged away.
        """
        quotes: list[tuple[str, Quote]] = []
        errors: list[str] = []

        for provider in self._providers:
            code = getattr(provider, "PROVIDER_CODE", type(provider).__name__)
            if not callable(getattr(provider, "latest_quote", None)):
                continue
            if hasattr(provider, "ready") and not provider.ready:
                continue
            try:
                result = provider.latest_quote(symbol)
            except Exception as exc:  # noqa: BLE001
                errors.append(f"{code}: {exc}")
                continue
            if result.ok and result.data is not None and result.data.price > 0:
                quotes.append((code, result.data))
            else:
                errors.append(f"{code}: {result.error or 'empty'}")

        if not quotes:
            detail = " | ".join(errors) if errors else "无可用数据源"
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得 {symbol} 的最新价格（fail-closed，未生成数据）：{detail}",
                warnings=("all_providers_failed", "fail_closed"),
            )

        # Only prices in the same currency are comparable; a source quoting another currency is
        # itself a disagreement worth reporting rather than silently mixing into the median.
        base_currency = _majority_currency(quotes)
        comparable = [(code, q) for code, q in quotes if q.currency == base_currency]
        mismatched = [code for code, q in quotes if q.currency != base_currency]

        prices = sorted(q.price for _, q in comparable)
        median = _median(prices)
        spread_pct = 0.0 if median <= 0 else (prices[-1] - prices[0]) / median * 100.0

        chosen = min(comparable, key=lambda item: abs(item[1].price - median))

        # Per-source deviation, not just the overall spread: knowing *which* source is off is
        # what lets you decide whether to distrust it.
        deviations = {
            code: 0.0 if median <= 0 else (q.price - median) / median * 100.0
            for code, q in comparable
        }
        outliers = sorted(
            (code for code, dev in deviations.items() if abs(dev) > tolerance_pct),
            key=lambda code: abs(deviations[code]),
            reverse=True,
        )
        sources = ";".join(
            f"{code}={q.price:g}" + (f"({deviations[code]:+.1f}%)" if code in deviations else "")
            for code, q in quotes
        )

        warnings = ["cross_checked", f"sources:{len(comparable)}"]
        if len(comparable) < 2:
            warnings.append("single_source")
        if mismatched:
            warnings.append("currency_disagreement:" + ",".join(mismatched))
        if spread_pct > tolerance_pct:
            warnings.append(f"price_disagreement:{spread_pct:.2f}")
        if outliers:
            warnings.append("outliers:" + ",".join(outliers))

        quote = Quote(
            symbol=chosen[1].symbol,
            price=median,
            currency=base_currency,
            as_of=chosen[1].as_of,
            provider=self.PROVIDER_CODE,
            provenance={
                **dict(chosen[1].provenance),
                "cross_check_sources": sources,
                "cross_check_spread_pct": f"{spread_pct:.4f}",
                "cross_check_count": str(len(comparable)),
                "cross_check_outliers": ",".join(outliers),
            },
        )
        return ProviderResult.success(self.PROVIDER_CODE, quote, warnings=tuple(warnings))

    def nav_series(self, symbol: str, start: date, end: date) -> ProviderResult[list[NavPoint]]:
        return self._first_success(
            "nav_series",
            lambda provider: provider.nav_series(symbol, start, end),
            f"未取得 {symbol} 的净值序列",
        )

    def fx_rate(self, base_currency: str, quote_currency: str) -> ProviderResult[FxQuote]:
        return self._first_success(
            "fx_rate",
            lambda provider: provider.fx_rate(base_currency, quote_currency),
            f"未取得 {base_currency}/{quote_currency} 的汇率",
        )

    def valuation_history(self, symbol: str, years: int = 10) -> ProviderResult[list[ValuationPoint]]:
        return self._first_success(
            "valuation_history",
            lambda provider: provider.valuation_history(symbol, years),
            f"未取得 {symbol} 的估值历史",
        )

    def financial_summary(self, symbol: str, periods: int = 5) -> ProviderResult[list[FinancialPeriod]]:
        return self._first_success(
            "financial_summary",
            lambda provider: provider.financial_summary(symbol, periods),
            f"未取得 {symbol} 的财务摘要",
        )

    def _first_success(
        self,
        capability: str,
        call: Callable[[Any], ProviderResult[Any]],
        failure_summary: str,
    ) -> ProviderResult[Any]:
        """Tries each provider that implements the capability; reports every failure, invents nothing.

        A rate-limited source is retried with a short backoff before moving on: it would have
        answered, it just needed a moment. Anything else fails straight through to the next
        source, because retrying a broken or unreachable one only wastes time.
        """
        errors: list[str] = []
        for provider in self._providers:
            code = getattr(provider, "PROVIDER_CODE", type(provider).__name__)
            if not callable(getattr(provider, capability, None)):
                continue
            if hasattr(provider, "ready") and not provider.ready:
                errors.append(f"{code}: not_ready")
                continue

            result, failure = self._call_with_backoff(provider, call, code)
            if result is not None:
                return ProviderResult.success(
                    code,
                    result.data,
                    warnings=tuple(result.warnings) + (f"resolved_by:{code}",),
                )
            errors.append(failure)

        detail = " | ".join(errors) if errors else f"无数据源实现 {capability}"
        return ProviderResult.failure(
            self.PROVIDER_CODE,
            f"{failure_summary}（fail-closed，未生成数据）：{detail}",
            warnings=("all_providers_failed", "fail_closed"),
        )

    def _call_with_backoff(
        self,
        provider: Any,
        call: Callable[[Any], ProviderResult[Any]],
        code: str,
    ) -> tuple[ProviderResult[Any] | None, str]:
        """Calls one provider, waiting out a rate limit before conceding."""
        last = ""
        for attempt in range(self._rate_limit_retries + 1):
            try:
                result = call(provider)
            except Exception as exc:  # noqa: BLE001
                error = classify(exc)
                last = f"{code}: [{error.code}] {error}"
                if not error.retryable or attempt == self._rate_limit_retries:
                    return None, last
                self._sleep(self._backoff_seconds(error, attempt))
                continue

            if result.ok and result.data:
                return result, ""

            last = f"{code}: {result.error or 'empty'}"
            # A provider can report a rate limit through the result rather than an exception.
            if "rate_limited" not in (result.warnings or ()) or attempt == self._rate_limit_retries:
                return None, last
            self._sleep(self._backoff_seconds(None, attempt))

        return None, last

    def _backoff_seconds(self, error: Any, attempt: int) -> float:
        hinted = getattr(error, "retry_after_seconds", None)
        if hinted:
            return min(float(hinted), self._max_backoff_seconds)
        return min(self._base_backoff_seconds * (2 ** attempt), self._max_backoff_seconds)
