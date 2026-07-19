from __future__ import annotations

from datetime import date, datetime, timedelta, timezone
from typing import Any

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import (
    Adjustment,
    AssetClass,
    Market,
    OhlcvBar,
    ProviderResult,
    SymbolId,
)
from caishenfolio_core.market.fixture import SymbolHit
from caishenfolio_core.market.network import call_with_direct_fallback, humanize_market_error


def _try_import_yf() -> Any | None:
    try:
        import yfinance as yf  # type: ignore

        return yf
    except Exception:  # noqa: BLE001
        return None


class YFinanceMarketDataProvider:
    """Real bars via Yahoo Finance (yfinance). Free, no API key. Never synthesizes."""

    PROVIDER_CODE = "yfinance"

    def __init__(self) -> None:
        self._yf = _try_import_yf()

    @property
    def ready(self) -> bool:
        return self._yf is not None

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        limit = max(1, min(limit, 50))
        q = (query or "").strip()
        if not q or self._yf is None:
            return []

        parsed = SymbolId.try_parse(q)
        if parsed is not None:
            yahoo = _to_yahoo_symbol(parsed)
            if yahoo is None:
                return []
            market = Market.US if parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"} else Market.HK
            return [
                SymbolHit(
                    parsed.value,
                    market,
                    AssetClass.EQUITY,
                    parsed.code,
                    self.PROVIDER_CODE,
                )
            ]

        # Bare ticker → offer US guesses
        ticker = q.upper()
        if not ticker.isalnum() and "." not in ticker:
            return []
        hits = [
            SymbolHit(f"NASDAQ:{ticker}", Market.US, AssetClass.EQUITY, ticker, self.PROVIDER_CODE),
            SymbolHit(f"NYSE:{ticker}", Market.US, AssetClass.EQUITY, ticker, self.PROVIDER_CODE),
        ]
        if ticker.isdigit() and len(ticker) <= 5:
            code = ticker.zfill(4) if len(ticker) <= 4 else ticker.zfill(5)
            hits.insert(
                0,
                SymbolHit(f"HKEX:{code}", Market.HK, AssetClass.EQUITY, code, self.PROVIDER_CODE),
            )
        return hits[:limit]

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        if self._yf is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "yfinance 未安装。请执行: pip install yfinance",
                warnings=("provider_unavailable", "fail_closed"),
            )
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"无效标的 '{symbol}'。",
                warnings=("fail_closed",),
            )
        yahoo = _to_yahoo_symbol(parsed)
        if yahoo is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"yfinance 不支持交易所 '{parsed.exchange}'（主要用于美股/港股）。",
                warnings=("unsupported_exchange", "fail_closed"),
            )
        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "结束日期必须不早于开始日期。",
                warnings=("fail_closed",),
            )

        yf_interval = {
            BarInterval.DAILY: "1d",
            BarInterval.WEEKLY: "1wk",
            BarInterval.MONTHLY: "1mo",
        }[interval]

        try:
            # yfinance end is exclusive → +1 day
            end_exclusive = end + timedelta(days=1)
            auto_adjust = adjustment is Adjustment.FORWARD

            def _download() -> Any:
                return self._yf.download(
                    tickers=yahoo,
                    start=start.isoformat(),
                    end=end_exclusive.isoformat(),
                    interval=yf_interval,
                    auto_adjust=auto_adjust,
                    progress=False,
                    threads=False,
                )

            df = call_with_direct_fallback(_download)
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                humanize_market_error(exc),
                warnings=("upstream_error", "fail_closed"),
            )

        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未从 Yahoo 取得行情：{parsed.value}（yahoo={yahoo}）",
                warnings=("empty_upstream", "fail_closed"),
            )

        # Flatten multiindex columns if present
        if hasattr(df.columns, "nlevels") and df.columns.nlevels > 1:
            try:
                df.columns = df.columns.get_level_values(0)
            except Exception:  # noqa: BLE001
                pass

        bars: list[OhlcvBar] = []
        currency = "USD" if parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"} else "HKD"
        for idx, row in df.iterrows():
            try:
                if hasattr(idx, "date"):
                    day = idx.date()  # type: ignore[union-attr]
                else:
                    day = date.fromisoformat(str(idx)[:10])
                if day < start or day > end:
                    continue
                o = float(row.get("Open", row.iloc[0]))
                h = float(row.get("High", row.iloc[1]))
                low = float(row.get("Low", row.iloc[2]))
                c = float(row.get("Close", row.iloc[3]))
                vol = float(row.get("Volume", 0) or 0)
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
                    currency=currency,
                    adjustment=adjustment,
                    provider=self.PROVIDER_CODE,
                    amount=None,
                    provenance={
                        "source": self.PROVIDER_CODE,
                        "symbol": parsed.value,
                        "yahoo_symbol": yahoo,
                        "source_api": "yfinance.download",
                        "interval": interval.value,
                        "synthetic": "false",
                    },
                )
            )

        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Yahoo 返回无法解析或区间为空：{parsed.value}",
                warnings=("parse_error", "fail_closed"),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=(
                "real_market_data",
                "not_for_investment_decisions",
                "yfinance_unofficial",
                f"interval:{interval.value}",
            ),
        )


def _to_yahoo_symbol(parsed: SymbolId) -> str | None:
    if parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"}:
        return parsed.code
    if parsed.exchange in {"HKEX", "HK"}:
        # Yahoo HK: 0700.HK
        code = parsed.code.lstrip("0") or "0"
        if parsed.code.isdigit():
            code = parsed.code.zfill(4)
        return f"{code}.HK"
    return None
