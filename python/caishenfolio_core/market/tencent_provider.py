"""Zero-dependency CN quotes from Tencent's public endpoint.

Why this exists alongside akshare: A-share prices previously rested on akshare alone, so the
cross-check had nothing to compare against, and a fresh install could not price anything until
the Python environment finished provisioning. This provider needs nothing but the standard
library, so it works immediately and gives the cross-check a second opinion.

Scope is deliberately narrow — latest quote only. Bars, search and fundamentals stay with the
richer providers.
"""

from __future__ import annotations

import re
import urllib.request
from datetime import date, datetime

from caishenfolio_core.data.markets import cn_exchange_for_code
from caishenfolio_core.data.models import ProviderResult, Quote, SymbolId

_ENDPOINT = "https://qt.gtimg.cn/q="
_TIMEOUT_SECONDS = 8

#: Position of each field inside the tilde-separated payload.
_FIELD_NAME = 1
_FIELD_PRICE = 3

_PAYLOAD_RE = re.compile(r'v_[a-z]{2}\d+="([^"]*)"')
_COMPACT_DATETIME_RE = re.compile(r"^\d{14}$")

_EXCHANGE_PREFIX = {
    "SSE": "sh",
    "SZSE": "sz",
    "BSE": "bj",
}


class TencentQuoteProvider:
    """Latest CN quote over plain HTTP. Never synthesizes; unparseable payloads fail closed."""

    PROVIDER_CODE = "tencent"

    def __init__(self, timeout_seconds: int = _TIMEOUT_SECONDS) -> None:
        self._timeout = timeout_seconds

    @property
    def ready(self) -> bool:
        # Standard library only, so there is no dependency that can be missing.
        return True

    def search(self, query: str = "", limit: int = 10) -> list:
        # Discovery belongs to the providers that have a symbol index.
        return []

    def historical_bars(self, *args, **kwargs):  # noqa: ANN002, ANN003
        return ProviderResult.failure(
            self.PROVIDER_CODE,
            "腾讯行情源只提供最新价，历史K线请用其他数据源。",
            warnings=("unsupported_capability", "fail_closed"),
        )

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效标的 '{symbol}'。", warnings=("fail_closed",)
            )

        parsed = parsed.normalized()
        ticker = to_tencent_ticker(parsed.exchange, parsed.code)
        if ticker is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"腾讯行情源不支持交易所 '{parsed.exchange}'（仅 A 股）。",
                warnings=("unsupported_exchange", "fail_closed"),
            )

        try:
            request = urllib.request.Request(
                _ENDPOINT + ticker,
                headers={"User-Agent": "OMNIX-Caishenfolio", "Referer": "https://finance.qq.com/"},
            )
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                raw = response.read()
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"腾讯行情获取失败：{exc}",
                warnings=("upstream_error", "fail_closed"),
            )

        # The endpoint answers in GBK; decoding as UTF-8 mangles the instrument name.
        text = raw.decode("gbk", errors="replace")
        return parse_quote(text, parsed.value, self.PROVIDER_CODE, ticker)


def to_tencent_ticker(exchange: str, code: str) -> str | None:
    """Maps ``SSE``/``600000`` to ``sh600000``; None for venues this source does not serve."""
    digits = "".join(ch for ch in code if ch.isdigit())
    if not digits:
        return None

    venue = (exchange or "").strip().upper()
    if venue:
        # Only a named CN venue is served. Falling back to the CN classifier for a foreign venue
        # would map HKEX:00700 to sz000700 and return a Shenzhen stock's price for Tencent.
        prefix = _EXCHANGE_PREFIX.get(venue)
    else:
        # A bare code carries no venue, so the shared classifier decides.
        prefix = _EXCHANGE_PREFIX.get(cn_exchange_for_code(digits))

    if prefix is None:
        return None

    return f"{prefix}{digits.zfill(6)}"


def parse_quote(
    payload: str,
    symbol: str,
    provider: str = TencentQuoteProvider.PROVIDER_CODE,
    ticker: str = "",
) -> ProviderResult[Quote]:
    """Parses the tilde-separated payload. Kept pure so the format is testable without network."""
    match = _PAYLOAD_RE.search(payload)
    if not match:
        return ProviderResult.failure(
            provider, f"腾讯行情返回无法解析：{symbol}", warnings=("parse_error", "fail_closed")
        )

    fields = match.group(1).split("~")
    if len(fields) <= _FIELD_PRICE:
        return ProviderResult.failure(
            provider, f"腾讯行情字段不足：{symbol}", warnings=("parse_error", "fail_closed")
        )

    try:
        price = float(fields[_FIELD_PRICE])
    except ValueError:
        return ProviderResult.failure(
            provider, f"腾讯行情价格无法解析：{symbol}", warnings=("parse_error", "fail_closed")
        )

    if price <= 0:
        # A suspended or delisted instrument quotes zero; that is not a price.
        return ProviderResult.failure(
            provider,
            f"{symbol} 当前无有效报价（可能停牌或退市）。",
            warnings=("no_quote", "fail_closed"),
        )

    return ProviderResult.success(
        provider,
        Quote(
            symbol=symbol,
            price=price,
            currency="CNY",
            as_of=_extract_date(fields),
            provider=provider,
            provenance={
                "source": provider,
                "symbol": symbol,
                "source_api": "qt.gtimg.cn",
                "upstream_ticker": ticker,
                "name": fields[_FIELD_NAME] if len(fields) > _FIELD_NAME else "",
                "synthetic": "false",
            },
        ),
        warnings=("real_market_data", "not_for_investment_decisions"),
    )


def _extract_date(fields: list[str]) -> date:
    """Finds the quote timestamp among the fields, falling back to today."""
    for value in fields:
        token = value.strip()
        if _COMPACT_DATETIME_RE.match(token):
            try:
                return datetime.strptime(token[:8], "%Y%m%d").date()
            except ValueError:
                continue
        if len(token) >= 10 and token[4] == "-" and token[7] == "-":
            try:
                return date.fromisoformat(token[:10])
            except ValueError:
                continue
    return date.today()
