from __future__ import annotations

import json
import os
import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path
from threading import Lock
from typing import Any

from caishenfolio_core.research.grid import GridPlan, next_grid_actions


def default_ledger_path() -> Path:
    env = (os.environ.get("CAISHENFOLIO_GRID_LEDGER_PATH") or "").strip()
    if env:
        return Path(env)
    cache = (os.environ.get("CAISHENFOLIO_BARS_CACHE_PATH") or "").strip()
    if cache:
        return Path(cache).with_name("grid_ledger.db")
    base = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    root = base / "Caishenfolio" / "state"
    root.mkdir(parents=True, exist_ok=True)
    return root / "grid_ledger.db"


class GridLedgerStore:
    """Manual grid trade journal (not broker execution)."""

    def __init__(self, path: Path | None = None) -> None:
        self.path = path or default_ledger_path()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = Lock()
        self._init()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(str(self.path), timeout=30)
        conn.row_factory = sqlite3.Row
        # WAL reduces Windows file-lock issues during short-lived connections
        try:
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA synchronous=NORMAL")
        except sqlite3.Error:
            pass
        return conn

    def _init(self) -> None:
        with self._lock:
            with self._connect() as conn:
                conn.executescript(
                    """
                    CREATE TABLE IF NOT EXISTS plans (
                        id TEXT PRIMARY KEY,
                        symbol TEXT NOT NULL,
                        name TEXT,
                        lower REAL NOT NULL,
                        upper REAL NOT NULL,
                        grid_count INTEGER NOT NULL,
                        order_cash REAL NOT NULL,
                        note TEXT,
                        created_at TEXT NOT NULL,
                        active INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE TABLE IF NOT EXISTS fills (
                        id TEXT PRIMARY KEY,
                        plan_id TEXT NOT NULL,
                        side TEXT NOT NULL,
                        price REAL NOT NULL,
                        qty REAL NOT NULL,
                        fee REAL NOT NULL DEFAULT 0,
                        grid_level REAL,
                        ts TEXT NOT NULL,
                        note TEXT,
                        FOREIGN KEY(plan_id) REFERENCES plans(id)
                    );
                    """
                )

    def create_plan(
        self,
        *,
        symbol: str,
        lower: float,
        upper: float,
        grid_count: int,
        order_cash: float,
        name: str = "",
        note: str = "",
    ) -> dict[str, object]:
        plan = GridPlan(lower=lower, upper=upper, grid_count=grid_count, order_cash=order_cash)
        pid = f"grid_{uuid.uuid4().hex[:12]}"
        now = datetime.now(timezone.utc).isoformat()
        with self._lock:
            with self._connect() as conn:
                conn.execute(
                    """
                    INSERT INTO plans(id, symbol, name, lower, upper, grid_count, order_cash, note, created_at, active)
                    VALUES (?,?,?,?,?,?,?,?,?,1)
                    """,
                    (
                        pid,
                        symbol.strip(),
                        name or symbol,
                        plan.lower,
                        plan.upper,
                        plan.grid_count,
                        plan.order_cash,
                        note,
                        now,
                    ),
                )
                conn.commit()
        return self.get_plan(pid)  # type: ignore[return-value]

    def list_plans(self, active_only: bool = True) -> list[dict[str, object]]:
        with self._lock:
            with self._connect() as conn:
                if active_only:
                    rows = conn.execute(
                        "SELECT * FROM plans WHERE active=1 ORDER BY created_at DESC"
                    ).fetchall()
                else:
                    rows = conn.execute("SELECT * FROM plans ORDER BY created_at DESC").fetchall()
        return [self._plan_row(r) for r in rows]

    def get_plan(self, plan_id: str) -> dict[str, object] | None:
        with self._lock:
            with self._connect() as conn:
                row = conn.execute("SELECT * FROM plans WHERE id=?", (plan_id,)).fetchone()
        return None if row is None else self._plan_row(row)

    def deactivate_plan(self, plan_id: str) -> dict[str, object]:
        with self._lock:
            with self._connect() as conn:
                conn.execute("UPDATE plans SET active=0 WHERE id=?", (plan_id,))
                conn.commit()
        p = self.get_plan(plan_id)
        return {"ok": p is not None, "plan": p}

    def add_fill(
        self,
        *,
        plan_id: str,
        side: str,
        price: float,
        qty: float,
        fee: float = 0.0,
        grid_level: float | None = None,
        ts: str | None = None,
        note: str = "",
    ) -> dict[str, object]:
        side_n = side.strip().lower()
        if side_n not in {"buy", "sell"}:
            return {"ok": False, "error": "side 必须是 buy 或 sell。"}
        if price <= 0 or qty <= 0:
            return {"ok": False, "error": "price/qty 必须为正。"}
        plan = self.get_plan(plan_id)
        if plan is None:
            return {"ok": False, "error": f"未知网格方案 {plan_id}"}
        fid = f"fill_{uuid.uuid4().hex[:12]}"
        when = ts or datetime.now(timezone.utc).isoformat()
        with self._lock:
            with self._connect() as conn:
                conn.execute(
                    """
                    INSERT INTO fills(id, plan_id, side, price, qty, fee, grid_level, ts, note)
                    VALUES (?,?,?,?,?,?,?,?,?)
                    """,
                    (fid, plan_id, side_n, price, qty, fee, grid_level, when, note),
                )
                conn.commit()
        return {"ok": True, "fill_id": fid, "snapshot": self.snapshot(plan_id, last_price=price)}

    def list_fills(self, plan_id: str) -> list[dict[str, object]]:
        with self._lock:
            with self._connect() as conn:
                rows = conn.execute(
                    "SELECT * FROM fills WHERE plan_id=? ORDER BY ts ASC",
                    (plan_id,),
                ).fetchall()
        return [dict(r) for r in rows]

    def snapshot(self, plan_id: str, *, last_price: float | None = None) -> dict[str, object]:
        plan_row = self.get_plan(plan_id)
        if plan_row is None:
            return {"ok": False, "error": "未知方案"}
        fills = self.list_fills(plan_id)
        # FIFO lots for PnL
        lots: list[dict[str, float]] = []
        realized = 0.0
        fees = 0.0
        for f in fills:
            side = str(f["side"])
            price = float(f["price"])
            qty = float(f["qty"])
            fee = float(f.get("fee") or 0)
            fees += fee
            if side == "buy":
                lots.append({"price": price, "qty": qty, "buy_level": float(f.get("grid_level") or price)})
            else:
                remain = qty
                while remain > 1e-12 and lots:
                    lot = lots[0]
                    take = min(remain, lot["qty"])
                    realized += take * (price - lot["price"])
                    lot["qty"] -= take
                    remain -= take
                    if lot["qty"] <= 1e-12:
                        lots.pop(0)
                # leftover sell without lots ignored for realized
        open_qty = sum(l["qty"] for l in lots)
        open_cost = sum(l["qty"] * l["price"] for l in lots)
        avg_cost = (open_cost / open_qty) if open_qty > 1e-12 else None
        px = float(last_price) if last_price is not None else avg_cost
        unreal = None
        if px is not None and open_qty > 1e-12 and avg_cost is not None:
            unreal = open_qty * (px - avg_cost)

        plan = GridPlan(
            lower=float(plan_row["lower"]),
            upper=float(plan_row["upper"]),
            grid_count=int(plan_row["grid_count"]),
            order_cash=float(plan_row["order_cash"]),
        )
        open_lots = [
            {"buy_level": l["buy_level"], "qty": l["qty"], "price": l["price"]} for l in lots if l["qty"] > 0
        ]
        actions = next_grid_actions(
            plan=plan,
            last_price=float(px if px is not None else plan.lower),
            open_lots=open_lots,
        )
        return {
            "ok": True,
            "plan": plan_row,
            "fills": fills,
            "realized_pnl": realized,
            "fees": fees,
            "open_qty": open_qty,
            "avg_cost": avg_cost,
            "unrealized_pnl": unreal,
            "last_price": px,
            "next_actions": actions,
            "disclaimer": "台账为人工记录，非券商成交；盈亏为估算。研究/模拟结论，非投资建议。",
        }

    def _plan_row(self, row: sqlite3.Row) -> dict[str, object]:
        d = dict(row)
        try:
            plan = GridPlan(
                lower=float(d["lower"]),
                upper=float(d["upper"]),
                grid_count=int(d["grid_count"]),
                order_cash=float(d["order_cash"]),
            )
            d["levels"] = plan.levels()
            d["step"] = (plan.upper - plan.lower) / plan.grid_count
        except Exception:  # noqa: BLE001
            d["levels"] = []
        return d
