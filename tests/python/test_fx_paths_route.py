from __future__ import annotations

import unittest

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.server.app import AnalyticsApp, dispatch


class FxPathsRouteTests(unittest.TestCase):
    """Contract for POST /research/fx-paths — the N2 path-comparison endpoint."""

    def test_held_path_comparison_against_fixture_quotes(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                "held": {"currency": "JPY", "amount": 2450, "cost_in_base": 100},
            },
        )

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        # Fixture quotes: USD/CNY=7.2, USD/JPY=150 → 100/7.2 and 2450/150.
        by_key = {p["key"]: p for p in payload["paths"]}
        self.assertAlmostEqual(by_key["base_to_target"]["gross_amount"], 13.888889, places=6)
        self.assertAlmostEqual(by_key["held_to_target"]["gross_amount"], 16.333333, places=6)
        # Fields ending in _pct are percent-unit values (17.6, not 0.176).
        self.assertAlmostEqual(payload["held_advantage_gross_pct"], 17.6, places=3)
        # Every quote states its direction, source and date — no bare numbers.
        for quote in payload["quotes"].values():
            self.assertIn("direction", quote)
            self.assertTrue(quote["as_of"])
        self.assertIn("非投资建议", payload["disclaimer"])

    def test_a_missing_quote_fails_the_whole_comparison_closed(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        # EUR is not in the fixture pool → the required USD/EUR quote cannot be fetched.
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "EUR",
                "base_amount": 100,
            },
        )

        self.assertEqual(status, 422)
        self.assertFalse(payload["ok"])
        self.assertIn("fail-closed", payload["error"])

    def test_missing_required_fields_is_a_client_error(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(app, "POST", "/research/fx-paths", body={})

        self.assertEqual(status, 400)
        self.assertIn("必须提供", payload["error"])

    def test_costs_round_trip_through_the_route(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                "held": {"currency": "JPY", "amount": 2450, "cost_in_base": 100},
                "costs": {
                    # Tri-state contract: every component explicit (0 = confirmed none).
                    "held_to_target": {"fee_pct": 5, "fixed_fee": 0},
                    "base_to_target": {"spread_pct": 0, "fixed_fee": 0},
                },
            },
        )

        self.assertEqual(status, 200)
        by_key = {p["key"]: p for p in payload["paths"]}
        # 5% fee on the held path: exact gross 2450/150, net = gross × 0.95.
        self.assertAlmostEqual(by_key["held_to_target"]["net_amount"], 2450 / 150 * 0.95, places=5)
        self.assertGreater(payload["held_advantage_net_pct"], 0)
        self.assertLess(payload["held_advantage_net_pct"], payload["held_advantage_gross_pct"])

    def test_new_money_only_needs_one_quote(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={"base_currency": "CNY", "target_currency": "USD", "base_amount": 1000},
        )

        self.assertEqual(status, 200)
        keys = {p["key"] for p in payload["paths"]}
        self.assertEqual(keys, {"base_to_target"})
        self.assertAlmostEqual(payload["paths"][0]["gross_amount"], 1000 / 7.2, places=6)


if __name__ == "__main__":
    unittest.main()
