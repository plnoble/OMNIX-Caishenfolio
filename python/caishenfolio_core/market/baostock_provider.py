"""A-share history from BaoStock.

Why this source: CN daily bars previously rested on akshare alone — the Tencent feed only
serves the latest price — so a single upstream change left the market with no history at all.
BaoStock is free, needs no API key, and carries complete A-share daily data back to 1990,
which also gives the cross-check a genuine second opinion on historical series.

The library keeps a process-wide session, so login is done once and lazily rather than per call.
"""

from __future__ import annotations

import threading
from datetime import date, datetime, timezone
from typing import Any

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.markets import cn_exchange_for_code
from caishenfolio_core.data.models import (
    Adjustment,
    OhlcvBar,
    ProviderResult,
    SymbolId,
)
from caishenfolio_core.market.errors import classify, warning_tags

_FIELDS = "date,code,open,high,low,close,volume,amount"

_EXCHANGE_PREFIX = {"SSE": "sh", "SZSE": "sz"}

_FREQUENCY = {
    BarInterval.DAILY: "d",
    BarInterval.WEEKLY: "w",
    BarInterval.MONTHLY: "m",
}

#: BaoStock's adjustflag: 1 backward, 2 forward, 3 none.
_ADJUST_FLAG = {
    Adjustment.RAW: "3",
    Adjustment.FORWARD: "2",
    Adjustment.BACKWARD: "1",
    Adjustment.UNKNOWN: "3",
}


def _try_import_baostock() -> Any | None:
    try:
        import baostock as bs  # type: ignore

        return bs
    except Exception:  # noqa: BLE001
        return None


class BaostockMarketDataProvider:
    """Daily/weekly/monthly A-share bars. Never synthesizes; unreadable payloads fail closed."""

    PROVIDER_CODE = "baostock"

    def __init__(self) -> None:
        self._bs = _try_import_baostock()
        self._logged_in = False
        self._lock = threading.Lock()

    @property
    def ready(self) -> bool:
        return self._bs is not None

    def search(self, query: str = "", limit: int = 10) -> list:
        # Discovery stays with the providers that maintain a symbol index.
        return []

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        if self._bs is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "baostock 未安装。请执行: pip install baostock",
                warnings=("provider_unavailable", "fail_closed"),
            )

        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效标的 '{symbol}'。", warnings=("fail_closed",))

        parsed = parsed.normalized()
        ticker = to_baostock_code(parsed.exchange, parsed.code)
        if ticker is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"baostock 不支持交易所 '{parsed.exchange}'（仅沪深 A 股）。",
                warnings=("unsupported_exchange", "fail_closed"),
            )

        frequency = _FREQUENCY.get(interval)
        if frequency is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"baostock 不支持周期 {interval.value}（仅日/周/月）。",
                warnings=("unsupported_interval", "fail_closed"),
            )

        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE, "结束日期必须不早于开始日期。", warnings=("fail_closed",))

        try:
            self._ensure_login()
            rows = self._query(ticker, start, end, frequency, _ADJUST_FLAG[adjustment])
        except Exception as exc:  # noqa: BLE001
            error = classify(exc)
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"baostock 取数失败：{parsed.value}（{error}）",
                warnings=warning_tags(error),
            )

        bars = _rows_to_bars(rows, parsed.value, adjustment, self.PROVIDER_CODE, ticker)
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"baostock 在区间内无数据：{parsed.value}",
                warnings=("empty_window", "fail_closed"),
            )

        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=(
                "real_market_data",
                "not_for_investment_decisions",
                f"interval:{interval.value}",
            ),
        )

    def _ensure_login(self) -> None:
        """Logs in once. BaoStock holds a process-wide session rather than per-call auth."""
        with self._lock:
            if self._logged_in or self._bs is None:
                return
            result = self._bs.login()
            if getattr(result, "error_code", "0") != "0":
                raise RuntimeError(f"baostock 登录失败：{getattr(result, 'error_msg', '未知')}")
            self._logged_in = True

    def _query(
        self, ticker: str, start: date, end: date, frequency: str, adjust: str
    ) -> list[list[str]]:
        assert self._bs is not None
        result = self._bs.query_history_k_data_plus(
            ticker,
            _FIELDS,
            start_date=start.isoformat(),
            end_date=end.isoformat(),
            frequency=frequency,
            adjustflag=adjust,
        )
        if getattr(result, "error_code", "0") != "0":
            raise RuntimeError(getattr(result, "error_msg", "baostock 查询失败"))

        rows: list[list[str]] = []
        while result.next():
            rows.append(result.get_row_data())
        return rows


def to_baostock_code(exchange: str, code: str) -> str | None:
    """Maps ``SSE``/``600000`` to ``sh.600000``; None for venues this source does not serve."""
    digits = "".join(ch for ch in code if ch.isdigit())
    if not digits:
        return None

    venue = (exchange or "").strip().upper()
    if venue:
        # A named foreign venue must never fall through to a CN guess.
        prefix = _EXCHANGE_PREFIX.get(venue)
    else:
        prefix = _EXCHANGE_PREFIX.get(cn_exchange_for_code(digits))

    return None if prefix is None else f"{prefix}.{digits.zfill(6)}"


def _rows_to_bars(
    rows: list[list[str]],
    symbol: str,
    adjustment: Adjustment,
    provider: str,
    ticker: str,
) -> list[OhlcvBar]:
    """Parses rows, skipping any that cannot be read rather than substituting zeros."""
    bars: list[OhlcvBar] = []
    for row in rows:
        if len(row) < 7:
            continue
        try:
            day = date.fromisoformat(row[0])
            open_ = float(row[2])
            high = float(row[3])
            low = float(row[4])
            close = float(row[5])
        except (ValueError, IndexError):
            # Suspended days come back with empty price fields; they are not zero-priced days.
            continue

        if close <= 0:
            continue

        bars.append(
            OhlcvBar(
                timestamp_utc=datetime(day.year, day.month, day.day, tzinfo=timezone.utc),
                open=open_,
                high=high,
                low=low,
                close=close,
                volume=_optional(row, 6) or 0.0,
                currency="CNY",
                adjustment=adjustment,
                provider=provider,
                amount=_optional(row, 7),
                provenance={
                    "source": provider,
                    "symbol": symbol,
                    "source_api": "query_history_k_data_plus",
                    "upstream_ticker": ticker,
                    "synthetic": "false",
                },
            )
        )

    return bars


def _optional(row: list[str], index: int) -> float | None:
    try:
        return float(row[index])
    except (ValueError, IndexError):
        return None
