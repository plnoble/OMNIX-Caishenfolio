from __future__ import annotations

import json
import os
from datetime import date
from typing import Any
from urllib.parse import parse_qs

from caishenfolio_core import PRODUCT_NAME, RESEARCH_DISCLAIMER, __version__
from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import Adjustment
from caishenfolio_core.market.bar_cache import BarsSqliteCache
from caishenfolio_core.market.cached_market import CachingMarketFacade
from caishenfolio_core.market.factory import create_market_provider, provider_status
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.market.network import trust_env_enabled
from caishenfolio_core.market.fund_catalog import search_funds
from caishenfolio_core.market.parquet_store import export_bars_parquet, parquet_available
from caishenfolio_core.market.symbol_index import fuzzy_search_a_share
from caishenfolio_core.research.backtest import cost_model_from_dict, ma_cross_backtest
from caishenfolio_core.research.compare import compare_normalized_closes
from caishenfolio_core.research.grid import grid_backtest, suggest_grid_from_bars
from caishenfolio_core.research.grid_ledger import GridLedgerStore
from caishenfolio_core.research.report import build_markdown_report, write_report
from caishenfolio_core.security.loopback import ensure_loopback, is_denied_wildcard
from caishenfolio_core.tasks.models import TaskKind, TaskStatus
from caishenfolio_core.tasks.store import InMemoryTaskStore


def _cache_enabled() -> bool:
    return (os.environ.get("CAISHENFOLIO_BARS_CACHE") or "1").strip().lower() not in {
        "0",
        "false",
        "no",
        "off",
    }


