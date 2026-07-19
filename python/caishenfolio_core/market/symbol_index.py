from __future__ import annotations

import json
import os
from concurrent.futures import ThreadPoolExecutor, TimeoutError as FuturesTimeout
from functools import lru_cache
from pathlib import Path

from caishenfolio_core.data.models import AssetClass, Market
from caishenfolio_core.market.fixture import SymbolHit

# Offline seed — always works for common names (e.g. 平安).
_SEED: tuple[tuple[str, str, str], ...] = (
    ("000001", "平安银行", "SZSE"),
    ("600000", "浦发银行", "SSE"),
    ("600519", "贵州茅台", "SSE"),
    ("000858", "五粮液", "SZSE"),
    ("300750", "宁德时代", "SZSE"),
    ("601318", "中国平安", "SSE"),
    ("601012", "隆基绿能", "SSE"),
    ("000002", "万科A", "SZSE"),
    ("600036", "招商银行", "SSE"),
    ("601166", "兴业银行", "SSE"),
    ("000333", "美的集团", "SZSE"),
    ("002594", "比亚迪", "SZSE"),
    ("300059", "东方财富", "SZSE"),
    ("510300", "沪深300ETF", "SSE"),
    ("159915", "创业板ETF", "SZSE"),
    ("159919", "沪深300ETF", "SZSE"),
    ("518880", "黄金ETF", "SSE"),
)


def _index_cache_path() -> Path:
    env = (os.environ.get("CAISHENFOLIO_SYMBOL_INDEX_PATH") or "").strip()
    if env:
        return Path(env)
    cache = (os.environ.get("CAISHENFOLIO_BARS_CACHE_PATH") or "").strip()
    if cache:
        return Path(cache).with_name("symbol_name_index.json")
    base = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    root = base / "Caishenfolio" / "state"
    root.mkdir(parents=True, exist_ok=True)
    return root / "symbol_name_index.json"


def _pick_col(columns: list[str], *candidates: str) -> str | None:
    lower = {str(c).lower(): c for c in columns}
    for name in candidates:
        if name in columns:
            return name
        if name.lower() in lower:
            return lower[name.lower()]
    return None


def _load_from_disk() -> list[tuple[str, str, str]]:
    path = _index_cache_path()
    if not path.is_file():
        return []
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(raw, list):
            return []
        out: list[tuple[str, str, str]] = []
        for item in raw:
            if not isinstance(item, (list, tuple)) or len(item) < 3:
                continue
            out.append((str(item[0]).zfill(6), str(item[1]), str(item[2])))
        return out
    except Exception:  # noqa: BLE001
        return []


def _save_to_disk(rows: list[tuple[str, str, str]]) -> None:
    path = _index_cache_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")
    except Exception:  # noqa: BLE001
        pass


def _load_from_akshare() -> list[tuple[str, str, str]]:
    try:
        import akshare as ak  # type: ignore
    except Exception:  # noqa: BLE001
        return []
    try:
        df = ak.stock_info_a_code_name()
    except Exception:  # noqa: BLE001
        return []
    if df is None or getattr(df, "empty", True):
        return []
    cols = [str(c) for c in df.columns]
    code_col = _pick_col(cols, "code", "代码", "symbol", "证券代码") or cols[0]
    name_col = _pick_col(cols, "name", "名称", "证券简称", "股票简称") or (
        cols[1] if len(cols) > 1 else cols[0]
    )
    out: list[tuple[str, str, str]] = []
    for _, row in df.iterrows():
        code = str(row[code_col]).strip()
        digits = "".join(ch for ch in code if ch.isdigit())
        if len(digits) >= 6:
            code = digits[-6:]
        elif digits:
            code = digits.zfill(6)
        else:
            continue
        name = str(row[name_col]).strip()
        if not name or name == "nan":
            continue
        exchange = "SSE" if code.startswith(("5", "6", "9")) else "SZSE"
        out.append((code, name, exchange))
    if out:
        _save_to_disk(out)
    return out


def _load_akshare_with_timeout(seconds: float = 6.0) -> list[tuple[str, str, str]]:
    if (os.environ.get("CAISHENFOLIO_SKIP_SYMBOL_INDEX_NETWORK") or "").strip() in {
        "1",
        "true",
        "yes",
    }:
        return []
    # Offline test mode
    if (os.environ.get("CAISHENFOLIO_MARKET_PROVIDER") or "").strip().lower() == "fixture":
        return []
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            fut = pool.submit(_load_from_akshare)
            return fut.result(timeout=seconds)
    except (FuturesTimeout, Exception):  # noqa: BLE001
        return []


@lru_cache(maxsize=1)
def load_a_share_name_index() -> list[tuple[str, str, str]]:
    """Prefer disk/full remote; always fall back to seed so 平安 never returns empty."""
    remote = _load_akshare_with_timeout(6.0)
    if remote:
        return remote
    disk = _load_from_disk()
    if disk:
        # merge seed names not in disk
        seen = {c for c, _, _ in disk}
        extra = [s for s in _SEED if s[0] not in seen]
        return list(disk) + extra
    return list(_SEED)


def fuzzy_search_a_share(query: str, limit: int = 20) -> list[SymbolHit]:
    """Substring fuzzy match on code/name (e.g. 平安 → 平安银行 / 中国平安)."""
    q = (query or "").strip()
    if not q:
        return []
    q_lower = q.lower()
    limit = max(1, min(limit, 50))

    starts: list[SymbolHit] = []
    contains: list[SymbolHit] = []
    code_hits: list[SymbolHit] = []

    for code, name, exchange in load_a_share_name_index():
        name_l = name.lower()
        code_l = code.lower()
        asset = (
            AssetClass.ETF
            if code.startswith(("51", "15", "56", "58", "16"))
            else AssetClass.EQUITY
        )
        hit = SymbolHit(
            f"{exchange}:{code}",
            Market.ASHARE,
            asset,
            name,
            provider="symbol_index",
        )
        if name.startswith(q) or name_l.startswith(q_lower):
            starts.append(hit)
        elif q in name or q_lower in name_l:
            contains.append(hit)
        elif q in code or q_lower in code_l:
            code_hits.append(hit)

    merged: list[SymbolHit] = []
    seen: set[str] = set()
    for bucket in (starts, contains, code_hits):
        for item in bucket:
            if item.symbol in seen:
                continue
            seen.add(item.symbol)
            merged.append(item)
            if len(merged) >= limit:
                return merged
    return merged
