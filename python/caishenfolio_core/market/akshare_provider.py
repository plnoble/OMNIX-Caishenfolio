from __future__ import annotations

import re
from datetime import date, datetime, timedelta, timezone  # noqa: F401
from functools import lru_cache
from typing import Any

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.markets import (
    CN_FUND_EXCHANGE,
    classify_cn_code,
    cn_exchange_for_code,
)
from caishenfolio_core.data.models import (
    Adjustment,
    AssetClass,
    FinancialPeriod,
    FxQuote,
    MarketRegion,
    NavPoint,
    OhlcvBar,
    ProviderResult,
    Quote,
    SymbolId,
    ValuationPoint,
)
from caishenfolio_core.market.em_fundamentals import (
    fetch_hk as fetch_hk_fundamentals,
    fetch_us as fetch_us_fundamentals,
)
from caishenfolio_core.market.fixture import SymbolHit
from caishenfolio_core.market.valuation_series import (
    describe_method as describe_valuation_method,
    reconstruct_valuation,
)
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
#: Enough calendar days to clear a long holiday run in any covered market.
_QUOTE_LOOKBACK_DAYS = 12


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
        #: Optional wider bar channel, set by the composite. Reconstructed US/HK valuation needs
        #: prices, and the source that has the fundamentals is not always the one that can
        #: reach the prices.
        self.bar_source: Any | None = None

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
            exchange = cn_exchange_for_code(code6)
            market, asset = self._classify_cn_code(code6, exchange)
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
                    MarketRegion.HK,
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
                    MarketRegion.US,
                    AssetClass.EQUITY,
                    name=ticker,
                    provider=self.PROVIDER_CODE,
                )
            )
            hits.append(
                SymbolHit(
                    f"NYSE:{ticker}",
                    MarketRegion.US,
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
            asset = classify_cn_code(parsed.code, parsed.exchange)
            if asset in {AssetClass.BOND, AssetClass.CONVERTIBLE_BOND}:
                if interval is not BarInterval.DAILY:
                    return ProviderResult.failure(
                        self.PROVIDER_CODE,
                        "债券行情本阶段仅支持日频。",
                        warnings=("unsupported_interval", "fail_closed"),
                    )
                return self._bars_cn_bond(ak, parsed, start, end, adjustment, asset)
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
            market, asset = self._classify_cn_code(parsed.code, parsed.exchange)
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
            return SymbolHit(parsed.value, MarketRegion.HK, AssetClass.EQUITY, parsed.code, self.PROVIDER_CODE)
        if parsed.exchange in {"NASDAQ", "NYSE", "AMEX", "US"}:
            return SymbolHit(parsed.value, MarketRegion.US, AssetClass.EQUITY, parsed.code, self.PROVIDER_CODE)
        if parsed.exchange in {"FUND", "OF"}:
            return SymbolHit(parsed.value, MarketRegion.CN, AssetClass.MUTUAL_FUND, parsed.code, self.PROVIDER_CODE)
        return SymbolHit(parsed.value, MarketRegion.US, AssetClass.EQUITY, parsed.code, self.PROVIDER_CODE)

    @staticmethod
    def _classify_cn_code(code: str, exchange: str | None = None) -> tuple[MarketRegion, AssetClass]:
        return MarketRegion.CN, classify_cn_code(code, exchange)

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
                    MarketRegion.CN,
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

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        """Last close (or last NAV for funds) over a short recent window."""
        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效标的 '{symbol}'。", warnings=("fail_closed",)
            )
        parsed = parsed.normalized()
        today = date.today()
        window_start = today - timedelta(days=_QUOTE_LOOKBACK_DAYS)

        if parsed.exchange == CN_FUND_EXCHANGE:
            nav = self.nav_series(parsed.value, window_start, today)
            if not nav.ok or not nav.data:
                return ProviderResult.failure(
                    self.PROVIDER_CODE,
                    nav.error or f"未取得 {parsed.value} 的最新净值。",
                    warnings=nav.warnings or ("fail_closed",),
                )
            last_nav = nav.data[-1]
            return ProviderResult.success(
                self.PROVIDER_CODE,
                Quote(
                    symbol=parsed.value,
                    price=last_nav.nav,
                    currency=last_nav.currency,
                    as_of=last_nav.as_of,
                    provider=self.PROVIDER_CODE,
                    provenance={**dict(last_nav.provenance), "channel": "latest_quote"},
                ),
                warnings=nav.warnings,
            )

        bars = self.historical_bars(parsed.value, window_start, today)
        if not bars.ok or not bars.data:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                bars.error or f"未取得 {parsed.value} 的最新价格。",
                warnings=bars.warnings or ("fail_closed",),
            )
        last = bars.data[-1]
        return ProviderResult.success(
            self.PROVIDER_CODE,
            Quote(
                symbol=parsed.value,
                price=last.close,
                currency=last.currency,
                as_of=last.timestamp_utc.date(),
                provider=self.PROVIDER_CODE,
                provenance={**dict(last.provenance), "channel": "latest_quote"},
            ),
            warnings=bars.warnings,
        )

    def nav_series(self, symbol: str, start: date, end: date) -> ProviderResult[list[NavPoint]]:
        """Daily NAV for an off-exchange open-end fund — the fund's own price channel."""
        ak = self._require_ak()
        if isinstance(ak, ProviderResult):
            return ak  # type: ignore[return-value]

        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效标的 '{symbol}'。", warnings=("fail_closed",)
            )
        parsed = parsed.normalized()
        if parsed.exchange != CN_FUND_EXCHANGE:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"'{parsed.value}' 不是场外基金（应为 FUND:代码）。",
                warnings=("unsupported_symbol", "fail_closed"),
            )
        if end < start:
            return ProviderResult.failure(
                self.PROVIDER_CODE, "结束日期必须不早于开始日期。", warnings=("fail_closed",)
            )

        fn = getattr(ak, "fund_open_fund_info_em", None)
        if fn is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "当前 akshare 缺少公募基金净值接口 fund_open_fund_info_em。",
                warnings=("unsupported_api", "fail_closed"),
            )

        try:
            df = call_with_direct_fallback(lambda: fn(symbol=parsed.code, indicator="单位净值走势"))
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"公募基金净值获取失败：{parsed.value}（{humanize_market_error(exc)}）",
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未从上游取得基金净值：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )

        cols = [str(c) for c in df.columns]
        date_col = _pick_column(df, ("净值日期", "date")) or cols[0]
        nav_col = _pick_column(df, ("单位净值", "nav")) or (cols[1] if len(cols) > 1 else cols[0])
        accum_col = _pick_column(df, ("累计净值",))
        growth_col = _pick_column(df, ("日增长率",))

        points: list[NavPoint] = []
        for _, row in df.iterrows():
            try:
                day = _parse_day(row[date_col])
                if day is None or day < start or day > end:
                    continue
                nav = float(row[nav_col])
            except Exception:  # noqa: BLE001
                continue
            points.append(
                NavPoint(
                    as_of=day,
                    nav=nav,
                    accumulated_nav=_optional_float(row, accum_col),
                    daily_return=_optional_float(row, growth_col),
                    currency="CNY",
                    provider=self.PROVIDER_CODE,
                    provenance={
                        "source": self.PROVIDER_CODE,
                        "symbol": parsed.value,
                        "source_api": "fund_open_fund_info_em",
                        "series": "unit_nav",
                        "synthetic": "false",
                    },
                )
            )

        if not points:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"基金净值无落在区间内的数据：{parsed.value}",
                warnings=("empty_window", "fail_closed"),
            )

        points.sort(key=lambda p: p.as_of)
        return ProviderResult.success(
            self.PROVIDER_CODE,
            points,
            warnings=("real_market_data", "not_for_investment_decisions"),
        )

    def valuation_history(self, symbol: str, years: int = 10) -> ProviderResult[list[ValuationPoint]]:
        """Daily PE/PB/dividend-yield history — the distribution a percentile is taken over."""
        ak = self._require_ak()
        if isinstance(ak, ProviderResult):
            return ak  # type: ignore[return-value]

        parsed = SymbolId.try_parse(symbol)
        if parsed is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效标的 '{symbol}'。", warnings=("fail_closed",))

        exchange = parsed.normalized().exchange
        if exchange not in {"SSE", "SZSE", "BSE"}:
            # No free source publishes a ready-made PE/PB series for US and HK, so those are
            # computed from price and per-share fundamentals instead.
            return self._reconstructed_valuation(parsed.normalized(), years)

        fn = getattr(ak, "stock_a_indicator_lg", None)
        if fn is None:
            # stock_a_indicator_lg was dropped from akshare, which silently took the A-share
            # valuation percentile — the feature this whole page rests on — with it.
            return self._baidu_valuation(parsed.normalized(), years)

        code = parsed.normalized().code
        try:
            df = call_with_direct_fallback(lambda: fn(symbol=code))
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"估值历史获取失败：{symbol}（{humanize_market_error(exc)}）",
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得估值历史：{symbol}",
                warnings=("empty_upstream", "fail_closed"),
            )

        date_col = _pick_column(df, ("trade_date", "date", "日期"))
        pe_col = _pick_column(df, ("pe_ttm", "pe", "市盈率"))
        pb_col = _pick_column(df, ("pb", "市净率"))
        dy_col = _pick_column(df, ("dv_ttm", "dv_ratio", "股息率"))
        if date_col is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE, "估值历史字段无法解析。", warnings=("parse_error", "fail_closed")
            )

        cutoff = date.today() - timedelta(days=int(years * 365.25))
        points: list[ValuationPoint] = []
        for _, row in df.iterrows():
            day = _parse_day(row[date_col])
            if day is None or day < cutoff:
                continue
            points.append(
                ValuationPoint(
                    as_of=day,
                    pe=_optional_float(row, pe_col),
                    pb=_optional_float(row, pb_col),
                    dividend_yield=_optional_float(row, dy_col),
                )
            )

        if not points:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"估值历史在近 {years} 年内无数据：{symbol}",
                warnings=("empty_window", "fail_closed"),
            )

        points.sort(key=lambda p: p.as_of)
        return ProviderResult.success(
            self.PROVIDER_CODE,
            points,
            warnings=("real_market_data", "not_for_investment_decisions"),
        )

    #: Baidu serves fixed windows rather than an arbitrary number of years.
    _BAIDU_PERIODS = ((1, "近一年"), (3, "近三年"), (5, "近五年"), (10, "近十年"))

    def _baidu_valuation(
        self, parsed: SymbolId, years: int
    ) -> ProviderResult[list[ValuationPoint]]:
        """A-share PE/PB from Baidu, the replacement for the retired akshare endpoint.

        PE and PB come from separate calls and are merged by date; a date present in only one of
        them keeps that metric and leaves the other empty rather than dropping the day.
        """
        period = next(
            (label for limit, label in self._BAIDU_PERIODS if years <= limit), "全部")

        merged: dict[date, dict[str, float]] = {}
        errors: list[str] = []
        for indicator, field in (("市盈率(TTM)", "pe"), ("市净率", "pb")):
            rows = self._baidu_series(parsed.code, indicator, period)
            if rows is None:
                errors.append(indicator)
                continue
            for day, value in rows:
                merged.setdefault(day, {})[field] = value

        if not merged:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得估值历史：{parsed.value}（{'、'.join(errors) or '上游无数据'}）",
                warnings=("empty_upstream", "fail_closed"),
            )

        points = [
            ValuationPoint(as_of=day, pe=values.get("pe"), pb=values.get("pb"))
            for day, values in sorted(merged.items())
        ]

        warnings = ["real_market_data", "not_for_investment_decisions", "valuation_source:baidu"]
        if errors:
            # Say which metric is missing rather than showing a page that looks complete.
            warnings.append("valuation_partial:" + ",".join(errors))

        return ProviderResult.success(self.PROVIDER_CODE, points, warnings=tuple(warnings))

    def _baidu_series(
        self, code: str, indicator: str, period: str
    ) -> list[tuple[date, float]] | None:
        fn = getattr(self._ak, "stock_zh_valuation_baidu", None)
        if fn is None:
            return None

        try:
            df = call_with_direct_fallback(
                lambda: fn(symbol=code, indicator=indicator, period=period))
        except Exception:  # noqa: BLE001
            return None
        if df is None or getattr(df, "empty", True):
            return None

        out: list[tuple[date, float]] = []
        for _, row in df.iterrows():
            day = _parse_day(row.get("date"))
            value = _optional_float(row, "value")
            # A zero or negative multiple is not a valuation; it corrupts the percentile.
            if day is not None and value is not None and value > 0:
                out.append((day, value))
        return out or None

    def _reconstructed_valuation(
        self, parsed: SymbolId, years: int
    ) -> ProviderResult[list[ValuationPoint]]:
        """PE/PB for US and HK names, computed from prices and published per-share figures.

        Marked as reconstructed in the warnings so no layer above can present it as a vendor
        series — the method and its limits belong on screen next to the number.
        """
        if parsed.exchange == "HKEX":
            fundamentals = fetch_hk_fundamentals(parsed.code)
            market = "港股"
        elif parsed.exchange in {"NASDAQ", "NYSE", "AMEX"}:
            fundamentals = fetch_us_fundamentals(parsed.code)
            market = "美股"
        else:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"估值历史暂不支持 {parsed.exchange}（当前支持 A 股 / 港股 / 美股）。",
                warnings=("unsupported_symbol", "fail_closed"),
            )

        if not fundamentals:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得 {parsed.value} 的每股财务数据，无法推算估值历史。",
                warnings=("empty_upstream", "fail_closed"),
            )

        end = date.today()
        start = end - timedelta(days=int(years * 365.25))
        # Prices may well come from a different source than the fundamentals — akshare's HK bar
        # endpoint is commonly blocked where yfinance is not — so ask the whole chain when one
        # has been wired in.
        bar_source = self.bar_source or self
        bars = bar_source.historical_bars(parsed.value, start, end)
        if not bars.ok or not bars.data:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得 {parsed.value} 的历史价格，无法推算估值历史。",
                warnings=("empty_upstream", "fail_closed"),
            )

        points = reconstruct_valuation(bars.data, fundamentals)
        if not points:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"{parsed.value} 的价格区间早于最早一期财报，无法推算估值历史。",
                warnings=("empty_window", "fail_closed"),
            )

        estimated = any(item.effective_date_estimated for item in fundamentals)
        return ProviderResult.success(
            self.PROVIDER_CODE,
            points,
            warnings=(
                "real_market_data",
                "not_for_investment_decisions",
                "valuation_reconstructed",
                f"valuation_market:{market}",
                f"valuation_method:{describe_valuation_method(fundamentals)}",
            )
            + (("valuation_announcement_date_estimated",) if estimated else ()),
        )

    def financial_summary(self, symbol: str, periods: int = 5) -> ProviderResult[list[FinancialPeriod]]:
        """Headline figures as filed: revenue, net profit, EPS, ROE."""
        ak = self._require_ak()
        if isinstance(ak, ProviderResult):
            return ak  # type: ignore[return-value]

        parsed = SymbolId.try_parse(symbol)
        if parsed is None or parsed.normalized().exchange not in {"SSE", "SZSE", "BSE"}:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"财务摘要当前仅支持 A 股（收到 '{symbol}'）。",
                warnings=("unsupported_symbol", "fail_closed"),
            )

        fn = getattr(ak, "stock_financial_abstract", None)
        if fn is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "当前 akshare 缺少财务摘要接口 stock_financial_abstract。",
                warnings=("unsupported_api", "fail_closed"),
            )

        try:
            df = call_with_direct_fallback(lambda: fn(symbol=parsed.normalized().code))
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"财务摘要获取失败：{symbol}（{humanize_market_error(exc)}）",
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未取得财务摘要：{symbol}",
                warnings=("empty_upstream", "fail_closed"),
            )

        result = _financial_periods_from(df, periods)
        if not result:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"财务摘要无法解析：{symbol}",
                warnings=("parse_error", "fail_closed"),
            )

        return ProviderResult.success(
            self.PROVIDER_CODE,
            result,
            warnings=("real_market_data", "not_for_investment_decisions"),
        )

    def fx_rate(self, base_currency: str, quote_currency_code: str) -> ProviderResult[FxQuote]:
        """CN interbank spot quotes. Yahoo covers pairs this source does not."""
        ak = self._require_ak()
        if isinstance(ak, ProviderResult):
            return ak  # type: ignore[return-value]

        base = (base_currency or "").strip().upper()
        quote = (quote_currency_code or "").strip().upper()
        if not base or not quote or base == quote:
            return ProviderResult.failure(
                self.PROVIDER_CODE, f"无效货币对 {base}/{quote}。", warnings=("fail_closed",)
            )

        fn = getattr(ak, "fx_spot_quote", None)
        if fn is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "当前 akshare 缺少外汇报价接口 fx_spot_quote；可改用 yfinance 数据源取汇率。",
                warnings=("unsupported_api", "fail_closed"),
            )

        try:
            df = call_with_direct_fallback(fn)
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"外汇报价获取失败：{humanize_market_error(exc)}",
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE, "未取得外汇报价。", warnings=("empty_upstream", "fail_closed")
            )

        pair_col = _pick_column(df, ("货币对", "pair", "symbol"))
        bid_col = _pick_column(df, ("买报价", "bid"))
        ask_col = _pick_column(df, ("卖报价", "ask"))
        if pair_col is None or (bid_col is None and ask_col is None):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                "外汇报价字段无法解析。",
                warnings=("parse_error", "fail_closed"),
            )

        wanted = f"{base}/{quote}"
        inverted = f"{quote}/{base}"
        for _, row in df.iterrows():
            label = str(row[pair_col]).strip().upper()
            if label not in {wanted, inverted}:
                continue
            mid = _mid_price(row, bid_col, ask_col)
            if mid is None or mid <= 0:
                continue
            rate = mid if label == wanted else 1.0 / mid
            return ProviderResult.success(
                self.PROVIDER_CODE,
                FxQuote(
                    base_currency=base,
                    quote_currency=quote,
                    rate=rate,
                    as_of=date.today(),
                    provider=self.PROVIDER_CODE,
                    provenance={
                        "source": self.PROVIDER_CODE,
                        "source_api": "fx_spot_quote",
                        "upstream_pair": label,
                        "synthetic": "false",
                    },
                ),
                warnings=("real_market_data", "not_for_investment_decisions"),
            )

        return ProviderResult.failure(
            self.PROVIDER_CODE,
            f"外汇报价中没有 {wanted}。",
            warnings=("pair_not_found", "fail_closed"),
        )

    def _bars_cn_fund(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
    ) -> ProviderResult[list[OhlcvBar]]:
        """Chart-facing view of the NAV series.

        The NAV channel stays the source of truth; open/high/low simply repeat the NAV so the
        existing chart keeps working, and the ``fund_nav_not_ohlcv`` warning says so.
        """
        result = self.nav_series(parsed.value, start, end)
        if not result.ok or not result.data:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                result.error or f"未取得基金净值：{parsed.value}",
                warnings=result.warnings or ("fail_closed",),
            )

        bars = [
            OhlcvBar(
                timestamp_utc=datetime(p.as_of.year, p.as_of.month, p.as_of.day, tzinfo=timezone.utc),
                open=p.nav,
                high=p.nav,
                low=p.nav,
                close=p.nav,
                volume=0.0,
                currency=p.currency,
                adjustment=adjustment,
                provider=self.PROVIDER_CODE,
                amount=None,
                provenance=dict(p.provenance),
            )
            for p in result.data
        ]
        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=tuple(result.warnings) + ("fund_nav_not_ohlcv",),
        )

    def _bars_cn_bond(
        self,
        ak: Any,
        parsed: SymbolId,
        start: date,
        end: date,
        adjustment: Adjustment,
        asset: AssetClass,
    ) -> ProviderResult[list[OhlcvBar]]:
        prefix = "sh" if parsed.exchange == "SSE" else "sz"
        ticker = f"{prefix}{parsed.code}"
        is_convertible = asset is AssetClass.CONVERTIBLE_BOND
        api_name = "bond_zh_hs_cov_daily" if is_convertible else "bond_zh_hs_daily"
        fn = getattr(ak, api_name, None)
        if fn is None:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"当前 akshare 缺少债券接口 {api_name}。",
                warnings=("unsupported_api", "fail_closed"),
            )

        try:
            df = call_with_direct_fallback(lambda: fn(symbol=ticker))
        except Exception as exc:  # noqa: BLE001
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"债券行情获取失败：{parsed.value}（{humanize_market_error(exc)}）",
                warnings=("upstream_error", "fail_closed"),
            )
        if df is None or getattr(df, "empty", True):
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"未从上游取得债券行情：{parsed.value}",
                warnings=("empty_upstream", "fail_closed"),
            )

        bars = _df_to_bars(
            df,
            provider=self.PROVIDER_CODE,
            currency="CNY",
            adjustment=adjustment,
            symbol=parsed.value,
            source_api=api_name,
            date_candidates=("日期", "date"),
        )
        # These APIs return full history, so clip to the requested window.
        bars = [bar for bar in bars if start <= bar.timestamp_utc.date() <= end]
        if not bars:
            return ProviderResult.failure(
                self.PROVIDER_CODE,
                f"债券行情无落在区间内的数据：{parsed.value}",
                warnings=("empty_window", "fail_closed"),
            )

        return ProviderResult.success(
            self.PROVIDER_CODE,
            bars,
            warnings=(
                "real_market_data",
                "not_for_investment_decisions",
                f"source_api:{api_name}",
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


def _financial_periods_from(df: Any, wanted: int) -> list[FinancialPeriod]:
    """Reads the abstract table, which upstream ships either row- or column-per-period."""
    columns = [str(c) for c in df.columns]
    period_columns = [c for c in columns if _looks_like_period(c)]

    if period_columns:
        # Wide layout: one column per reporting period, indicator names down the first column.
        label_col = columns[0]
        rows = {str(row[label_col]).strip(): row for _, row in df.iterrows()}
        periods: list[FinancialPeriod] = []
        for column in sorted(period_columns, reverse=True)[:wanted]:
            periods.append(
                FinancialPeriod(
                    period=column,
                    revenue=_row_value(rows, ("营业总收入", "营业收入"), column),
                    net_profit=_row_value(rows, ("归母净利润", "净利润"), column),
                    eps=_row_value(rows, ("基本每股收益", "每股收益"), column),
                    roe=_row_value(rows, ("净资产收益率", "净资产收益率(ROE)"), column),
                )
            )
        return _with_growth(periods)

    # Long layout: one row per period.
    period_col = _pick_column(df, ("报告期", "报告日", "period", "date"))
    if period_col is None:
        return []

    periods = []
    for _, row in df.iterrows():
        periods.append(
            FinancialPeriod(
                period=str(row[period_col]),
                revenue=_optional_float(row, _pick_column(df, ("营业总收入", "营业收入"))),
                net_profit=_optional_float(row, _pick_column(df, ("归母净利润", "净利润"))),
                eps=_optional_float(row, _pick_column(df, ("基本每股收益", "每股收益"))),
                roe=_optional_float(row, _pick_column(df, ("净资产收益率",))),
            )
        )
    periods.sort(key=lambda p: p.period, reverse=True)
    return _with_growth(periods[:wanted])


def _looks_like_period(name: str) -> bool:
    digits = "".join(ch for ch in name if ch.isdigit())
    return len(digits) == 8 and name.strip() == digits


def _row_value(rows: dict[str, Any], labels: tuple[str, ...], column: str) -> float | None:
    for label in labels:
        row = rows.get(label)
        if row is None:
            continue
        try:
            value = float(row[column])
        except Exception:  # noqa: BLE001
            continue
        return None if value != value else value
    return None


def _with_growth(periods: list[FinancialPeriod]) -> list[FinancialPeriod]:
    """Fills year-on-year growth where consecutive periods allow it."""
    out: list[FinancialPeriod] = []
    for i, period in enumerate(periods):
        previous = periods[i + 1] if i + 1 < len(periods) else None
        out.append(
            FinancialPeriod(
                period=period.period,
                revenue=period.revenue,
                net_profit=period.net_profit,
                eps=period.eps,
                roe=period.roe,
                revenue_growth=_growth(period.revenue, previous.revenue if previous else None),
                profit_growth=_growth(period.net_profit, previous.net_profit if previous else None),
            )
        )
    return out


def _growth(current: float | None, previous: float | None) -> float | None:
    if current is None or previous is None or previous == 0:
        return None
    # A swing from a loss to a profit has no meaningful percentage.
    if previous < 0:
        return None
    return (current - previous) / previous


def _optional_float(row: Any, column: str | None) -> float | None:
    if column is None:
        return None
    try:
        value = float(row[column])
    except Exception:  # noqa: BLE001
        return None
    return None if value != value else value  # drop NaN


def _mid_price(row: Any, bid_col: str | None, ask_col: str | None) -> float | None:
    bid = _optional_float(row, bid_col)
    ask = _optional_float(row, ask_col)
    if bid is not None and ask is not None:
        return (bid + ask) / 2.0
    return bid if bid is not None else ask


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
