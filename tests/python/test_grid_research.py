from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.research.grid import grid_backtest, suggest_grid_from_bars
from caishenfolio_core.research.grid_ledger import GridLedgerStore
from caishenfolio_core.server.app import AnalyticsApp, dispatch


def _oscillating_bars(n: int = 80, mid: float = 10.0, amp: float = 1.5) -> list[dict]:
    bars = []
    for i in range(n):
        # sine-like oscillation for grid activity
        import math

        price = mid + amp * math.sin(i / 4.0)
        bars.append(
            {
                "timestamp_utc": f"2024-01-{(i % 28) + 1:02d}T00:00:00+00:00",
                "open": price,
                "high": price + 0.05,
                "low": price - 0.05,
                "close": price,
                "volume": 1000,
            }
        )
    return bars


class GridSuggestAndBacktestTests(unittest.TestCase):
    def test_suggest_and_backtest(self) -> None:
        bars = _oscillating_bars()
        sug = suggest_grid_from_bars(bars, symbol="SSE:600000", order_cash=500)
        self.assertTrue(sug["ok"], msg=sug.get("error"))
        plan = sug["plan"]
        self.assertGreater(plan["upper"], plan["lower"])
        self.assertGreaterEqual(plan["grid_count"], 2)

        bt = grid_backtest(
            bars,
            symbol="SSE:600000",
            lower=float(plan["lower"]),
            upper=float(plan["upper"]),
            grid_count=int(plan["grid_count"]),
            order_cash=500,
        )
        self.assertTrue(bt["ok"], msg=bt.get("error"))
        self.assertIn("total_return", bt)
        self.assertIn("disclaimer", bt)


class GridLedgerTests(unittest.TestCase):
    def test_fifo_pnl_and_next_actions(self) -> None:
        # ignore_cleanup_errors: Windows may briefly lock SQLite files after close
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            store = GridLedgerStore(Path(tmp) / "ledger.db")
            plan = store.create_plan(
                symbol="SSE:600000",
                lower=9.0,
                upper=11.0,
                grid_count=4,
                order_cash=1000,
                name="test",
            )
            pid = str(plan["id"])
            store.add_fill(plan_id=pid, side="buy", price=9.5, qty=100, fee=1, grid_level=9.5)
            store.add_fill(plan_id=pid, side="sell", price=10.0, qty=50, fee=1, grid_level=10.0)
            snap = store.snapshot(pid, last_price=9.8)
            self.assertTrue(snap["ok"])
            # 50 shares sold: (10-9.5)*50 = 25 realized
            self.assertAlmostEqual(float(snap["realized_pnl"]), 25.0, places=4)
            self.assertAlmostEqual(float(snap["open_qty"]), 50.0, places=4)
            self.assertIn("next_actions", snap)
            self.assertIsNotNone(snap["next_actions"].get("next_buy_level"))


class GridRouteTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.ledger = GridLedgerStore(Path(self._tmp.name) / "grid.db")
        self.app = AnalyticsApp(
            market=FixtureMarketDataProvider(),
            cache=None,
            grid_ledger=self.ledger,
        )

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_suggest_route(self) -> None:
        status, payload = dispatch(
            self.app,
            "POST",
            "/research/grid-suggest",
            body={
                "symbol": "SSE:600000",
                "start": "2024-01-02",
                "end": "2024-06-01",
                "order_cash": 800,
            },
        )
        self.assertIn(status, (200, 422))
        self.assertIn("disclaimer", payload)

    def test_backtest_route(self) -> None:
        status, payload = dispatch(
            self.app,
            "POST",
            "/research/grid-backtest",
            body={
                "symbol": "SSE:600000",
                "start": "2024-01-02",
                "end": "2024-06-01",
                "lower": 9.0,
                "upper": 12.0,
                "grid_count": 6,
                "order_cash": 500,
            },
        )
        self.assertIn(status, (200, 422))
        if status == 200:
            self.assertTrue(payload.get("ok"))

    def test_ledger_routes(self) -> None:
        status, created = dispatch(
            self.app,
            "POST",
            "/research/grid/plans",
            body={
                "symbol": "SSE:600000",
                "lower": 9.0,
                "upper": 11.0,
                "grid_count": 4,
                "order_cash": 1000,
                "name": "route-test",
            },
        )
        self.assertEqual(status, 200)
        self.assertTrue(created.get("ok"))
        pid = created["plan"]["id"]

        status, fill = dispatch(
            self.app,
            "POST",
            "/research/grid/fills",
            body={"plan_id": pid, "side": "buy", "price": 9.5, "qty": 100, "fee": 0.5},
        )
        self.assertEqual(status, 200)
        self.assertTrue(fill.get("ok"))

        status, snap = dispatch(
            self.app,
            "GET",
            f"/research/grid/plans/{pid}/snapshot",
            query="last_price=9.8",
        )
        self.assertEqual(status, 200)
        self.assertTrue(snap.get("ok"))
        self.assertAlmostEqual(float(snap["open_qty"]), 100.0, places=4)

        status, plans = dispatch(self.app, "GET", "/research/grid/plans", query="")
        self.assertEqual(status, 200)
        self.assertGreaterEqual(len(plans.get("items") or []), 1)


if __name__ == "__main__":
    unittest.main()
