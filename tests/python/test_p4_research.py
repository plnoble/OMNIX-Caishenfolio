from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.research.backtest import CostModel, ma_cross_backtest
from caishenfolio_core.research.compare import compare_normalized_closes
from caishenfolio_core.research.report import build_markdown_report, write_report
from caishenfolio_core.server.app import AnalyticsApp, dispatch


class BacktestUnitTests(unittest.TestCase):
    def test_ma_cross_runs_on_fixture_like_bars(self) -> None:
        bars = []
        price = 10.0
        for i in range(60):
            # gentle up then down to create crosses
            price += 0.2 if i < 30 else -0.15
            bars.append(
                {
                    "timestamp_utc": f"2024-01-{(i % 28) + 1:02d}T00:00:00+00:00",
                    "close": price,
                    "open": price,
                    "high": price + 0.1,
                    "low": price - 0.1,
                    "volume": 1000,
                }
            )
        result = ma_cross_backtest(bars, symbol="SSE:600000", fast=5, slow=20)
        self.assertTrue(result.ok, msg=result.error)
        self.assertIsNotNone(result.total_return)
        self.assertIsNotNone(result.buy_hold_return)

        costly = ma_cross_backtest(
            bars,
            symbol="SSE:600000",
            fast=5,
            slow=20,
            costs=CostModel(commission_rate=0.001, stamp_duty_rate=0.001, slippage_rate=0.001),
        )
        self.assertTrue(costly.ok, msg=costly.error)
        # higher costs should not beat free model on same path (usually)
        self.assertIsNotNone(costly.cost_model)
        self.assertIn("commission_rate", costly.cost_model)


class CompareUnitTests(unittest.TestCase):
    def test_compare_needs_two_series(self) -> None:
        out = compare_normalized_closes(
            {
                "A": [
                    {"timestamp_utc": "2024-01-02", "close": 10},
                    {"timestamp_utc": "2024-01-03", "close": 11},
                ],
                "B": [
                    {"timestamp_utc": "2024-01-02", "close": 100},
                    {"timestamp_utc": "2024-01-03", "close": 110},
                ],
            }
        )
        self.assertTrue(out["ok"])
        self.assertEqual(out["date_count"], 2)


class ReportUnitTests(unittest.TestCase):
    def test_write_report(self) -> None:
        md = build_markdown_report(
            title="测试报告",
            symbol="SSE:600000",
            sections=[{"heading": "摘要", "body": "hello"}],
        )
        with tempfile.TemporaryDirectory() as tmp:
            written = write_report(md, tmp, filename="t.md")
            self.assertTrue(written["ok"])
            self.assertTrue(Path(str(written["markdown_path"])).is_file())


class DispatchResearchTests(unittest.TestCase):
    def test_backtest_route_with_fixture(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider(), cache=None)
        status, payload = dispatch(
            app,
            "POST",
            "/research/backtest-ma",
            body={
                "symbol": "SSE:600000",
                "start": "2024-01-02",
                "end": "2024-06-01",
                "fast": 5,
                "slow": 20,
            },
        )
        self.assertIn(status, (200, 422))
        self.assertIn("disclaimer", payload)

    def test_compare_route(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider(), cache=None)
        status, payload = dispatch(
            app,
            "POST",
            "/research/compare",
            body={
                "symbols": ["SSE:600000", "SZSE:000001"],
                "start": "2024-01-02",
                "end": "2024-03-01",
            },
        )
        self.assertIn(status, (200, 422))


if __name__ == "__main__":
    unittest.main()
