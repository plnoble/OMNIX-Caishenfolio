from __future__ import annotations

import json
import os
import sqlite3
from dataclasses import dataclass
from datetime import date, datetime, timezone
from pathlib import Path
from threading import Lock
from typing import Iterable

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import Adjustment, OhlcvBar


def default_cache_path() -> Path:
    env = (os.environ.get("CAISHENFOLIO_BARS_CACHE_PATH") or "").strip()
    if env:
        return Path(env)
    base = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    root = base / "Caishenfolio" / "state"
    root.mkdir(parents=True, exist_ok=True)
    return root / "bars_cache.db"


def default_max_bytes() -> int:
    raw = (os.environ.get("CAISHENFOLIO_BARS_CACHE_MAX_MB") or "512").strip()
    try:
        mb = max(64, int(raw))
    except ValueError:
        mb = 512
    return mb * 1024 * 1024


@dataclass
class CacheStats:
    path: str
    size_bytes: int
    max_bytes: int
    bar_rows: int
    symbols: int

    def to_dict(self) -> dict[str, object]:
        return {
            "path": self.path,
            "size_bytes": self.size_bytes,
            "max_bytes": self.max_bytes,
            "size_mb": round(self.size_bytes / (1024 * 1024), 2),
            "max_mb": round(self.max_bytes / (1024 * 1024), 2),
            "bar_rows": self.bar_rows,
            "symbols": self.symbols,
            "usage_ratio": round(self.size_bytes / self.max_bytes, 4) if self.max_bytes else 0,
        }


