"""Open-end fund NAV from Eastmoney's public series file, standard library only.

Why this exists: off-exchange fund NAV rested entirely on one akshare endpoint, so a single
upstream change would leave every FUND: holding unpriced. This reads the same public series
Eastmoney's own fund pages draw from, needs no API key and no third-party package, and so keeps
answering both before the Python environment is provisioned and when akshare's endpoint moves.

Scope is deliberately narrow — NAV history for off-exchange funds. Everything else stays with
the richer providers.
"""

from __future__ import annotations

import json
import re
import urllib.request
from datetime import date, datetime, timedelta, timezone

from caishenfolio_core.data.markets import CN_FUND_EXCHANGE
from caishenfolio_core.data.models import NavPoint, ProviderResult, SymbolId
from caishenfolio_core.market.errors import classify, warning_tags

_ENDPOINT = "https://fund.eastmoney.com/pingzhongdata/{code}.js"
_TIMEOUT_SECONDS = 10

#: Eastmoney stamps each point at Beijing midnight, so the date must be read in UTC+8.
_CST = timezone(timedelta(hours=8))

_UNIT_NAV_RE = re.compile(r"Data_netWorthTrend\s*=\s*(\[.*?\])\s*;", re.DOTALL)
_ACCUM_NAV_RE = re.compile(r"Data_ACWorthTrend\s*=\s*(\[.*?\])\s*;", re.DOTALL)


class EastmoneyFundNavProvider:
    """Daily NAV for CN open-end funds. Never synthesizes; unparseable payloads fail closed."""

    PROVIDER_CODE = "eastmoney_fund"

    def __init__(self, timeout_seconds: int = _TIMEOUT_SECONDS) -> None:
        self._timeout = timeout_seconds

    @property
    def ready(self) -> bool:
        # Standard library only, so there is no dependency that can be missing.
        return True

    def search(self, query: str = "", limit: int = 10) -> list:
        # Discovery belongs to the providers that maintain a fund index.
        return []

    def historical_bars(self, *args, **kwargs):  # noqa: ANN002, ANN003
        return ProviderResult.failure(
            self.PROVIDER_CODE,
            "该数据源只提供场外基金净值，K线请用其他数据源。",
            warnings=("unsupported_capability", "fail_closed"),
        )

    def nav_series(self, symbol: str, start: date, end: date) -> ProviderResult[list[NavPoint]]:
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效标的 '{symbol}'。", warnings=("fail_closed",)
            )

        parsed = parsed.normalized()
        if parsed.exchange != CN_FUND_EXCHANGE:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"'{parsed.value}' 不是场外基金（应为 {CN_FUND_EXCHANGE}:代码）。",
                warnings=("unsupported_symbol", "fail_closed"),
            )
        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE, "结束日期必须不早于开始日期。", warnings=("fail_closed",)
            )

        code = "".join(ch for ch in parsed.code if ch.isdigit()).zfill(6)
        try:
            request = urllib.request.Request(
                _ENDPOINT.format(code=code),
                headers={
                    "User-Agent": "OMNIX-Caishenfolio",
                    "Referer": f"https://fund.eastmoney.com/{code}.html",
                },
            )
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                raw = response.read()
        except Exception as exc:  # noqa: BLE001
            error = classify(exc)
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"天天基金净值获取失败：{parsed.value}（{error}）",
                warnings=warning_tags(error),
            )

        return parse_nav_series(
            raw.decode("utf-8", errors="replace"), parsed.value, start, end, self.PROVIDER_CODE
        )


def parse_nav_series(
    payload: str,
    symbol: str,
    start: date,
    end: date,
    provider: str = EastmoneyFundNavProvider.PROVIDER_CODE,
) -> ProviderResult[list[NavPoint]]:
    """Parses the series file. Kept pure so the format is testable without network."""
    match = _UNIT_NAV_RE.search(payload)
    if match is None:
        return ProviderResult.failure(
            provider,
            f"天天基金返回无法解析（未找到净值序列）：{symbol}",
            warnings=("parse_error", "fail_closed"),
        )

    try:
        rows = json.loads(match.group(1))
    except ValueError:
        return ProviderResult.failure(
            provider, f"天天基金净值序列格式异常：{symbol}", warnings=("parse_error", "fail_closed")
        )

    accumulated = _accumulated_by_day(payload)

    points: list[NavPoint] = []
    for row in rows:
        if not isinstance(row, dict):
            continue
        day = _to_day(row.get("x"))
        nav = _to_float(row.get("y"))
        # A fund that had not started, or a day the manager did not publish, has no NAV.
        # Reporting it as zero would show a 100% loss, so such days are dropped instead.
        if day is None or nav is None or nav <= 0:
            continue
        if day < start or day > end:
            continue

        points.append(
            NavPoint(
                as_of=day,
                nav=nav,
                currency="CNY",
                provider=provider,
                accumulated_nav=accumulated.get(day),
                daily_return=_percent_to_ratio(row.get("equityReturn")),
                provenance={
                    "source": provider,
                    "symbol": symbol,
                    "source_api": "pingzhongdata",
                    "synthetic": "false",
                },
            )
        )

    if not points:
        return ProviderResult.failure(
            provider,
            f"天天基金在区间内无净值数据：{symbol}",
            warnings=("empty_window", "fail_closed"),
        )

    points.sort(key=lambda p: p.as_of)
    return ProviderResult.success(
        provider,
        points,
        warnings=("real_market_data", "not_for_investment_decisions", "fund_nav_not_ohlcv"),
    )


def _accumulated_by_day(payload: str) -> dict[date, float]:
    """Cumulative NAV lives in a separate ``[[ms, value], ...]`` array; absent is fine."""
    match = _ACCUM_NAV_RE.search(payload)
    if match is None:
        return {}
    try:
        rows = json.loads(match.group(1))
    except ValueError:
        return {}

    out: dict[date, float] = {}
    for row in rows:
        if not isinstance(row, (list, tuple)) or len(row) < 2:
            continue
        day = _to_day(row[0])
        value = _to_float(row[1])
        if day is not None and value is not None and value > 0:
            out[day] = value
    return out


def _to_day(value: object) -> date | None:
    try:
        return datetime.fromtimestamp(float(value) / 1000.0, tz=_CST).date()  # type: ignore[arg-type]
    except (TypeError, ValueError, OSError, OverflowError):
        return None


def _to_float(value: object) -> float | None:
    try:
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return None


def _percent_to_ratio(value: object) -> float | None:
    """``equityReturn`` is a percentage; the model stores a ratio."""
    number = _to_float(value)
    return None if number is None else number / 100.0
