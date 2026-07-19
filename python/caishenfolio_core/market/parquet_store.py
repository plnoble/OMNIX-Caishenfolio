from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any


def parquet_available() -> bool:
    try:
        import pyarrow  # noqa: F401
        import pyarrow.parquet  # noqa: F401

        return True
    except Exception:  # noqa: BLE001
        return False


def default_parquet_root() -> Path:
    env = (os.environ.get("CAISHENFOLIO_PARQUET_ROOT") or "").strip()
    if env:
        return Path(env)
    cache = (os.environ.get("CAISHENFOLIO_BARS_CACHE_PATH") or "").strip()
    if cache:
        return Path(cache).parent / "parquet"
    base = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    root = base / "Caishenfolio" / "state" / "parquet"
    root.mkdir(parents=True, exist_ok=True)
    return root


def export_bars_parquet(
    bars: list[dict[str, Any]],
    *,
    symbol: str,
    interval: str,
    adjustment: str,
    root: Path | None = None,
) -> dict[str, object]:
    if not bars:
        return {"ok": False, "error": "无K线可导出。"}
    if not parquet_available():
        # fallback JSON lines for environments without pyarrow
        out_root = root or default_parquet_root()
        out_root.mkdir(parents=True, exist_ok=True)
        safe = symbol.replace(":", "_")
        path = out_root / f"{safe}__{interval}__{adjustment}.jsonl"
        with path.open("w", encoding="utf-8") as f:
            for b in bars:
                f.write(json.dumps(b, ensure_ascii=False) + "\n")
        return {
            "ok": True,
            "format": "jsonl",
            "path": str(path),
            "rows": len(bars),
            "warning": "pyarrow 未安装，已导出 JSONL 代替 Parquet。pip install pyarrow 可启用 parquet。",
        }

    import pyarrow as pa
    import pyarrow.parquet as pq

    out_root = root or default_parquet_root()
    out_root.mkdir(parents=True, exist_ok=True)
    safe = symbol.replace(":", "_")
    path = out_root / f"{safe}__{interval}__{adjustment}.parquet"

    cols = {
        "timestamp_utc": [str(b.get("timestamp_utc")) for b in bars],
        "open": [float(b["open"]) for b in bars],
        "high": [float(b["high"]) for b in bars],
        "low": [float(b["low"]) for b in bars],
        "close": [float(b["close"]) for b in bars],
        "volume": [float(b.get("volume") or 0) for b in bars],
        "symbol": [symbol] * len(bars),
        "interval": [interval] * len(bars),
        "adjustment": [adjustment] * len(bars),
    }
    table = pa.table(cols)
    pq.write_table(table, path)
    return {
        "ok": True,
        "format": "parquet",
        "path": str(path),
        "rows": len(bars),
    }
