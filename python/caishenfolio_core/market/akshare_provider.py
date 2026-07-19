from __future__ import annotations

import re
from datetime import date, datetime, timezone
from functools import lru_cache
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
from caishenfolio_core.market.network import (
    apply_requests_trust_env,
    call_with_direct_fallback,
    force_direct_connection,
    humanize_market_error,
    trust_env_enabled,
)
from caishenfolio_core.market.symbol_index import fuzzy_search_a_share

_CODE_ONLY_RE = re.compile(r"^[0-9]{5,6}$")
_US_TICKER_RE = re.compile(r"^[A-Za-z][A-Za-z0-9.\-]{0,9}$")


def _try_import_akshare() -> Any | None:
    try:
        import akshare as ak  # type: ignore

        apply_requests_trust_env()
        return ak
    except Exception:  # noqa: BLE001 - dependency optional; fail-closed later
        return None


class AkshareMarketDataProvider:
    """Real market data via AkShare (public web sources). Never synthesizes bars."""

    PROVIDER_CODE = "akshare"

    def __init__(self) -> None:
        apply_requests_trust_env()
        self._ak = _try_import_akshare()

    @property
    def ready(self) -> bool:
        return self._ak is not None

    def _require_ak(self) -> Any | ProviderResult[list[OhlcvBar]]:
        if self._ak is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "真实行情依赖 akshare 未安装或不可用。请执行: pip install akshare",
                warnings=("provider_unavailable", "fail_closed"),
            )
        return self._ak

    def search(self, query: str = "", limit: int = 10) -> list[SymbolHit]:
        limit = max(1, min(limit, 50))
        q = (query or "").strip()
        hits: list[SymbolHit] = []
        # Allow fuzzy search even if temporary network import fails later for bars
        if self._ak is None:
            try:
                return fuzzy_search_a_share(q, limit=limit)
            except Exception:  # noqa: BLE001
                return []

        # Exact EXCHANGE:CODE
        parsed = SymbolId.try_parse(q)
        if parsed is not None:
            hit = self._resolve_known_symbol(parsed)
            return [hit] if hit is not None else []

        # Pure A-share / fund / ETF numeric code (6 digits)
        if _CODE_ONLY_RE.match(q) and len(q) <= 6:
            code6 = q.zfill(6)
            exchange = "SSE" if code6.startswith(("5", "6", "9")) else "SZSE"
            market, asset = self._classify_cn_code(code6)
            hits.append(
                SymbolHit(
                    f"{exchange}:{code6}",
                    market,
                    asset,
                    name=code6,
                    provider=self.PROVIDER_CODE,
                )
            )

        # Fuzzy A-share name/code (e.g. 浦发 → 浦发银行)
        try:
            for item in fuzzy_search_a_share(q, limit=limit):
                if all(h.symbol != item.symbol for h in hits):
                    hits.append(item)
                if len(hits) >= limit:
                    return hits[:limit]
        except Exception:  # noqa: BLE001
            pass

        try:
            for item in self._search_a_share(q, limit=limit):
                if all(h.symbol != item.symbol for h in hits):
                    hits.append(item)
                if len(hits) >= limit:
                    return hits[:limit]
        except Exception:  # noqa: BLE001 - search soft-fail; bars remain fail-closed
            pass

        # HK numeric 5-digit
        if q.isdigit() and len(q) <= 5:
            code = q.zfill(5)
            hits.append(
                SymbolHit(
                    f"HKEX:{code}",
                    Market.HK,
                    AssetClass.EQUITY,
                    name=code,
                    provider=self.PROVIDER_CODE,
                )
            )

        # US ticker guess
        if _US_TICKER_RE.match(q) and not q.isdigit():
            ticker = q.upper()
            hits.append(
                SymbolHit(
                    f"NASDAQ:{ticker}",
                    Market.US,
                    AssetClass.EQUITY,
                    name=ticker,
                    provider=self.PROVIDER_CODE,
                )
            )
            hits.append(
                SymbolHit(
                    f"NYSE:{ticker}",
                    Market.US,
                    AssetClass.EQUITY,
                    name=ticker,
                    provider=self.PROVIDER_CODE,
                )
            )

        # Note: skip heavy fund_etf_spot_em network dump on every search (was causing 60s timeouts).
        # Users can still query ETF by 6-digit code above.

        return hits[:limit]

    def historical_bars(
        self,
        symbol: str,
        start: date,
        end: date,
        adjustment: Adjustment = Adjustment.RAW,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        ak_or_err = self._require_ak()
        if isinstance(ak_or_err, ProviderResult):
            return ak_or_err
        ak = ak_or_err

        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"无效标的 '{symbol}'。期望格式 EXCHANGE:SYMBOL。",
                warnings=("fail_closed",),
            )
        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "结束日期必须不早于开始日期。",
                warnings=("fail_closed",),
            )

        try:
            result = self._historical_bars_once(ak, parsed, start, end, adjustment, interval)
            if (
                not result.ok
                and trust_env_enabled()
                and result.error
                and _looks_like_proxy_or_network(result.error)
            ):
                with force_direct_connection():
                    retry = self._historical_bars_once(ak, parsed, start, end, adjustment, interval)
                if retry.ok:
                    warnings = list(retry.warnings) + ["retried_without_system_proxy"]
                    return ProviderResult.success(
                        retry.provider,
                        list(retry.data or []),
                        warnings=tuple(warnings),
                    )
                return retry
            return result
        except Exception as exc:  # noqa: BLE001 - network/upstream; never invent
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                humanize_market_error(exc),
                warnings=("upstream_error", "fail_closed", "proxy_or_network"),
            )

    def _historical_bars_once(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
        interval: BarInterval,
    ) -> ProviderResult[list[OhlcvBar]]:
        if parsed.exchange in {"SSE", "SZSE", "BSE"}:
            if interval.is_intraday:
                return self._bars_ashare_min(ak, parsed, start, end, adjustment, interval)
            if interval.is_aggregate_from_daily:
                daily = self._bars_ashare(ak, parsed, start, end, adjustment, BarInterval.DAILY)
                return daily  # aggregation done in cache layer; raw daily OK
            return self._bars_ashare(ak, parsed, start, end, adjustment, interval)
        if parsed.exchange in {"HKEX", "HK"}:
            if interval.is_intraday:
                return ProviderResult.failure(
                    self.PROVIDER_CODE,
                    "港股分钟线本阶段未接入，请用日/周/月K。",
                    warnings=("unsupported_interval", "fail_closed"),
                )
            return self._bars_hk(ak, parsed, start, end, adjustment, interval)
        if parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"}:
            return self._bars_us(ak, parsed, start, end, adjustment, interval)
        if parsed.exchange in {"FUND", "OF"}:
            if interval is not BarInterval.DAILY:
                return ProviderResult.failure(
                    self.PROVIDER_CODE,
                    "基金净值序列本阶段仅支持日频。",
                    warnings=("unsupported_interval", "fail_closed"),
                )
            return self._bars_cn_fund(ak, parsed, start, end, adjustment)
        return ProviderResult.failure(
            self.PROVIDER_CODE,
            f"暂不支持交易所 '{parsed.exchange}' 的真实行情。",
            warnings=("unsupported_exchange", "fail_closed"),
        )

    def _resolve_known_symbol(self, parsed: SymbolId) -> SymbolHit | None:
        if parsed.exchange in {"SSE", "SZSE", "BSE"}:
            market, asset = self._classify_cn_code(parsed.code)
            name = parsed.code
            try:
                for hit in self._search_a_share(parsed.code, limit=5):
                    if hit.symbol.endswith(f":{parsed.code}"):
                        name = hit.name
                        break
            except Exception:  # noqa: BLE001
                pass
            return SymbolHit(parsed.value, market, asset, name, self.PROVIDER_CODE)
        if parsed.exchange in {"HKEX", "HK"}:
            return SymbolHit(parsed.value, Market.HK, AssetClass.EQUITY, parsed.code, self.PROVIDER_CODE)
        if parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"}:
            return SymbolHit(parsed.value, Market.US, AssetClass.EQUITY, parsed.code, self.PROVIDER_CODE)
        if parsed.exchange in {"FUND", "OF"}:
            return SymbolHit(parsed.value, Market.ETF, AssetClass.FUND, parsed.code, self.PROVIDER_CODE)
        return SymbolHit(parsed.value, Market.US, AssetClass.EQUITY, parsed.code, self.PROVIDER_CODE)

    @staticmethod
    def _classify_cn_code(code: str) -> tuple[Market, AssetClass]:
        # Rough classification for display; bars still come from real APIs.
        if code.startswith(("51", "15", "56", "58", "16")):
            return Market.ETF, AssetClass.ETF
        if code.startswith(("5", "1")) and not code.startswith(("51", "15")):
            return Market.ETF, AssetClass.FUND
        return Market.ASHARE, AssetClass.EQUITY

    def _search_a_share(self, query: str, limit: int) -> list[SymbolHit]:
        assert self._ak is not None
        df = _a_share_code_name(self._ak)
        if df is None or df.empty:
            return []
        q = query.strip().lower()
        rows = df
        if q:
            code_col = "code" if "code" in df.columns else df.columns[0]
            name_col = "name" if "name" in df.columns else df.columns[1]
            mask = (
                df[code_col].astype(str).str.lower().str.contains(q, na=False)
                | df[name_col].astype(str).str.lower().str.contains(q, na=False)
            )
            rows = df.loc[mask]
        hits: list[SymbolHit] = []
        code_col = "code" if "code" in rows.columns else rows.columns[0]
        name_col = "name" if "name" in rows.columns else rows.columns[1]
        for _, row in rows.head(limit).iterrows():
            code = str(row[code_col]).zfill(6)
            name = str(row[name_col])
            exchange = "SSE" if code.startswith(("5", "6", "9")) else "SZSE"
            market, asset = self._classify_cn_code(code)
            hits.append(
                SymbolHit(
                    f"{exchange}:{code}",
                    market,
                    asset,
                    name=name,
                    provider=self.PROVIDER_CODE,
                )
            )
        return hits

    def _search_cn_etf(self, query: str, limit: int) -> list[SymbolHit]:
        assert self._ak is not None
        if not query.strip():
            return []
        # fund_etf_spot_em is large; prefer name filter via fund_etf_fund_info_em if present.
        ak = self._ak
        fn = getattr(ak, "fund_etf_spot_em", None)
        if fn is None:
            return []
        df = fn()
        if df is None or getattr(df, "empty", True):
            return []
        # Expected columns often: 代码, 名称
        code_col = "代码" if "代码" in df.columns else df.columns[0]
        name_col = "名称" if "名称" in df.columns else df.columns[1]
        q = query.strip().lower()
        mask = (
            df[code_col].astype(str).str.lower().str.contains(q, na=False)
            | df[name_col].astype(str).str.lower().str.contains(q, na=False)
        )
        hits: list[SymbolHit] = []
        for _, row in df.loc[mask].head(limit).iterrows():
            code = str(row[code_col]).zfill(6)
            name = str(row[name_col])
            exchange = "SSE" if code.startswith(("5", "6")) else "SZSE"
            hits.append(
                SymbolHit(
                    f"{exchange}:{code}",
                    Market.ETF,
                    AssetClass.ETF,
                    name=name,
                    provider=self.PROVIDER_CODE,
                )
            )
        return hits

    def _bars_ashare(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        """Try multiple *real* A-share endpoints; never fall back to synthetic."""
        adjust = _to_ak_adjust(adjustment)
        start_s = start.strftime("%Y%m%d")
        end_s = end.strftime("%Y%m%d")
        period = interval.value  # daily / weekly / monthly
        errors: list[str] = []

        def try_hist() -> Any:
            return ak.stock_zh_a_hist(
                symbol=parsed.code,
                period=period,
                start_date=start_s,
                end_date=end_s,
                adjust=adjust,
            )

        attempts: list[tuple[str, Any]] = [("stock_zh_a_hist", try_hist)]

        hist_tx = getattr(ak, "stock_zh_a_hist_tx", None)
        if hist_tx is not None:
            def try_tx() -> Any:
                # Tencent source — still real market data.
                return hist_tx(
                    symbol=parsed.code,
                    start_date=start_s,
                    end_date=end_s,
                    adjust=adjust,
                )

            attempts.append(("stock_zh_a_hist_tx", try_tx))

        daily = getattr(ak, "stock_zh_a_daily", None)
        if daily is not None and interval is BarInterval.DAILY:
            def try_daily() -> Any:
                # Symbol form often sh600000 / sz000001
                prefix = (
                    "sh"
                    if parsed.exchange in {"SSE", "BSE"} or parsed.code.startswith(("5", "6", "9"))
                    else "sz"
                )
                kwargs: dict[str, Any] = {"symbol": f"{prefix}{parsed.code}"}
                if adjust:
                    kwargs["adjust"] = adjust
                return daily(**kwargs)

            attempts.append(("stock_zh_a_daily", try_daily))

        for api_name, fetcher in attempts:
            try:
                df = fetcher()
            except Exception as exc:  # noqa: BLE001
                errors.append(f"{api_name}: {exc}")
                continue
            if df is None or getattr(df, "empty", True):
                errors.append(f"{api_name}: empty")
                continue
            bars = _df_to_bars(
                df,
                provider=self.PROVIDER_CODE,
                currency="CNY",
                adjustment=adjustment,
                symbol=parsed.value,
                source_api=api_name,
                date_candidates=("日期", "date", "date"),
            )
            # daily APIs may return full history — clip window
            bars = [bar for bar in bars if start <= bar.timestamp_utc.date() <= end]
            if not bars:
                errors.append(f"{api_name}: no rows in window")
                continue
            return ProviderResult.success(
                self.PROVIDER_CODE,
                bars,
                warnings=(
                    "real_market_data",
                    "not_for_investment_decisions",
                    f"source_api:{api_name}",
                    f"interval:{interval.value}",
                ),
            )

        detail = "；".join(errors) if errors else "无可用接口"
        return ProviderResult.failure(
            self.PROVIDER_CODE,
            humanize_market_error(f"未从上游取得 A 股行情：{parsed.value}（{detail}）"),
            warnings=("empty_upstream", "fail_closed"),
        )

    def _bars_ashare_min(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
        interval: BarInterval,
    ) -> ProviderResult[list[OhlcvBar]]:
        """A-share minute bars via Eastmoney helper when available."""
        period_map = {
            BarInterval.M1: "1",
            BarInterval.M5: "5",
            BarInterval.M15: "15",
            BarInterval.M30: "30",
            BarInterval.M60: "60",
        }
        period = period_map.get(interval)
        if period is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"不支持的分钟周期 {interval.value}",
                warnings=("unsupported_interval", "fail_closed"),
            )
        fn = getattr(ak, "stock_zh_a_hist_min_em", None)
        if fn is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "当前 akshare 缺少 stock_zh_a_hist_min_em 分钟线接口。",
                warnings=("unsupported_api", "fail_closed"),
            )
        try:
            df = fn(
                symbol=parsed.code,
                start_date=f"{start.strftime('%Y-%m-%d')} 09:30:00",
                end_date=f"{end.strftime('%Y-%m-%d')} 15:00:00",
                period=period,
                adjust=_to_ak_adjust(adjustment),
            )
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                humanize_market_error(exc),
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得分钟线：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )
        bars = _df_to_bars(
            df,
            provider=self.PROVIDER_CODE,
            currency="CNY",
            adjustment=adjustment,
            symbol=parsed.value,
            source_api="stock_zh_a_hist_min_em",
            date_candidates=("时间", "datetime", "日期", "date"),
        )
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"分钟线无法解析：{parsed.value}",
                warnings=("parse_error", "fail_closed"),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=("real_market_data", "intraday", f"interval:{interval.value}", "not_for_investment_decisions"),
        )

    def _bars_hk(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        code = parsed.code.zfill(5)
        # stock_hk_hist uses symbol like "00700"; period daily/weekly/monthly when supported
        df = ak.stock_hk_hist(
            symbol=code,
            period=interval.value,
            start_date=start.strftime("%Y%m%d"),
            end_date=end.strftime("%Y%m%d"),
            adjust=_to_ak_adjust(adjustment),
        )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未从上游取得港股行情：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )
        bars = _df_to_bars(
            df,
            provider=self.PROVIDER_CODE,
            currency="HKD",
            adjustment=adjustment,
            symbol=parsed.value,
            source_api="stock_hk_hist",
            date_candidates=("日期", "date"),
        )
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"上游返回无法解析的港股行情：{parsed.value}",
                warnings=("parse_error", "fail_closed"),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=("real_market_data", "not_for_investment_decisions", f"interval:{interval.value}"),
        )

    def _bars_us(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
        interval: BarInterval = BarInterval.DAILY,
    ) -> ProviderResult[list[OhlcvBar]]:
        ticker = parsed.code
        # Prefer daily history by symbol; API variants differ across akshare versions.
        df = None
        source_api = ""
        errors: list[str] = []
        for api_name, kwargs in (
            ("stock_us_hist", {"symbol": ticker, "period": interval.value, "start_date": start.strftime("%Y%m%d"), "end_date": end.strftime("%Y%m%d"), "adjust": _to_ak_adjust(adjustment)}),
            ("stock_us_daily", {"symbol": ticker, "adjust": _to_ak_adjust(adjustment)}),
        ):
            fn = getattr(ak, api_name, None)
            if fn is None:
                continue
            try:
                candidate = fn(**kwargs)
                if candidate is not None and not getattr(candidate, "empty", True):
                    df = candidate
                    source_api = api_name
                    break
            except TypeError:
                # signature mismatch — try fewer kwargs
                try:
                    candidate = fn(symbol=ticker)
                    if candidate is not None and not getattr(candidate, "empty", True):
                        df = candidate
                        source_api = api_name
                        break
                except Exception as exc:  # noqa: BLE001
                    errors.append(f"{api_name}: {exc}")
            except Exception as exc:  # noqa: BLE001
                errors.append(f"{api_name}: {exc}")

        if df is None or getattr(df, "empty", True):
            detail = ("；".join(errors) if errors else "无可用 US 接口或空结果")
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未从上游取得美股行情：{parsed.value}（{detail}）",
                warnings=("empty_upstream", "fail_closed"),
            )

        bars = _df_to_bars(
            df,
            provider=self.PROVIDER_CODE,
            currency="USD",
            adjustment=adjustment,
            symbol=parsed.value,
            source_api=source_api,
            date_candidates=("日期", "date", "Date"),
        )
        # Filter to requested window when daily API returns full history
        bars = [
            bar
            for bar in bars
            if start <= bar.timestamp_utc.date() <= end
        ]
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"美股上游有数据但不在请求区间内：{parsed.value}",
                warnings=("empty_window", "fail_closed"),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=("real_market_data", "not_for_investment_decisions"),
        )

    def _bars_cn_fund(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
    ) -> ProviderResult[list[OhlcvBar]]:
        code = parsed.code
        fn = getattr(ak, "fund_open_fund_info_em", None)
        if fn is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "当前 akshare 缺少公募基金净值接口 fund_open_fund_info_em。",
                warnings=("unsupported_api", "fail_closed"),
            )
        # indicator 单位净值; 接口返回可能不含 OHLCV 完整字段
        try:
            df = fn(symbol=code, indicator="单位净值走势")
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"公募基金净值获取失败：{parsed.value}（{exc}）",
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未从上游取得基金净值：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )
        # Map NAV series to OHLC with open=high=low=close=nav (documented as NAV, not trade bars)
        date_col = "净值日期" if "净值日期" in df.columns else df.columns[0]
        nav_col = "单位净值" if "单位净值" in df.columns else df.columns[1]
        bars: list[OhlcvBar] = []
        for _, row in df.iterrows():
            try:
                day = _parse_day(row[date_col])
                if day is None or day < start or day > end:
                    continue
                nav = float(row[nav_col])
            except Exception:  # noqa: BLE001
                continue
            bars.append(
                OhlcvBar(
                    timestamp_utc=datetime(day.year, day.month, day.day, tzinfo=timezone.utc),
                    open=nav,
                    high=nav,
                    low=nav,
                    close=nav,
                    volume=0.0,
                    currency="CNY",
                    adjustment=adjustment,
                    provider=self.PROVIDER_CODE,
                    amount=None,
                    provenance={
                        "source": self.PROVIDER_CODE,
                        "symbol": parsed.value,
                        "source_api": "fund_open_fund_info_em",
                        "series": "unit_nav",
                        "synthetic": "false",
                    },
                )
            )
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"基金净值无落在区间内的数据：{parsed.value}",
                warnings=("empty_window", "fail_closed"),
            )
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=(
                "real_market_data",
                "fund_nav_not_ohlcv",
                "not_for_investment_decisions",
            ),
        )