class BarsSqliteCache:
    """Disk-backed OHLCV cache. Memory holds nothing beyond query results."""

    def __init__(self, path: Path | None = None, max_bytes: int | None = None) -> None:
        self.path = path or default_cache_path()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.max_bytes = max_bytes if max_bytes is not None else default_max_bytes()
        self._lock = Lock()
        self._init()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(str(self.path), timeout=30)
        conn.row_factory = sqlite3.Row
        return conn

    def _init(self) -> None:
        with self._lock:
            with self._connect() as conn:
                conn.executescript(
                    """
                    CREATE TABLE IF NOT EXISTS bars (
                        symbol TEXT NOT NULL,
                        interval TEXT NOT NULL,
                        adjustment TEXT NOT NULL,
                        ts TEXT NOT NULL,
                        open REAL NOT NULL,
                        high REAL NOT NULL,
                        low REAL NOT NULL,
                        close REAL NOT NULL,
                        volume REAL NOT NULL,
                        amount REAL,
                        currency TEXT,
                        provider TEXT,
                        provenance_json TEXT,
                        PRIMARY KEY (symbol, interval, adjustment, ts)
                    );
                    CREATE INDEX IF NOT EXISTS ix_bars_symbol_interval
                        ON bars(symbol, interval, adjustment, ts);
                    CREATE TABLE IF NOT EXISTS meta (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    """
                )

    def get_bars(
        self,
        symbol: str,
        interval: BarInterval,
        adjustment: Adjustment,
        start: date,
        end: date,
    ) -> list[OhlcvBar]:
        start_s = datetime(start.year, start.month, start.day, tzinfo=timezone.utc).isoformat()
        end_s = datetime(end.year, end.month, end.day, 23, 59, 59, tzinfo=timezone.utc).isoformat()
        with self._lock:
            with self._connect() as conn:
                rows = conn.execute(
                    """
                    SELECT * FROM bars
                    WHERE symbol = ? AND interval = ? AND adjustment = ?
                      AND ts >= ? AND ts <= ?
                    ORDER BY ts ASC
                    """,
                    (symbol, interval.value, adjustment.value, start_s, end_s),
                ).fetchall()
        return [_row_to_bar(r) for r in rows]

    def max_date(
        self,
        symbol: str,
        interval: BarInterval,
        adjustment: Adjustment,
    ) -> date | None:
        with self._lock:
            with self._connect() as conn:
                row = conn.execute(
                    """
                    SELECT MAX(ts) AS m FROM bars
                    WHERE symbol = ? AND interval = ? AND adjustment = ?
                    """,
                    (symbol, interval.value, adjustment.value),
                ).fetchone()
        if not row or not row["m"]:
            return None
        try:
            return datetime.fromisoformat(str(row["m"])).date()
        except ValueError:
            return None

    def upsert_bars(
        self,
        symbol: str,
        interval: BarInterval,
        adjustment: Adjustment,
        bars: Iterable[OhlcvBar],
    ) -> int:
        items = list(bars)
        if not items:
            return 0
        with self._lock:
            with self._connect() as conn:
                conn.executemany(
                    """
                    INSERT INTO bars (
                        symbol, interval, adjustment, ts,
                        open, high, low, close, volume, amount,
                        currency, provider, provenance_json
                    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)
                    ON CONFLICT(symbol, interval, adjustment, ts) DO UPDATE SET
                        open=excluded.open,
                        high=excluded.high,
                        low=excluded.low,
                        close=excluded.close,
                        volume=excluded.volume,
                        amount=excluded.amount,
                        currency=excluded.currency,
                        provider=excluded.provider,
                        provenance_json=excluded.provenance_json
                    """,
                    [
                        (
                            symbol,
                            interval.value,
                            adjustment.value,
                            bar.timestamp_utc.isoformat(),
                            bar.open,
                            bar.high,
                            bar.low,
                            bar.close,
                            bar.volume,
                            bar.amount,
                            bar.currency,
                            bar.provider,
                            json.dumps(bar.provenance or {}, ensure_ascii=False),
                        )
                        for bar in items
                    ],
                )
                conn.commit()
        # Quota check without VACUUM every write (VACUUM is expensive).
        try:
            size = self.path.stat().st_size if self.path.exists() else 0
            if size > self.max_bytes:
                self.enforce_quota()
        except OSError:
            pass
        return len(items)

    def stats(self) -> CacheStats:
        size = self.path.stat().st_size if self.path.exists() else 0
        with self._lock:
            with self._connect() as conn:
                bar_rows = int(conn.execute("SELECT COUNT(*) FROM bars").fetchone()[0])
                symbols = int(conn.execute("SELECT COUNT(DISTINCT symbol) FROM bars").fetchone()[0])
        return CacheStats(str(self.path), size, self.max_bytes, bar_rows, symbols)

    def clear(self, symbol: str | None = None) -> None:
        with self._lock:
            with self._connect() as conn:
                if symbol:
                    conn.execute("DELETE FROM bars WHERE symbol = ?", (symbol,))
                else:
                    conn.execute("DELETE FROM bars")
                conn.commit()
        # reclaim space
        with self._lock:
            with self._connect() as conn:
                conn.execute("VACUUM")

    def enforce_quota(self) -> None:
        """Delete oldest bars by timestamp until under max_bytes."""
        stats = self.stats()
        if stats.size_bytes <= self.max_bytes:
            return
        with self._lock:
            with self._connect() as conn:
                # delete oldest 10% batches until under limit
                while True:
                    size = self.path.stat().st_size if self.path.exists() else 0
                    if size <= self.max_bytes:
                        break
                    cur = conn.execute("SELECT COUNT(*) FROM bars")
                    total = int(cur.fetchone()[0])
                    if total <= 0:
                        break
                    batch = max(100, total // 10)
                    conn.execute(
                        """
                        DELETE FROM bars WHERE rowid IN (
                            SELECT rowid FROM bars ORDER BY ts ASC LIMIT ?
                        )
                        """,
                        (batch,),
                    )
                    conn.commit()
                conn.execute("VACUUM")


def _row_to_bar(row: sqlite3.Row) -> OhlcvBar:
    prov_raw = row["provenance_json"] or "{}"
    try:
        provenance = json.loads(prov_raw)
    except json.JSONDecodeError:
        provenance = {}
    return OhlcvBar(
        timestamp_utc=datetime.fromisoformat(row["ts"]),
        open=float(row["open"]),
        high=float(row["high"]),
        low=float(row["low"]),
        close=float(row["close"]),
        volume=float(row["volume"]),
        currency=str(row["currency"] or "CNY"),
        adjustment=Adjustment(row["adjustment"]),
        provider=str(row["provider"] or "cache"),
        amount=None if row["amount"] is None else float(row["amount"]),
        provenance={str(k): str(v) for k, v in (provenance or {}).items()},
    )
