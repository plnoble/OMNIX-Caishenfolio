from __future__ import annotations

from caishenfolio_core.data.markets import region_of
from caishenfolio_core.data.models import AssetClass
from caishenfolio_core.market.fixture import SymbolHit

# Common CN funds / ETFs for discovery (not exhaustive full market).
_FUND_SEED: tuple[tuple[str, str, str, AssetClass], ...] = (
    ("SSE:510300", "沪深300ETF", "etf", AssetClass.ETF),
    ("SSE:510500", "中证500ETF", "etf", AssetClass.ETF),
    ("SZSE:159915", "创业板ETF", "etf", AssetClass.ETF),
    ("SZSE:159919", "沪深300ETF易方达", "etf", AssetClass.ETF),
    ("SSE:518880", "黄金ETF", "etf", AssetClass.ETF),
    ("SSE:511010", "国债ETF", "etf", AssetClass.ETF),
    ("FUND:000001", "华夏成长混合", "fund", AssetClass.MUTUAL_FUND),
    ("FUND:110022", "易方达消费行业", "fund", AssetClass.MUTUAL_FUND),
    ("FUND:161725", "招商中证白酒", "fund", AssetClass.MUTUAL_FUND),
    ("FUND:005827", "易方达蓝筹精选", "fund", AssetClass.MUTUAL_FUND),
)


def search_funds(query: str = "", limit: int = 20) -> list[SymbolHit]:
    q = (query or "").strip().lower()
    limit = max(1, min(limit, 50))
    hits: list[SymbolHit] = []
    for symbol, name, kind, asset in _FUND_SEED:
        if not q or q in symbol.lower() or q in name.lower() or q in kind:
            hits.append(
                SymbolHit(
                    symbol,
                    region_of(symbol),
                    asset,
                    name,
                    provider="fund_catalog",
                )
            )
        if len(hits) >= limit:
            break
    return hits
