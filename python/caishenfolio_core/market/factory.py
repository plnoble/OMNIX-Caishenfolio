from __future__ import annotations

import os
from typing import Any

from caishenfolio_core.market.akshare_provider import AkshareMarketDataProvider
from caishenfolio_core.market.alphavantage_provider import AlphaVantageMarketDataProvider
from caishenfolio_core.market.cached_market import CachingMarketFacade
from caishenfolio_core.market.catalog import PROVIDER_CATALOG
from caishenfolio_core.market.composite import CompositeMarketDataProvider
from caishenfolio_core.market.credentials import public_secret_status
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.market.network import trust_env_enabled
from caishenfolio_core.market.tushare_provider import TushareMarketDataProvider
from caishenfolio_core.market.yfinance_provider import YFinanceMarketDataProvider


def create_market_provider(name: str | None = None, *, use_cache: bool | None = None) -> Any:
    """Create market provider. Default: auto composite + disk cache."""
    selected = (name or os.environ.get("CAISHENFOLIO_MARKET_PROVIDER") or "auto").strip().lower()
    if use_cache is None:
        use_cache = (os.environ.get("CAISHENFOLIO_BARS_CACHE") or "1").strip().lower() not in {
            "0",
            "false",
            "no",
            "off",
        }

    if selected in {"fixture", "synthetic", "demo"}:
        inner: Any = FixtureMarketDataProvider()
    elif selected in {"akshare"}:
        inner = AkshareMarketDataProvider()
    elif selected in {"yfinance", "yahoo"}:
        inner = YFinanceMarketDataProvider()
    elif selected in {"tushare"}:
        inner = TushareMarketDataProvider()
    elif selected in {"alphavantage", "alpha_vantage", "av"}:
        inner = AlphaVantageMarketDataProvider()
    elif selected in {"auto", "real", "live_data", "composite"}:
        inner = CompositeMarketDataProvider(_real_providers_in_priority())
    else:
        raise ValueError(
            f"未知行情数据源 '{selected}'。支持: auto, akshare, yfinance, tushare, alphavantage, fixture。"
        )

    if use_cache and selected not in {"fixture", "synthetic", "demo"}:
        return CachingMarketFacade(inner)
    return inner


def _real_providers_in_priority() -> list[Any]:
    # Order matters: free broad CN first, then free global, then key-based.
    return [
        AkshareMarketDataProvider(),
        YFinanceMarketDataProvider(),
        TushareMarketDataProvider(),
        AlphaVantageMarketDataProvider(),
    ]


def _child_status(provider: Any) -> dict[str, object]:
    code = getattr(provider, "PROVIDER_CODE", type(provider).__name__)
    ready = True
    if hasattr(provider, "ready"):
        ready = bool(provider.ready)
    return {
        "id": code,
        "ready": ready,
        "synthetic": code == FixtureMarketDataProvider.PROVIDER_CODE,
    }


def provider_status(provider: Any) -> dict[str, object]:
    code = getattr(provider, "PROVIDER_CODE", type(provider).__name__)
    ready = True
    if hasattr(provider, "ready"):
        ready = bool(provider.ready)
    synthetic = code == FixtureMarketDataProvider.PROVIDER_CODE

    children: list[dict[str, object]] = []
    if isinstance(provider, CompositeMarketDataProvider):
        children = [_child_status(child) for child in provider.children]
    else:
        children = [_child_status(provider)]

    catalog = []
    for item in PROVIDER_CATALOG:
        entry = dict(item)
        runtime = next((c for c in children if c["id"] == item["id"]), None)
        if item["id"] == "auto" and code == "auto":
            entry["runtime_ready"] = ready
        elif runtime is not None:
            entry["runtime_ready"] = runtime["ready"]
        else:
            entry["runtime_ready"] = False if item.get("implemented") else None
        catalog.append(entry)

    status: dict[str, object] = {
        "market_provider": code,
        "market_provider_ready": ready,
        "market_data_synthetic": synthetic,
        "http_trust_env": trust_env_enabled(),
        "providers": children,
        "catalog": catalog,
    }
    status.update(public_secret_status())
    return status
