from __future__ import annotations

from datetime import date, datetime, timezone
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
from caishenfolio_core.market.credentials import load_secrets
from caishenfolio_core.market.fixture import SymbolHit
from caishenfolio_core.market.network import humanize_market_error


class TushareMarketDataProvider:
    """Real A-share bars via Tushare Pro. Requires user token. Never synthesizes."""

    PROVIDER_CODE = "tushare"

    def __init__(self, token: str | None = None) -> None:
        self._token = (token if token is not None else load_secrets().get("tushare_token", "")).strip()
        self._api: Any | None = None
        if self._token:
            try:
                import tushare as ts  # type: ignore

                self._api = ts.pro_api(self._token)
            except Exception:  # noqa: BLE001
                self._api = None

    @property
    def ready(self) -> bool:
        return self._api is not None and bool(self._token)

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        limit = max(1, min(limit, 50))
        if not self.ready:
            return []
        q = (query or "").strip()
        parsed = SymbolId.try_parse(q)
        if parsed is not None and parsed.exchange in {"SSE", "SZSE", "BSE"}:
            return [
                SymbolHit(
                    parsed.value,
                    Market.ASHARE,
                    AssetClass.EQUITY,
                    parsed.code,
                    self.PROVIDER_CODE,
                )
            ]
        if q.isdigit() and len(q) <= 6:
            code = q.zfill(6)
            exchange = "SSE" if code.startswith(("5", "6", "9")) else "SZSE"
            return [
                SymbolHit(
                    f"{exchange}:{code}",
                    Market.ASHARE,
                    AssetClass.EQUITY,
                    code,
                    self.PROVIDER_CODE,
                )
            ]
        return []

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        if not self._token:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "未配置 Tushare token。请在「数据源与密钥」填写，或设置环境变量 CAISHENFOLIO_TUSHARE_TOKEN。"
                " 申请：https://tushare.pro",
                warnings=("missing_credentials", "fail_closed"),
            )
        if self._api is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "tushare 未安装或初始化失败。请执行: pip install tushare",
                warnings=("provider_unavailable", "fail_closed"),
            )

        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"无效标的 '{symbol}'。",
                warnings=("fail_closed",),
            )
        ts_code = _to_ts_code(parsed)
        if ts_code is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Tushare 本阶段主要用于 A 股，不支持 '{parsed.exchange}'。",
                warnings=("unsupported_exchange", "fail_closed"),
            )
        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "结束日期必须不早于开始日期。",
                warnings=("fail_closed",),
            )

        try:
            # daily is unadjusted; weekly/monthly use pro APIs when available
            start_s = start.strftime("%Y%m%d")
            end_s = end.strftime("%Y%m%d")
            if interval is BarInterval.WEEKLY:
                df = self._api.weekly(ts_code=ts_code, start_date=start_s, end_date=end_s)
            elif interval is BarInterval.MONTHLY:
                df = self._api.monthly(ts_code=ts_code, start_date=start_s, end_date=end_s)
            else:
                df = self._api.daily(ts_code=ts_code, start_date=start_s, end_date=end_s)
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                humanize_market_error(exc),
                warnings=("upstream_error", "fail_closed"),
            )

        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Tushare 未返回行情（可能积分不足或代码无效）：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )

        bars: list[OhlcvBar] = []
        for _, row in df.iterrows():
            try:
                day = datetime.strptime(str(row["trade_date"]), "%Y%m%d").date()
                bars.append(
                    OhlcvBar(
                        timestamp_utc=datetime(day.year, day.month, day.day, tzinfo=timezone.utc),
                        open=float(row["open"]),
                        high=float(row["high"]),
                        low=float(row["low"]),
                        close=float(row["close"]),
                        volume=float(row.get("vol", 0) or 0) * 100.0,  # 手 → 股（约）
                        currency="CNY",
                        adjustment=Adjustment.RAW if adjustment is Adjustment.RAW else adjustment,
                        provider=self.PROVIDER_CODE,
                        amount=float(row["amount"]) * 1000.0 if row.get("amount") is not None else None,
                        provenance={
                            "source": self.PROVIDER_CODE,
                            "symbol": parsed.value,
                            "ts_code": ts_code,
                            "source_api": "pro.daily",
                            "synthetic": "false",
                            "volume_unit": "shares_approx_from_hands",
                        },
                    )
                )
            except Exception:  # noqa: BLE001
                continue

        bars.sort(key=lambda b: b.timestamp_utc)
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"Tushare 数据无法解析：{parsed.value}",
                warnings=("parse_error", "fail_closed"),
            )
        warnings = ["real_market_data", "not_for_investment_decisions"]
        if adjustment is not Adjustment.RAW:
            warnings.append("tushare_daily_is_raw_adjust_requested_ignored")
        return ProviderResult.success(self.PROVIDER_CODE, bars, warnings=tuple(warnings))


def _to_ts_code(parsed: SymbolId) -> str | None:
    code = parsed.code
    if parsed.exchange == "SSE":
        return f"{code}.SH"
    if parsed.exchange == "SZSE":
        return f"{code}.SZ"
    if parsed.exchange == "BSE":
        return f"{code}.BJ"
    return None
