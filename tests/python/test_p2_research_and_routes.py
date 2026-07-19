from __future__ import annotations

import json
import unittest

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.server.app import AnalyticsApp, dispatch
from caishenfolio_core.tasks.models import TaskKind, TaskStatus


class ResearchCommandTests(unittest.TestCase):
    def test_symbol_snapshot_success(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/symbol-snapshot",
            body={
                "symbol": "SSE:600000",
                "start": "2024-01-02",
                "end": "2024-01-08",
            },
        )
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["task"]["kind"], TaskKind.RESEARCH.value)
        self.assertEqual(payload["task"]["status"], TaskStatus.SUCCEEDED.value)
        self.assertIsNotNone(payload["artifact"])
        self.assertEqual(payload["artifact"]["kind"], "research_snapshot")
        summary = json.loads(payload["artifact"]["uri_or_payload"])
        self.assertGreaterEqual(summary["bar_count"], 1)
        self.assertIn("disclaimer", summary)
        self.assertTrue(summary["synthetic"])
        self.assertTrue(payload["audit"])

    def test_symbol_snapshot_fail_closed(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/symbol-snapshot",
            body={
                "symbol": "SSE:999999",
                "start": "2024-01-02",
                "end": "2024-01-03",
            },
        )
        self.assertEqual(status, 422)
        self.assertFalse(payload["ok"])
        self.assertEqual(payload["task"]["status"], TaskStatus.FAILED.value)
        self.assertIsNone(payload["artifact"])

    def test_health_phase_p3(self) -> None:
        status, health = dispatch(AnalyticsApp(market=FixtureMarketDataProvider()), "GET", "/health")
        self.assertEqual(status, 200)
        self.assertEqual(health["phase"], "P4")

    def test_get_task_and_artifacts(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        _, created = dispatch(
            app,
            "POST",
            "/research/symbol-snapshot",
            body={"symbol": "NASDAQ:AAPL", "start": "2024-01-02", "end": "2024-01-05"},
        )
        task_id = created["task"]["id"]
        status, task = dispatch(app, "GET", f"/tasks/{task_id}")
        self.assertEqual(status, 200)
        self.assertEqual(task["id"], task_id)

        status, artifacts = dispatch(app, "GET", f"/tasks/{task_id}/artifacts")
        self.assertEqual(status, 200)
        self.assertEqual(len(artifacts["items"]), 1)


if __name__ == "__main__":
    unittest.main()
