from __future__ import annotations

import json
from datetime import date, datetime, timezone
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import (
    Adjustment,
    AssetClass,
    Market,
    OhlcvBar,
    ProviderResult,
    SymbolId,
)
from caishenfolio_core.market.credentials import load_secrets
from caishenfolio_core.market.fixture import SymbolHit
from caishenfolio_core.market.network import call_with_direct_fallback, humanize_market_error


class AlphaVantageMarketDataProvider:
    """Real US bars via Alpha Vantage. Requires free/paid API key. Never synthesizes."""

    PROVIDER_CODE = "alphavantage"
    _BASE = "https://www.alphavantage.co/query"

    def __init__(self, api_key: str | None = None) -> None:
        self._key = (
            api_key if api_key is not None else load_secrets().get("alphavantage_api_key", "")
        ).strip()

    @property
    def ready(self) -> bool:
        return bool(self._key)

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        limit = max(1, min(limit, 50))
        if not self.ready:
            return []
        q = (query or "").strip()
        parsed = SymbolId.try_parse(q)
        if parsed is not None and parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"}:
            return [
                SymbolHit(
                    parsed.value,
                    Market.US,
                    AssetClass.EQUITY,
                    parsed.code,
                    self.PROVIDER_CODE,
                )
            ]
        if q.isalpha():
            t = q.upper()
            return [
                SymbolHit(f"NASDAQ:{t}", Market.US, AssetClass.EQUITY, t, self.PROVIDER_CODE),
                SymbolHit(f"NYSE:{t}", Market.US, AssetClass.EQUITY, t, self.PROVIDER_CODE),
            ][:limit]
        return []

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        if interval is not BarInterval.DAILY:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "Alpha Vantage 本阶段仅接入日K；周K/月K请改用 akshare/yfinance 或 auto。",
                warnings=("unsupported_interval", "fail_closed"),
            )
        if not self._key:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "未配置 Alpha Vantage API Key。请在「数据源与密钥」填写，或设置 CAISHENFOLIO_ALPHAVANTAGE_API_KEY。"
                " 免费申请：https://www.alphavantage.co/support/#api-key",
                warnings=("missing_credentials", "fail_closed"),
            )
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"无效标的 '{symbol}'。",
                warnings=("fail_closed",),
            )
        if parsed.exchange not in {"NASDAQ", "NYSE", "AMEX", "US"}:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Alpha Vantage 本阶段主要用于美股，不支持 '{parsed.exchange}'。",
                warnings=("unsupported_exchange", "fail_closed"),
            )
        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "结束日期必须不早于开始日期。",
                warnings=("fail_closed",),
            )

        params = {
            "function": "TIME_SERIES_DAILY_ADJUSTED" if adjustment is not Adjustment.RAW else "TIME_SERIES_DAILY",
            "symbol": parsed.code,
            "outputsize": "full",
            "apikey": self._key,
            "datatype": "json",
        }
        url = f"{self._BASE}?{urlencode(params)}"

        try:
            def _fetch() -> dict[str, Any]:
                req = Request(url, headers={"User-Agent": "Caishenfolio/0.4"})
                with urlopen(req, timeout=30) as resp:  # noqa: S310 - fixed HTTPS host
                    return json.loads(resp.read().decode("utf-8"))

            payload = call_with_direct_fallback(_fetch)
        except (HTTPError, URLError, TimeoutError, json.JSONDecodeError) as exc:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                humanize_market_error(exc),
                warnings=("upstream_error", "fail_closed"),
            )
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                humanize_market_error(exc),
                warnings=("upstream_error", "fail_closed"),
            )

        if "Error Message" in payload:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Alpha Vantage 错误：{payload['Error Message']}",
                warnings=("upstream_error", "fail_closed"),
            )
        if "Note" in payload:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Alpha Vantage 限流或提示：{payload['Note']}",
                warnings=("rate_limited", "fail_closed"),
            )
        if "Information" in payload and "Time Series" not in str(payload.keys()):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Alpha Vantage：{payload.get('Information')}",
                warnings=("upstream_error", "fail_closed"),
            )

        series_key = next((k for k in payload if "Time Series" in k), None)
        if not series_key or not isinstance(payload.get(series_key), dict):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Alpha Vantage 未返回时间序列：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )

        series: dict[str, Any] = payload[series_key]
        bars: list[OhlcvBar] = []
        for day_text, row in series.items():
            try:
                day = date.fromisoformat(day_text[:10])
                if day < start or day > end:
                    continue
                if not isinstance(row, dict):
                    continue
                # keys like "1. open"
                o = float(row.get("1. open") or row.get("1. Open"))
                h = float(row.get("2. high") or row.get("2. High"))
                low = float(row.get("3. low") or row.get("3. Low"))
                if adjustment is not Adjustment.RAW and "5. adjusted close" in row:
                    c = float(row["5. adjusted close"])
                else:
                    c = float(row.get("4. close") or row.get("4. Close"))
                vol = float(row.get("6. volume") or row.get("5. volume") or 0)
            except Exception:  # noqa: BLE001
                continue
            bars.append(
                OhlcvBar(
                    timestamp_utc=datetime(day.year, day.month, day.day, tzinfo=timezone.utc),
                    open=o,
                    high=h,
                    low=low,
                    close=c,
                    volume=vol,
                    currency="USD",
                    adjustment=adjustment,
                    provider=self.PROVIDER_CODE,
                    amount=None,
                    provenance={
                        "source": self.PROVIDER_CODE,
                        "symbol": parsed.value,
                        "source_api": params["function"],
                        "synthetic": "false",
                    },
                )
            )

        bars.sort(key=lambda b: b.timestamp_utc)
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Alpha Vantage 区间内无数据：{parsed.value}",
                warnings=("empty_window", "fail_closed"),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=("real_market_data", "not_for_investment_decisions"),
        )
