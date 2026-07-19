from __future__ import annotations

import json
import threading
import unittest
from datetime import date
from urllib.request import urlopen

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.server.app import AnalyticsApp, dispatch
from caishenfolio_core.server.http_server import serve
from caishenfolio_core.tasks.models import TaskKind, TaskStatus
from caishenfolio_core.tasks.store import InMemoryTaskStore


class TaskStoreTests(unittest.TestCase):
    def test_task_artifact_audit(self) -> None:
        store = InMemoryTaskStore()
        task = store.create_task(TaskKind.MARKET_DATA, "bars", {"symbol": "SSE:600000"})
        store.update_status(task.id, TaskStatus.RUNNING)
        artifact = store.add_artifact(task.id, "bars", "fixture bars", uri_or_payload="memory://bars")
        done = store.update_status(task.id, TaskStatus.SUCCEEDED, "ok")
        audits = store.list_audit(task.id)
        self.assertEqual(done.status, TaskStatus.SUCCEEDED)
        self.assertIn(artifact.id, done.artifact_ids)
        self.assertGreaterEqual(len(audits), 3)


class FixtureMarketTests(unittest.TestCase):
    def test_bars_and_fail_closed(self) -> None:
        provider = FixtureMarketDataProvider()
        ok = provider.historical_bars("NASDAQ:AAPL", date(2024, 1, 2), date(2024, 1, 5))
        self.assertTrue(ok.ok)
        self.assertTrue(ok.data)
        bad = provider.historical_bars("SSE:999999", date(2024, 1, 2), date(2024, 1, 3))
        self.assertFalse(bad.ok)
        self.assertIsNone(bad.data)
        self.assertIn("fail_closed", bad.warnings)


class DispatchTests(unittest.TestCase):
    def test_health_search_bars_tasks(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, health = dispatch(app, "GET", "/health")
        self.assertEqual(status, 200)
        self.assertEqual(health["phase"], "P4")
        self.assertEqual(health["market_provider"], "fixture")
        self.assertTrue(health["market_data_synthetic"])

        status, search = dispatch(app, "GET", "/symbols/search", "q=AAPL")
        self.assertEqual(status, 200)
        self.assertTrue(search["items"])

        status, bars = dispatch(
            app,
            "GET",
            "/market/bars",
            "symbol=SSE:600000&start=2024-01-02&end=2024-01-05",
        )
        self.assertEqual(status, 200)
        self.assertTrue(bars["ok"])

        status, task = dispatch(app, "POST", "/tasks", body={"kind": "market_data", "title": "demo"})
        self.assertEqual(status, 200)
        task_id = str(task["id"])
        status, audits = dispatch(app, "GET", f"/tasks/{task_id}/audit")
        self.assertEqual(status, 200)
        self.assertTrue(audits["items"])


class HttpServerSmokeTests(unittest.TestCase):
    def test_loopback_health(self) -> None:
        port = 18765
        thread = threading.Thread(
            target=serve,
            kwargs={"host": "127.0.0.1", "port": port},
            daemon=True,
        )
        thread.start()
        # tiny wait via retries
        payload = None
        last_error: Exception | None = None
        for _ in range(40):
            try:
                with urlopen(f"http://127.0.0.1:{port}/health", timeout=0.25) as response:
                    payload = json.loads(response.read().decode("utf-8"))
                break
            except Exception as exc:  # noqa: BLE001 - retry until server is up
                last_error = exc
                thread.join(0.05)
        self.assertIsNotNone(payload, msg=str(last_error))
        assert payload is not None
        self.assertEqual(payload["status"], "ok")
        self.assertEqual(payload["product"], "Caishenfolio")


if __name__ == "__main__":
    unittest.main()
