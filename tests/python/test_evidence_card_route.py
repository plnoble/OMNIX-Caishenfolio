from __future__ import annotations

import json
import unittest

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.research.evidence_card import BANNED_PHRASES
from caishenfolio_core.server.app import AnalyticsApp, dispatch
from caishenfolio_core.tasks.models import TaskKind, TaskStatus


class EvidenceCardRouteTests(unittest.TestCase):
    """Contract for POST /research/evidence-card — the N4 endpoint (plan §9)."""

    def test_a_full_card_maps_to_task_artifact_and_audit(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/evidence-card",
            body={"symbol": "SSE:600000", "start": "2024-06-01", "end": "2025-12-31"},
        )

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["task"]["kind"], TaskKind.RESEARCH.value)
        self.assertEqual(payload["task"]["status"], TaskStatus.SUCCEEDED.value)
        self.assertEqual(payload["artifact"]["kind"], "evidence_card")
        self.assertTrue(payload["audit"])

        # The artifact is the card itself, stored as JSON — the claim is auditable.
        stored = json.loads(payload["artifact"]["uri_or_payload"])
        self.assertEqual(stored["symbol"], "SSE:600000")
        self.assertEqual(stored["conclusion"]["status"], payload["card"]["conclusion"]["status"])

    def test_a_portfolio_input_reaches_the_impact_section(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/evidence-card",
            body={
                "symbol": "SSE:600000",
                "start": "2024-06-01",
                "end": "2025-12-31",
                "portfolio": {
                    "candidate_amount": 90_000.0,
                    "candidate_currency": "CNY",
                    "portfolio_value": 100_000.0,
                    "portfolio_currency": "CNY",
                    "current_weight_of_symbol": 0.05,
                    "single_position_limit": 0.2,
                    "cash_available": 95_000.0,
                    "horizon_days": 365,
                },
            },
        )

        self.assertEqual(status, 200)
        card = payload["card"]
        # The risk limit outranks any evidence leaning (plan §9 conclusion ordering).
        self.assertEqual(card["conclusion"]["status"], "risk_exceeded")
        self.assertIsNotNone(card["portfolio_impact"])
        self.assertTrue(any("超过你设定的上限" in note for note in card["portfolio_impact"]["notes"]))

    def test_a_missing_symbol_is_a_client_error(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(app, "POST", "/research/evidence-card", body={})

        self.assertEqual(status, 400)
        self.assertIn("必须提供", payload["error"])

    def test_a_failing_symbol_yields_insufficient_not_fabrication(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/evidence-card",
            body={"symbol": "SSE:999999", "start": "2025-01-01", "end": "2025-12-31"},
        )

        self.assertEqual(status, 200)
        card = payload["card"]
        self.assertEqual(card["conclusion"]["status"], "insufficient")
        self.assertTrue(card["data_quality"]["missing"])

    def test_every_item_in_the_served_card_is_traceable_and_unbanned(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        _, payload = dispatch(
            app,
            "POST",
            "/research/evidence-card",
            body={
                "symbol": "SSE:600000",
                "start": "2024-06-01",
                "end": "2025-12-31",
                "portfolio": {
                    "candidate_amount": 10_000.0,
                    "candidate_currency": "CNY",
                    "portfolio_value": 100_000.0,
                    "portfolio_currency": "CNY",
                    "current_weight_of_symbol": 0.1,
                    "single_position_limit": 0.5,
                    "cash_available": 50_000.0,
                },
            },
        )

        card = payload["card"]
        for section in ("evidence_for", "evidence_against", "unknowns"):
            for item in card[section]:
                self.assertTrue(item["basis"], f"{section} item lacks a basis")

        def all_strings(node) -> list[str]:
            if isinstance(node, dict):
                return [s for v in node.values() for s in all_strings(v)]
            if isinstance(node, list):
                return [s for v in node for s in all_strings(v)]
            return [node] if isinstance(node, str) else []

        for phrase in BANNED_PHRASES:
            for text in all_strings(card):
                self.assertNotIn(phrase, text, msg=f"banned phrase {phrase!r} in {text[:40]!r}")

        self.assertIn("非投资建议", card["disclaimer"])


if __name__ == "__main__":
    unittest.main()
