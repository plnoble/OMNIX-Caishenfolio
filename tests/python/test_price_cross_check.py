from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.data.models import ProviderResult, Quote
from caishenfolio_core.market.composite import CompositeMarketDataProvider
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.server.app import AnalyticsApp, dispatch

AS_OF = date(2026, 7, 31)


class _StubProvider:
    """A source that answers with a fixed price, or fails."""

    def __init__(self, code: str, price: float | None, currency: str = "CNY") -> None:
        self.PROVIDER_CODE = code
        self.ready = True
        self._price = price
        self._currency = currency

    def search(self, query: str = "", limit: int = 10) -> list:
        return []

    def historical_bars(self, *args, **kwargs):  # noqa: ANN002, ANN003
        return ProviderResult.failure(self.PROVIDER_CODE, "no bars")

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        if self._price is None:
            return ProviderResult.failure(self.PROVIDER_CODE, "upstream down")
        return ProviderResult.success(
            self.PROVIDER_CODE,
            Quote(
                symbol=symbol,
                price=self._price,
                currency=self._currency,
                as_of=AS_OF,
                provider=self.PROVIDER_CODE,
            ),
        )


def _warning_value(warnings: tuple[str, ...], prefix: str) -> str | None:
    for item in warnings:
        if item.startswith(prefix):
            return item[len(prefix):]
    return None


class CrossCheckTests(unittest.TestCase):
    def test_default_behaviour_is_unchanged(self) -> None:
        composite = CompositeMarketDataProvider([_StubProvider("a", 10.0), _StubProvider("b", 99.0)])

        result = composite.latest_quote("SSE:600000")

        # Without cross-check the first source still wins, as before.
        self.assertTrue(result.ok)
        assert result.data is not None
        self.assertEqual(result.data.price, 10.0)
        self.assertIn("resolved_by:a", result.warnings)

    def test_agreeing_sources_produce_no_disagreement_warning(self) -> None:
        composite = CompositeMarketDataProvider([_StubProvider("a", 10.00), _StubProvider("b", 10.10)])

        result = composite.latest_quote("SSE:600000", cross_check=True, tolerance_pct=2.0)

        self.assertTrue(result.ok)
        assert result.data is not None
        self.assertAlmostEqual(result.data.price, 10.05, places=6)
        self.assertIn("cross_checked", result.warnings)
        self.assertIn("sources:2", result.warnings)
        self.assertIsNone(_warning_value(result.warnings, "price_disagreement:"))

    def test_disagreeing_sources_are_flagged_with_the_spread(self) -> None:
        composite = CompositeMarketDataProvider([_StubProvider("a", 10.0), _StubProvider("b", 12.0)])

        result = composite.latest_quote("SSE:600000", cross_check=True, tolerance_pct=2.0)

        self.assertTrue(result.ok)
        spread = _warning_value(result.warnings, "price_disagreement:")
        self.assertIsNotNone(spread)
        # (12 - 10) / 11 = 18.18%
        self.assertAlmostEqual(float(spread), 18.18, places=1)

    def test_median_resists_one_bad_source(self) -> None:
        composite = CompositeMarketDataProvider([
            _StubProvider("a", 10.0),
            _StubProvider("b", 10.1),
            _StubProvider("c", 1000.0),
        ])

        result = composite.latest_quote("SSE:600000", cross_check=True)

        assert result.data is not None
        # The outlier moves the reported price by a cent, not by two orders of magnitude.
        self.assertAlmostEqual(result.data.price, 10.1, places=6)
        self.assertIsNotNone(_warning_value(result.warnings, "price_disagreement:"))

    def test_every_source_price_is_recorded(self) -> None:
        composite = CompositeMarketDataProvider([_StubProvider("a", 10.0), _StubProvider("b", 12.0)])

        result = composite.latest_quote("SSE:600000", cross_check=True)

        assert result.data is not None
        payload = result.data.to_dict()
        self.assertEqual(payload["source_count"], 2)
        self.assertIn("a=10", str(payload["sources"]))
        self.assertIn("b=12", str(payload["sources"]))
        self.assertGreater(payload["spread_pct"], 0)

    def test_a_single_available_source_still_answers(self) -> None:
        composite = CompositeMarketDataProvider([_StubProvider("a", 10.0), _StubProvider("b", None)])

        result = composite.latest_quote("SSE:600000", cross_check=True)

        self.assertTrue(result.ok)
        assert result.data is not None
        self.assertEqual(result.data.price, 10.0)
        self.assertIn("single_source", result.warnings)
        self.assertIsNone(_warning_value(result.warnings, "price_disagreement:"))

    def test_all_sources_failing_still_fails_closed(self) -> None:
        composite = CompositeMarketDataProvider([_StubProvider("a", None), _StubProvider("b", None)])

        result = composite.latest_quote("SSE:600000", cross_check=True)

        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("fail_closed", result.warnings)

    def test_a_source_quoting_another_currency_is_reported_not_averaged(self) -> None:
        composite = CompositeMarketDataProvider([
            _StubProvider("a", 10.0, "CNY"),
            _StubProvider("b", 10.1, "CNY"),
            _StubProvider("c", 1.4, "USD"),
        ])

        result = composite.latest_quote("SSE:600000", cross_check=True)

        assert result.data is not None
        self.assertEqual(result.data.currency, "CNY")
        self.assertAlmostEqual(result.data.price, 10.05, places=6)
        self.assertIn("currency_disagreement:c", result.warnings)


class QuoteRouteTests(unittest.TestCase):
    def setUp(self) -> None:
        self.app = AnalyticsApp(market=FixtureMarketDataProvider())

    def test_route_accepts_the_cross_check_flag(self) -> None:
        status, payload = dispatch(
            self.app, "GET", "/market/quote", "symbol=SSE:600000&cross_check=1&tolerance=1.5")

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])

    def test_route_rejects_a_non_numeric_tolerance(self) -> None:
        status, _ = dispatch(
            self.app, "GET", "/market/quote", "symbol=SSE:600000&cross_check=1&tolerance=abc")

        self.assertEqual(status, 400)

    def test_a_single_provider_ignores_cross_check_without_erroring(self) -> None:
        # FixtureMarketDataProvider has no cross-check to do; the plain signature must still work.
        status, payload = dispatch(self.app, "GET", "/market/quote", "symbol=SSE:600000&cross_check=1")

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["data"]["symbol"], "SSE:600000")


if __name__ == "__main__":
    unittest.main()
