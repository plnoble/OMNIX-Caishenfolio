from __future__ import annotations

import unittest
from datetime import date, timedelta

from caishenfolio_core.data.models import ValuationPoint
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.research.valuation import MIN_HISTORY_POINTS, position_from_history
from caishenfolio_core.server.app import AnalyticsApp, dispatch


def _history(pes: list[float]) -> list[ValuationPoint]:
    start = date(2020, 1, 1)
    return [
        ValuationPoint(as_of=start + timedelta(days=i), pe=pe, pb=pe / 10.0, dividend_yield=0.02)
        for i, pe in enumerate(pes)
    ]


class PositionTests(unittest.TestCase):
    def test_a_low_multiple_ranks_low_in_its_own_history(self) -> None:
        # Ten years between 20 and 60; today's 21 sits near the bottom.
        history = _history([float(v) for v in range(60, 19, -1)] + [21.0])

        position = position_from_history("SSE:600000", history)
        pe = position.metric("市盈率 PE")

        self.assertIsNotNone(pe)
        self.assertEqual(pe.current, 21.0)
        self.assertLess(pe.percentile, 10.0)
        self.assertEqual(pe.low, 20.0)
        self.assertEqual(pe.high, 60.0)

    def test_a_high_multiple_ranks_high(self) -> None:
        history = _history([float(v) for v in range(20, 61)] + [61.0])

        pe = position_from_history("SSE:600000", history).metric("市盈率 PE")
        self.assertGreater(pe.percentile, 90.0)

    def test_reports_the_span_it_ranked_against(self) -> None:
        history = _history([25.0] * 100)
        position = position_from_history("SSE:600000", history)

        self.assertEqual(position.span_start, date(2020, 1, 1))
        self.assertEqual(position.span_end, date(2020, 1, 1) + timedelta(days=99))
        self.assertEqual(position.as_of, position.span_end)

    def test_a_thin_history_is_marked_unreliable(self) -> None:
        position = position_from_history("SSE:600000", _history([20.0, 25.0, 30.0]))
        pe = position.metric("市盈率 PE")

        self.assertFalse(pe.is_reliable)
        self.assertLess(pe.sample_size, MIN_HISTORY_POINTS)
        self.assertTrue(any("分位不可靠" in note for note in position.notes))

    def test_a_long_history_is_reliable(self) -> None:
        position = position_from_history("SSE:600000", _history([20.0 + i * 0.1 for i in range(300)]))

        self.assertTrue(position.metric("市盈率 PE").is_reliable)
        self.assertFalse(any("分位不可靠" in note for note in position.notes))

    def test_a_negative_pe_is_called_out_as_meaningless(self) -> None:
        history = _history([float(v) for v in range(20, 120)] + [-15.0])

        position = position_from_history("SSE:600000", history)
        self.assertTrue(any("为负" in note for note in position.notes))

    def test_missing_values_are_reported_not_guessed(self) -> None:
        history = [ValuationPoint(as_of=date(2024, 1, 1), pe=None, pb=None, dividend_yield=None)]

        position = position_from_history("SSE:600000", history)
        for metric in position.metrics:
            self.assertIsNone(metric.current)
            self.assertIsNone(metric.percentile)
        self.assertTrue(any("当前值缺失" in note for note in position.notes))

    def test_empty_history_does_not_throw(self) -> None:
        position = position_from_history("SSE:600000", [])

        self.assertIsNone(position.span_start)
        self.assertTrue(all(m.percentile is None for m in position.metrics))

    def test_the_payload_states_that_a_percentile_is_not_a_recommendation(self) -> None:
        payload = position_from_history("SSE:600000", _history([20.0] * 100)).to_dict()

        disclaimer = str(payload["disclaimer"])
        self.assertIn("非投资建议", disclaimer)
        self.assertIn("不代表便宜或应当买入", disclaimer)

    def test_median_low_and_high_describe_the_distribution(self) -> None:
        position = position_from_history("SSE:600000", _history([10.0, 20.0, 30.0, 40.0, 50.0]))
        pe = position.metric("市盈率 PE")

        self.assertEqual(pe.low, 10.0)
        self.assertEqual(pe.high, 50.0)
        self.assertEqual(pe.median, 30.0)


class RouteTests(unittest.TestCase):
    def setUp(self) -> None:
        self.app = AnalyticsApp(market=FixtureMarketDataProvider())

    def test_valuation_route_returns_positions_and_history(self) -> None:
        status, payload = dispatch(self.app, "GET", "/market/valuation", "symbol=SSE:600000&years=3")

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"], msg=payload.get("error"))
        metrics = {m["name"]: m for m in payload["data"]["metrics"]}
        self.assertIn("市盈率 PE", metrics)
        self.assertIsNotNone(metrics["市盈率 PE"]["percentile"])
        self.assertTrue(payload["history"])

    def test_valuation_route_requires_a_symbol(self) -> None:
        status, _ = dispatch(self.app, "GET", "/market/valuation")
        self.assertEqual(status, 400)

    def test_an_instrument_without_valuation_data_fails_closed(self) -> None:
        status, payload = dispatch(self.app, "GET", "/market/valuation", "symbol=FX:USDCNY")

        self.assertEqual(status, 200)
        self.assertFalse(payload["ok"])
        self.assertIsNone(payload["data"])

    def test_financials_route_returns_periods_with_growth(self) -> None:
        status, payload = dispatch(self.app, "GET", "/market/financials", "symbol=SSE:600000&periods=4")

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"], msg=payload.get("error"))
        self.assertEqual(len(payload["data"]), 4)
        self.assertIn("revenue", payload["data"][0])
        self.assertIn("roe", payload["data"][0])

    def test_financials_route_requires_a_symbol(self) -> None:
        status, _ = dispatch(self.app, "GET", "/market/financials")
        self.assertEqual(status, 400)


if __name__ == "__main__":
    unittest.main()