class AnalyticsApp:
    def __init__(
        self,
        *,
        market: Any | None = None,
        tasks: InMemoryTaskStore | None = None,
        cache: BarsSqliteCache | None = None,
        grid_ledger: GridLedgerStore | None = None,
    ) -> None:
        self.market = market if market is not None else create_market_provider()
        self.tasks = tasks or InMemoryTaskStore()
        self.cache = cache
        if self.cache is None and isinstance(self.market, CachingMarketFacade):
            self.cache = self.market.cache
        elif self.cache is None and _cache_enabled() and not isinstance(self.market, FixtureMarketDataProvider):
            try:
                self.cache = BarsSqliteCache()
            except Exception:  # noqa: BLE001
                self.cache = None
        self.grid_ledger = grid_ledger or GridLedgerStore()

    def health(self) -> dict[str, object]:
        status = provider_status(self.market)
        payload: dict[str, object] = {
            "status": "ok",
            "product": PRODUCT_NAME,
            "version": __version__,
            "phase": "P5",
            "disclaimer": RESEARCH_DISCLAIMER,
            "live_trading_enabled": False,
            "grid_research_enabled": True,
        }
        payload.update(status)
        if self.cache is not None:
            payload["bars_cache"] = self.cache.stats().to_dict()
        return payload

    def market_diagnostics(self) -> dict[str, object]:
        status = provider_status(self.market)
        tips: list[str] = []
        ready = bool(status.get("market_provider_ready"))
        synthetic = bool(status.get("market_data_synthetic"))
        if not ready:
            tips.append("请安装免费源: pip install akshare pandas yfinance")
        tips.append("支持模糊搜索：输入「浦发」可匹配名称。")
        tips.append("K线周期：1/5/15/30/60分钟、日/周/月/季/年。")
        tips.append("本地 bars 缓存默认开启，关注列表可增量同步。")
        if synthetic:
            tips.append("当前为 fixture 合成数据，仅供演示。")
        if not synthetic and trust_env_enabled():
            tips.append("Proxy 问题时设 CAISHENFOLIO_HTTP_TRUST_ENV=0 或关闭系统代理。")
        payload = {
            **status,
            "tips": tips,
            "supported_examples": [
                "SSE:600000",
                "SZSE:000001",
                "HKEX:00700",
                "NASDAQ:AAPL",
            ],
            "intervals": [i.value for i in BarInterval],
        }
        if self.cache is not None:
            payload["bars_cache"] = self.cache.stats().to_dict()
        return payload

    def search_symbols(self, query: str = "", limit: int = 20) -> dict[str, object]:
        limit = max(1, min(int(limit), 50))
        # Local fuzzy first (fast, good for 浦发)
        local = fuzzy_search_a_share(query, limit=limit)
        funds = search_funds(query, limit=limit)
        remote: list[Any] = []
        try:
            remote = self.market.search(query, limit=limit) or []
        except Exception:  # noqa: BLE001
            remote = []
        merged = []
        seen: set[str] = set()
        for hit in list(local) + list(funds) + list(remote):
            sym = hit.symbol if hasattr(hit, "symbol") else str(hit.get("symbol", ""))
            if not sym or sym in seen:
                continue
            seen.add(sym)
            merged.append(hit if hasattr(hit, "to_dict") else hit)
            if len(merged) >= limit:
                break
        items = [h.to_dict() if hasattr(h, "to_dict") else h for h in merged]
        provider = "symbol_index+fund_catalog+upstream"
        return {"items": items, "provider": provider, "fuzzy": True}

    def market_bars(
        self,
        symbol: str,
        start: str,
        end: str,
        adjustment: str = Adjustment.RAW.value,
        interval: str = BarInterval.DAILY.value,
    ) -> dict[str, object]:
        provider = getattr(self.market, "PROVIDER_CODE", "unknown")
        try:
            start_date = date.fromisoformat(start)
            end_date = date.fromisoformat(end)
            adj = Adjustment(adjustment)
            bar_interval = BarInterval.parse(interval)
        except ValueError as exc:
            return {
                "ok": False,
                "provider": provider,
                "data": None,
                "warnings": [],
                "error": str(exc),
                "interval": interval,
            }

        try:
            result = self.market.historical_bars(symbol, start_date, end_date, adj, bar_interval)
        except TypeError:
            result = self.market.historical_bars(symbol, start_date, end_date, adj)
        return {
            "ok": result.ok,
            "provider": result.provider,
            "interval": bar_interval.value,
            "interval_label": bar_interval.label_zh,
            "adjustment": adj.value,
            "from_cache": "served_with_disk_cache" in (result.warnings or ()),
            "data": None
            if result.data is None
            else [
                {
                    "timestamp_utc": bar.timestamp_utc.isoformat(),
                    "open": bar.open,
                    "high": bar.high,
                    "low": bar.low,
                    "close": bar.close,
                    "volume": bar.volume,
                    "amount": bar.amount,
                    "currency": bar.currency,
                    "adjustment": bar.adjustment.value,
                    "provider": bar.provider,
                    "provenance": dict(bar.provenance),
                }
                for bar in result.data
            ],
            "warnings": list(result.warnings),
            "error": result.error,
        }

    def cache_stats(self) -> dict[str, object]:
        if self.cache is None:
            return {"ok": False, "error": "缓存未启用"}
        return {"ok": True, **self.cache.stats().to_dict()}

    def cache_clear(self, symbol: str | None = None) -> dict[str, object]:
        if self.cache is None:
            return {"ok": False, "error": "缓存未启用"}
        self.cache.clear(symbol=symbol or None)
        return {"ok": True, **self.cache.stats().to_dict()}

    def cache_sync(self, symbols: list[str], years: int = 10) -> dict[str, object]:
        if not isinstance(self.market, CachingMarketFacade):
            # wrap ad-hoc
            facade = CachingMarketFacade(self.market, self.cache)
        else:
            facade = self.market
        results = []
        for sym in symbols:
            sym = str(sym).strip()
            if not sym:
                continue
            results.append(facade.sync_symbol(sym, years=years))
        stats = self.cache.stats().to_dict() if self.cache else {}
        return {"ok": True, "items": results, "bars_cache": stats}

    def research_backtest_ma(
        self,
        symbol: str,
        start: str,
        end: str,
        *,
        fast: int = 5,
        slow: int = 20,
        adjustment: str = Adjustment.RAW.value,
        interval: str = BarInterval.DAILY.value,
        costs: dict[str, Any] | None = None,
    ) -> dict[str, object]:
        bars_payload = self.market_bars(symbol, start, end, adjustment, interval)
        if not bars_payload.get("ok") or not bars_payload.get("data"):
            return {
                "ok": False,
                "error": bars_payload.get("error") or "无法加载K线。",
                "bars": bars_payload,
                "disclaimer": RESEARCH_DISCLAIMER,
            }
        result = ma_cross_backtest(
            list(bars_payload["data"]),
            symbol=symbol,
            fast=fast,
            slow=slow,
            costs=cost_model_from_dict(costs),
        )
        payload = result.to_dict()
        payload["bars_provider"] = bars_payload.get("provider")
        payload["interval"] = bars_payload.get("interval")
        return payload

    def research_compare(
        self,
        symbols: list[str],
        start: str,
        end: str,
        *,
        adjustment: str = Adjustment.RAW.value,
        interval: str = BarInterval.DAILY.value,
    ) -> dict[str, object]:
        series: dict[str, list[dict[str, Any]]] = {}
        errors: list[str] = []
        for sym in symbols:
            sym = str(sym).strip()
            if not sym:
                continue
            bars_payload = self.market_bars(sym, start, end, adjustment, interval)
            if bars_payload.get("ok") and bars_payload.get("data"):
                series[sym] = list(bars_payload["data"])
            else:
                errors.append(f"{sym}: {bars_payload.get('error') or 'no data'}")
        cmp = compare_normalized_closes(series)
        if errors:
            cmp["fetch_errors"] = errors
        return cmp

    def research_grid_suggest(
        self,
        symbol: str,
        start: str,
        end: str,
        *,
        adjustment: str = Adjustment.RAW.value,
        interval: str = BarInterval.DAILY.value,
        lookback: int | None = None,
        grid_count: int | None = None,
        order_cash: float = 1000.0,
    ) -> dict[str, object]:
        bars_payload = self.market_bars(symbol, start, end, adjustment, interval)
        if not bars_payload.get("ok") or not bars_payload.get("data"):
            return {
                "ok": False,
                "error": bars_payload.get("error") or "无法加载K线。",
                "bars": bars_payload,
                "disclaimer": RESEARCH_DISCLAIMER,
            }
        result = suggest_grid_from_bars(
            list(bars_payload["data"]),
            symbol=symbol,
            lookback=lookback,
            grid_count=grid_count,
            order_cash=float(order_cash),
        )
        result["bars_provider"] = bars_payload.get("provider")
        result["interval"] = bars_payload.get("interval")
        return result

    def research_grid_backtest(
        self,
        symbol: str,
        start: str,
        end: str,
        *,
        lower: float,
        upper: float,
        grid_count: int,
        order_cash: float = 1000.0,
        initial_cash: float | None = None,
        adjustment: str = Adjustment.RAW.value,
        interval: str = BarInterval.DAILY.value,
        costs: dict[str, Any] | None = None,
    ) -> dict[str, object]:
        bars_payload = self.market_bars(symbol, start, end, adjustment, interval)
        if not bars_payload.get("ok") or not bars_payload.get("data"):
            return {
                "ok": False,
                "error": bars_payload.get("error") or "无法加载K线。",
                "bars": bars_payload,
                "disclaimer": RESEARCH_DISCLAIMER,
            }
        result = grid_backtest(
            list(bars_payload["data"]),
            symbol=symbol,
            lower=float(lower),
            upper=float(upper),
            grid_count=int(grid_count),
            order_cash=float(order_cash),
            costs=costs,
            initial_cash=initial_cash,
        )
        result["bars_provider"] = bars_payload.get("provider")
        result["interval"] = bars_payload.get("interval")
        return result

    def research_export_report(
        self,
        *,
        artifact_root: str,
        title: str,
        symbol: str | None,
        sections: list[dict[str, Any]],
        filename: str | None = None,
    ) -> dict[str, object]:
        if not artifact_root or not str(artifact_root).strip():
            return {"ok": False, "error": "artifact_root 必填（Host Artifact 路径）。"}
        md = build_markdown_report(
            title=title or "研究报告",
            symbol=symbol,
            sections=sections,
            product=PRODUCT_NAME,
        )
        written = write_report(md, artifact_root, filename=filename)
        # also map to task artifact record
        task = self.tasks.create_task(
            TaskKind.REPORT,
            title or "研究报告",
            metadata={"symbol": symbol or "", "path": str(written.get("markdown_path") or "")},
        )
        self.tasks.update_status(task.id, TaskStatus.RUNNING)
        art = self.tasks.add_artifact(
            task.id,
            kind="report_markdown",
            title=title or "报告",
            uri_or_payload=str(written.get("markdown_path") or ""),
            content_type="text/markdown",
        )
        self.tasks.update_status(task.id, TaskStatus.SUCCEEDED, "报告已写入 Artifact 根。")
        return {
            **written,
            "task": task.to_dict(),
            "artifact": art.to_dict(),
            "disclaimer": RESEARCH_DISCLAIMER,
        }

    def export_parquet(
        self,
        symbol: str,
        start: str,
        end: str,
        *,
        adjustment: str = Adjustment.RAW.value,
        interval: str = BarInterval.DAILY.value,
    ) -> dict[str, object]:
        bars_payload = self.market_bars(symbol, start, end, adjustment, interval)
        if not bars_payload.get("ok") or not bars_payload.get("data"):
            return {
                "ok": False,
                "error": bars_payload.get("error") or "无K线可导出",
                "parquet_available": parquet_available(),
            }
        result = export_bars_parquet(
            list(bars_payload["data"]),
            symbol=symbol,
            interval=str(bars_payload.get("interval") or interval),
            adjustment=adjustment,
        )
        result["parquet_available"] = parquet_available()
        return result

    def list_tasks(self, kind: str | None = None, status: str | None = None, limit: int = 50) -> dict[str, object]:
        kind_enum = TaskKind(kind) if kind else None
        status_enum = TaskStatus(status) if status else None
        items = self.tasks.list_tasks(kind=kind_enum, status=status_enum, limit=limit)
        return {"items": [item.to_dict() for item in items]}

    def create_task(self, kind: str, title: str, metadata: dict[str, str] | None = None) -> dict[str, object]:
        task = self.tasks.create_task(TaskKind(kind), title, metadata=metadata)
        return task.to_dict()

    def list_audit(self, task_id: str, event_type: str | None = None, limit: int = 50) -> dict[str, object]:
        items = self.tasks.list_audit(task_id, event_type=event_type, limit=limit)
        return {"items": [item.to_dict() for item in items]}

    def get_task(self, task_id: str) -> dict[str, object] | None:
        task = self.tasks.get_task(task_id)
        return None if task is None else task.to_dict()

    def list_artifacts(self, task_id: str) -> dict[str, object]:
        task = self.tasks.get_task(task_id)
        if task is None:
            return {"error": f"未知任务 '{task_id}'。", "items": []}
        items = []
        for artifact_id in task.artifact_ids:
            artifact = self.tasks.get_artifact(artifact_id)
            if artifact is not None:
                items.append(artifact.to_dict())
        return {"items": items}

    def research_symbol_snapshot(
        self,
        symbol: str,
        start: str,
        end: str,
        adjustment: str = Adjustment.RAW.value,
    ) -> dict[str, object]:
        provider = getattr(self.market, "PROVIDER_CODE", "unknown")
        title = f"标的快照 {symbol}".strip()
        task = self.tasks.create_task(
            TaskKind.RESEARCH,
            title,
            metadata={
                "symbol": symbol,
                "start": start,
                "end": end,
                "adjustment": adjustment,
                "command": "symbol_snapshot",
                "provider": str(provider),
            },
        )
        self.tasks.update_status(task.id, TaskStatus.RUNNING, "正在加载行情…")
        bars_payload = self.market_bars(symbol, start, end, adjustment)
        if not bars_payload.get("ok"):
            failed = self.tasks.update_status(
                task.id,
                TaskStatus.FAILED,
                str(bars_payload.get("error") or "行情获取失败。"),
            )
            return {
                "ok": False,
                "task": failed.to_dict(),
                "artifact": None,
                "bars": bars_payload,
                "disclaimer": RESEARCH_DISCLAIMER,
                "error": bars_payload.get("error"),
            }

        bars = bars_payload.get("data") or []
        closes = [float(bar["close"]) for bar in bars if bar.get("close") is not None]
        summary = {
            "symbol": symbol,
            "start": start,
            "end": end,
            "adjustment": adjustment,
            "provider": bars_payload.get("provider"),
            "bar_count": len(bars),
            "first_close": closes[0] if closes else None,
            "last_close": closes[-1] if closes else None,
            "min_close": min(closes) if closes else None,
            "max_close": max(closes) if closes else None,
            "warnings": list(bars_payload.get("warnings") or []),
            "disclaimer": RESEARCH_DISCLAIMER,
            "synthetic": bars_payload.get("provider") == FixtureMarketDataProvider.PROVIDER_CODE,
        }
        artifact = self.tasks.add_artifact(
            task.id,
            kind="research_snapshot",
            title=f"{symbol} 行情快照",
            uri_or_payload=json.dumps(summary, ensure_ascii=False),
            content_type="application/json",
        )
        done = self.tasks.update_status(
            task.id,
            TaskStatus.SUCCEEDED,
            f"{len(bars)} 根K线；最新收盘={summary['last_close']}；数据源={bars_payload.get('provider')}",
        )
        audits = self.tasks.list_audit(task.id)
        return {
            "ok": True,
            "task": done.to_dict(),
            "artifact": artifact.to_dict(),
            "summary": summary,
            "audit": [item.to_dict() for item in audits],
            "disclaimer": RESEARCH_DISCLAIMER,
            "error": None,
        }