@lru_cache(maxsize=1)
def _a_share_code_name(ak: Any) -> Any:
    return call_with_direct_fallback(lambda: ak.stock_info_a_code_name())


def _to_ak_adjust(adjustment: Adjustment) -> str:
    if adjustment is Adjustment.FORWARD:
        return "qfq"
    if adjustment is Adjustment.BACKWARD:
        return "hfq"
    return ""


def _looks_like_proxy_or_network(message: str) -> bool:
    lower = message.lower()
    return any(
        token in lower
        for token in (
            "proxy",
            "max retries",
            "timed out",
            "timeout",
            "connection",
            "remote end closed",
            "ssl",
            "name resolution",
            "getaddrinfo",
            "网络",
            "代理",
        )
    )


def _parse_day(value: Any) -> date | None:
    if value is None:
        return None
    if hasattr(value, "date") and callable(value.date):
        try:
            return value.date()  # type: ignore[no-any-return]
        except Exception:  # noqa: BLE001
            pass
    text = str(value).strip().replace("/", "-")
    if not text:
        return None
    try:
        return date.fromisoformat(text[:10])
    except ValueError:
        try:
            return datetime.strptime(text[:10], "%Y-%m-%d").date()
        except ValueError:
            return None


def _pick_column(df: Any, candidates: tuple[str, ...]) -> str | None:
    for name in candidates:
        if name in df.columns:
            return name
    # case-insensitive fallback
    lower_map = {str(c).lower(): c for c in df.columns}
    for name in candidates:
        if name.lower() in lower_map:
            return lower_map[name.lower()]
    return None