def health_payload() -> dict[str, object]:
    return AnalyticsApp(market=FixtureMarketDataProvider(), cache=None).health() | {"phase": "P4"}


def validate_bind_host(host: str) -> str:
    if is_denied_wildcard(host):
        raise ValueError(f"禁止绑定通配/非回环地址 '{host}'。")
    ensure_loopback(host)
    return host


def dispatch(app: AnalyticsApp, method: str, path: str, query: str = "", body: dict[str, Any] | None = None) -> tuple[int, dict[str, object]]:
    body = body or {}
    params = {key: values[-1] for key, values in parse_qs(query, keep_blank_values=True).items()}
    normalized = path.rstrip("/") or "/"

    if method == "GET" and normalized == "/health":
        return 200, app.health()
    if method == "GET" and normalized == "/market/diagnostics":
        return 200, app.market_diagnostics()
    if method == "GET" and normalized == "/market/cache":
        return 200, app.cache_stats()
    if method == "POST" and normalized == "/market/cache/clear":
        return 200, app.cache_clear(str(body.get("symbol") or "") or None)
    if method == "POST" and normalized == "/market/cache/sync":
        symbols = body.get("symbols") or []
        if isinstance(symbols, str):
            symbols = [symbols]
        years = int(body.get("years", 10))
        return 200, app.cache_sync([str(s) for s in symbols], years=years)
    if method == "GET" and normalized == "/symbols/search":
        return 200, app.search_symbols(params.get("q", ""), int(params.get("limit", "20")))
    if method == "GET" and normalized == "/market/bars":
        if "symbol" not in params or "start" not in params or "end" not in params:
            return 400, {"error": "必须提供 symbol、start、end。"}
        return 200, app.market_bars(
            params["symbol"],
            params["start"],
            params["end"],
            params.get("adjustment", Adjustment.RAW.value),
            params.get("interval", BarInterval.DAILY.value),
        )
    if method == "GET" and normalized == "/tasks":
        return 200, app.list_tasks(params.get("kind"), params.get("status"), int(params.get("limit", "50")))
    if method == "POST" and normalized == "/tasks":
        try:
            return 200, app.create_task(str(body.get("kind", "system")), str(body.get("title", "")), body.get("metadata"))
        except (ValueError, KeyError) as exc:
            return 400, {"error": str(exc)}
    if method == "GET" and normalized.startswith("/tasks/") and normalized.endswith("/audit"):
        task_id = normalized[len("/tasks/") : -len("/audit")]
        return 200, app.list_audit(task_id, params.get("event_type"), int(params.get("limit", "50")))
    if method == "GET" and normalized.startswith("/tasks/") and normalized.endswith("/artifacts"):
        task_id = normalized[len("/tasks/") : -len("/artifacts")]
        payload = app.list_artifacts(task_id)
        if "error" in payload and not payload.get("items"):
            return 404, payload
        return 200, payload
    if method == "GET" and normalized.startswith("/tasks/") and normalized.count("/") == 2:
        task_id = normalized[len("/tasks/") :]
        task = app.get_task(task_id)
        if task is None:
            return 404, {"error": f"未知任务 '{task_id}'。"}
        return 200, task
    if method == "POST" and normalized == "/research/symbol-snapshot":
        symbol = str(body.get("symbol", "")).strip()
        start = str(body.get("start", "")).strip()
        end = str(body.get("end", "")).strip()
        adjustment = str(body.get("adjustment", Adjustment.RAW.value))
        if not symbol or not start or not end:
            return 400, {"error": "必须提供 symbol、start、end。"}
        result = app.research_symbol_snapshot(symbol, start, end, adjustment)
        return (200 if result.get("ok") else 422), result
    if method == "POST" and normalized == "/research/backtest-ma":
        symbol = str(body.get("symbol", "")).strip()
        start = str(body.get("start", "")).strip()
        end = str(body.get("end", "")).strip()
        if not symbol or not start or not end:
            return 400, {"error": "必须提供 symbol、start、end。"}
        costs = body.get("costs") if isinstance(body.get("costs"), dict) else None
        result = app.research_backtest_ma(
            symbol,
            start,
            end,
            fast=int(body.get("fast", 5)),
            slow=int(body.get("slow", 20)),
            adjustment=str(body.get("adjustment", Adjustment.RAW.value)),
            interval=str(body.get("interval", BarInterval.DAILY.value)),
            costs=costs,
        )
        return (200 if result.get("ok") else 422), result
    if method == "POST" and normalized == "/research/compare":
        symbols = body.get("symbols") or []
        if isinstance(symbols, str):
            symbols = [s.strip() for s in symbols.split(",") if s.strip()]
        start = str(body.get("start", "")).strip()
        end = str(body.get("end", "")).strip()
        if not symbols or not start or not end:
            return 400, {"error": "必须提供 symbols、start、end。"}
        result = app.research_compare(
            [str(s) for s in symbols],
            start,
            end,
            adjustment=str(body.get("adjustment", Adjustment.RAW.value)),
            interval=str(body.get("interval", BarInterval.DAILY.value)),
        )
        return (200 if result.get("ok") else 422), result
    if method == "POST" and normalized == "/research/export-report":
        result = app.research_export_report(
            artifact_root=str(body.get("artifact_root", "")),
            title=str(body.get("title", "研究报告")),
            symbol=body.get("symbol"),
            sections=list(body.get("sections") or []),
            filename=body.get("filename"),
        )
        return (200 if result.get("ok") else 400), result
    if method == "POST" and normalized == "/research/grid-suggest":
        symbol = str(body.get("symbol", "")).strip()
        start = str(body.get("start", "")).strip()
        end = str(body.get("end", "")).strip()
        if not symbol or not start or not end:
            return 400, {"error": "必须提供 symbol、start、end。"}
        gc = body.get("grid_count")
        lb = body.get("lookback")
        result = app.research_grid_suggest(
            symbol,
            start,
            end,
            adjustment=str(body.get("adjustment", Adjustment.RAW.value)),
            interval=str(body.get("interval", BarInterval.DAILY.value)),
            lookback=int(lb) if lb is not None and str(lb).strip() != "" else None,
            grid_count=int(gc) if gc is not None and str(gc).strip() != "" else None,
            order_cash=float(body.get("order_cash", 1000.0)),
        )
        return (200 if result.get("ok") else 422), result
    if method == "POST" and normalized == "/research/grid-backtest":
        symbol = str(body.get("symbol", "")).strip()
        start = str(body.get("start", "")).strip()
        end = str(body.get("end", "")).strip()
        if not symbol or not start or not end:
            return 400, {"error": "必须提供 symbol、start、end。"}
        try:
            lower = float(body["lower"])
            upper = float(body["upper"])
            grid_count = int(body["grid_count"])
        except (KeyError, TypeError, ValueError):
            return 400, {"error": "必须提供 lower、upper、grid_count。"}
        costs = body.get("costs") if isinstance(body.get("costs"), dict) else None
        init_cash = body.get("initial_cash")
        result = app.research_grid_backtest(
            symbol,
            start,
            end,
            lower=lower,
            upper=upper,
            grid_count=grid_count,
            order_cash=float(body.get("order_cash", 1000.0)),
            initial_cash=float(init_cash) if init_cash is not None and str(init_cash).strip() != "" else None,
            adjustment=str(body.get("adjustment", Adjustment.RAW.value)),
            interval=str(body.get("interval", BarInterval.DAILY.value)),
            costs=costs,
        )
        return (200 if result.get("ok") else 422), result
    if method == "GET" and normalized == "/research/grid/plans":
        active_only = (params.get("active_only") or "1").strip().lower() not in {"0", "false", "no"}
        return 200, {"ok": True, "items": app.grid_ledger.list_plans(active_only=active_only)}
    if method == "POST" and normalized == "/research/grid/plans":
        try:
            plan = app.grid_ledger.create_plan(
                symbol=str(body.get("symbol", "")).strip(),
                lower=float(body["lower"]),
                upper=float(body["upper"]),
                grid_count=int(body["grid_count"]),
                order_cash=float(body.get("order_cash", 1000.0)),
                name=str(body.get("name") or ""),
                note=str(body.get("note") or ""),
            )
            return 200, {"ok": True, "plan": plan}
        except (KeyError, TypeError, ValueError) as exc:
            return 400, {"ok": False, "error": str(exc)}
    if method == "POST" and normalized == "/research/grid/fills":
        try:
            result = app.grid_ledger.add_fill(
                plan_id=str(body.get("plan_id", "")).strip(),
                side=str(body.get("side", "")),
                price=float(body["price"]),
                qty=float(body["qty"]),
                fee=float(body.get("fee") or 0),
                grid_level=float(body["grid_level"]) if body.get("grid_level") is not None else None,
                ts=str(body["ts"]) if body.get("ts") else None,
                note=str(body.get("note") or ""),
            )
            return (200 if result.get("ok") else 400), result
        except (KeyError, TypeError, ValueError) as exc:
            return 400, {"ok": False, "error": str(exc)}
    if method == "GET" and normalized.startswith("/research/grid/plans/") and normalized.endswith("/snapshot"):
        plan_id = normalized[len("/research/grid/plans/") : -len("/snapshot")]
        last_price = params.get("last_price")
        lp = float(last_price) if last_price not in (None, "") else None
        result = app.grid_ledger.snapshot(plan_id, last_price=lp)
        return (200 if result.get("ok") else 404), result
    if method == "POST" and normalized.startswith("/research/grid/plans/") and normalized.endswith("/deactivate"):
        plan_id = normalized[len("/research/grid/plans/") : -len("/deactivate")]
        return 200, app.grid_ledger.deactivate_plan(plan_id)
    if method == "POST" and normalized == "/market/export-parquet":
        symbol = str(body.get("symbol", "")).strip()
        start = str(body.get("start", "")).strip()
        end = str(body.get("end", "")).strip()
        if not symbol or not start or not end:
            return 400, {"error": "必须提供 symbol、start、end。"}
        result = app.export_parquet(
            symbol,
            start,
            end,
            adjustment=str(body.get("adjustment", Adjustment.RAW.value)),
            interval=str(body.get("interval", BarInterval.DAILY.value)),
        )
        return (200 if result.get("ok") else 422), result

    return 404, {"error": f"未知路由 {method} {normalized}"}