def _df_to_bars(
    df: Any,
    *,
    provider: str,
    currency: str,
    adjustment: Adjustment,
    symbol: str,
    source_api: str,
    date_candidates: tuple[str, ...],
) -> list[OhlcvBar]:
    date_col = _pick_column(df, date_candidates)
    open_col = _pick_column(df, ("开盘", "open", "Open"))
    high_col = _pick_column(df, ("最高", "high", "High"))
    low_col = _pick_column(df, ("最低", "low", "Low"))
    close_col = _pick_column(df, ("收盘", "close", "Close"))
    vol_col = _pick_column(df, ("成交量", "volume", "Volume"))
    amount_col = _pick_column(df, ("成交额", "amount", "Amount", "turnover"))
    if not all([date_col, open_col, high_col, low_col, close_col]):
        return []

    bars: list[OhlcvBar] = []
    for _, row in df.iterrows():
        day = _parse_day(row[date_col])
        if day is None:
            continue
        try:
            o = float(row[open_col])
            h = float(row[high_col])
            low = float(row[low_col])
            c = float(row[close_col])
            vol = float(row[vol_col]) if vol_col is not None else 0.0
            amount = float(row[amount_col]) if amount_col is not None else None
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
                provider=provider,
                amount=amount,
                provenance={
                    "source": provider,
                    "symbol": symbol,
                    "source_api": source_api,
                    "synthetic": "false",
                },
            )
        )
    bars.sort(key=lambda item: item.timestamp_utc)
    return bars
