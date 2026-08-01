from __future__ import annotations

import unittest
from datetime import date, datetime, timezone

from caishenfolio_core.data.fundamentals import PerShareFundamental
from caishenfolio_core.data.models import Adjustment, OhlcvBar
from caishenfolio_core.market.em_fundamentals import (
    HK_PUBLICATION_LAG_DAYS,
    parse_hk_annual,
    parse_us_quarterly,
)
from caishenfolio_core.market.valuation_series import describe_method, reconstruct_valuation


def bar(day: date, close: float) -> OhlcvBar:
    return OhlcvBar(
        timestamp_utc=datetime(day.year, day.month, day.day, tzinfo=timezone.utc),
        open=close, high=close, low=close, close=close, volume=1000.0,
        currency="USD", adjustment=Adjustment.RAW, provider="test",
    )


class UsQuarterlyTests(unittest.TestCase):
    def _rows(self) -> list[dict]:
        # Shape taken from stock_financial_us_analysis_indicator_em(indicator="单季报").
        return [
            {"REPORT_DATE": "2025-03-29 00:00:00", "NOTICE_DATE": "2025-05-01 00:00:00",
             "BASIC_EPS": 1.0, "DILUTED_EPS": 1.0},
            {"REPORT_DATE": "2025-06-28 00:00:00", "NOTICE_DATE": "2025-07-31 00:00:00",
             "BASIC_EPS": 1.1, "DILUTED_EPS": 1.1},
            {"REPORT_DATE": "2025-09-27 00:00:00", "NOTICE_DATE": "2025-10-30 00:00:00",
             "BASIC_EPS": 1.2, "DILUTED_EPS": 1.2},
            {"REPORT_DATE": "2025-12-27 00:00:00", "NOTICE_DATE": "2026-01-29 00:00:00",
             "BASIC_EPS": 1.7, "DILUTED_EPS": 1.7},
        ]

    def test_sums_four_quarters_into_a_ttm_figure(self) -> None:
        result = parse_us_quarterly(self._rows())

        item = result[-1]
        self.assertAlmostEqual(item.eps_ttm, 5.0)
        self.assertEqual(item.period_end, date(2025, 12, 27))

    def test_uses_the_announcement_date_not_the_period_end(self) -> None:
        item = parse_us_quarterly(self._rows())[-1]

        # The Q4 figure was not knowable on 2025-12-27; it was announced in January.
        self.assertEqual(item.effective_date, date(2026, 1, 29))
        self.assertFalse(item.effective_date_estimated)

    def test_fewer_than_four_quarters_yields_nothing(self) -> None:
        # Summing three quarters would understate earnings and overstate PE.
        self.assertEqual(parse_us_quarterly(self._rows()[:3]), [])

    def test_prefers_diluted_eps_and_falls_back_to_basic(self) -> None:
        rows = self._rows()
        rows[-1] = {**rows[-1], "DILUTED_EPS": None, "BASIC_EPS": 2.0}

        self.assertAlmostEqual(parse_us_quarterly(rows)[-1].eps_ttm, 5.3)

    def test_rows_out_of_order_are_still_summed_chronologically(self) -> None:
        shuffled = list(reversed(self._rows()))

        self.assertAlmostEqual(parse_us_quarterly(shuffled)[-1].eps_ttm, 5.0)

    def test_an_announcement_before_the_period_end_is_refused(self) -> None:
        rows = self._rows()
        rows[-1] = {**rows[-1], "NOTICE_DATE": "2020-01-01 00:00:00"}

        item = parse_us_quarterly(rows)[-1]

        # A filing cannot be public five years before the quarter it covers.
        self.assertGreater(item.effective_date, item.period_end)

    def test_unparseable_rows_are_skipped(self) -> None:
        self.assertEqual(parse_us_quarterly([{"REPORT_DATE": "", "BASIC_EPS": "x"}]), [])


class HkAnnualTests(unittest.TestCase):
    def _rows(self) -> list[dict]:
        # Shape taken from stock_financial_hk_analysis_indicator_em(indicator="年度").
        return [
            {"REPORT_DATE": "2025-12-31 00:00:00", "BPS": 126.93, "BASIC_EPS": 24.75,
             "DILUTED_EPS": 24.15, "EPS_TTM": 24.65, "CURRENCY": "CNY"},
            {"REPORT_DATE": "2024-12-31 00:00:00", "BPS": 107.07, "BASIC_EPS": 20.94,
             "DILUTED_EPS": 20.49, "EPS_TTM": 21.04, "CURRENCY": "CNY"},
        ]

    def test_reads_earnings_and_book_value(self) -> None:
        items = parse_hk_annual(self._rows())

        latest = items[-1]
        self.assertAlmostEqual(latest.eps_ttm, 24.65)
        self.assertAlmostEqual(latest.bps, 126.93)
        self.assertEqual(latest.currency, "CNY")

    def test_the_publication_lag_is_applied_and_flagged(self) -> None:
        latest = parse_hk_annual(self._rows())[-1]

        # The feed gives no announcement date, so the lag is an assumption and must say so.
        self.assertTrue(latest.effective_date_estimated)
        self.assertEqual(
            (latest.effective_date - latest.period_end).days, HK_PUBLICATION_LAG_DAYS)

    def test_rows_come_back_oldest_first(self) -> None:
        items = parse_hk_annual(self._rows())

        self.assertEqual([i.period_end.year for i in items], [2024, 2025])

    def test_a_row_with_neither_earnings_nor_book_value_is_skipped(self) -> None:
        self.assertEqual(parse_hk_annual([{"REPORT_DATE": "2025-12-31 00:00:00"}]), [])


class ReconstructionTests(unittest.TestCase):
    def _fundamental(self, effective: date, eps: float | None = 5.0,
                     bps: float | None = 20.0) -> PerShareFundamental:
        return PerShareFundamental(
            period_end=date(2025, 12, 31), effective_date=effective, eps_ttm=eps, bps=bps)

    def test_divides_price_by_the_published_per_share_figures(self) -> None:
        points = reconstruct_valuation(
            [bar(date(2026, 3, 2), 100.0)], [self._fundamental(date(2026, 1, 29))])

        point = points[0]
        self.assertAlmostEqual(point.pe, 20.0)
        self.assertAlmostEqual(point.pb, 5.0)

    def test_days_before_the_report_was_published_get_no_multiple(self) -> None:
        bars = [bar(date(2026, 1, 5), 100.0), bar(date(2026, 2, 5), 100.0)]

        points = reconstruct_valuation(bars, [self._fundamental(date(2026, 1, 29))])

        # Using the report on 2026-01-05 would price January against unannounced earnings.
        self.assertEqual([p.as_of for p in points], [date(2026, 2, 5)])

    def test_a_newer_report_takes_over_from_its_announcement_date(self) -> None:
        old = PerShareFundamental(date(2024, 12, 31), date(2025, 1, 29), eps_ttm=4.0, bps=10.0)
        new = PerShareFundamental(date(2025, 12, 31), date(2026, 1, 29), eps_ttm=5.0, bps=20.0)
        bars = [bar(date(2026, 1, 28), 100.0), bar(date(2026, 1, 29), 100.0)]

        points = reconstruct_valuation(bars, [old, new])

        self.assertAlmostEqual(points[0].pe, 25.0)  # still on the old EPS
        self.assertAlmostEqual(points[1].pe, 20.0)  # switches on the announcement day

    def test_a_loss_making_period_has_no_pe_rather_than_a_negative_one(self) -> None:
        points = reconstruct_valuation(
            [bar(date(2026, 3, 2), 100.0)], [self._fundamental(date(2026, 1, 29), eps=-2.0)])

        # A negative PE is undefined, not cheap; it must not enter a percentile distribution.
        self.assertIsNone(points[0].pe)
        self.assertAlmostEqual(points[0].pb, 5.0)

    def test_a_missing_book_value_leaves_pb_empty_without_losing_pe(self) -> None:
        points = reconstruct_valuation(
            [bar(date(2026, 3, 2), 100.0)], [self._fundamental(date(2026, 1, 29), bps=None)])

        self.assertAlmostEqual(points[0].pe, 20.0)
        self.assertIsNone(points[0].pb)

    def test_zero_earnings_produce_no_multiple(self) -> None:
        points = reconstruct_valuation(
            [bar(date(2026, 3, 2), 100.0)], [self._fundamental(date(2026, 1, 29), eps=0.0)])

        self.assertIsNone(points[0].pe)

    def test_bars_out_of_order_are_handled(self) -> None:
        bars = [bar(date(2026, 3, 2), 100.0), bar(date(2026, 2, 2), 50.0)]

        points = reconstruct_valuation(bars, [self._fundamental(date(2026, 1, 29))])

        self.assertEqual([p.as_of for p in points], [date(2026, 2, 2), date(2026, 3, 2)])
        self.assertAlmostEqual(points[0].pe, 10.0)

    def test_no_bars_or_no_fundamentals_yields_nothing(self) -> None:
        self.assertEqual(reconstruct_valuation([], [self._fundamental(date(2026, 1, 29))]), [])
        self.assertEqual(reconstruct_valuation([bar(date(2026, 3, 2), 10.0)], []), [])

    def test_a_daily_series_is_long_enough_for_a_percentile(self) -> None:
        from caishenfolio_core.research.valuation import MIN_HISTORY_POINTS

        bars = [bar(date(2026, 1, 1), 100.0)]
        bars += [bar(date(2026, 2, 1), 100.0 + i) for i in range(MIN_HISTORY_POINTS + 10)]

        points = reconstruct_valuation(bars, [self._fundamental(date(2025, 12, 1))])

        # The denominator only steps quarterly, but the multiple moves with the price every day,
        # which is exactly what the vendor series for A-shares also does.
        self.assertGreaterEqual(len(points), MIN_HISTORY_POINTS)


class BarSourceWiringTests(unittest.TestCase):
    def test_the_chain_lends_its_bar_channel_to_children_that_need_prices(self) -> None:
        from caishenfolio_core.market.factory import create_market_provider

        chain = create_market_provider("auto", use_cache=False)

        akshare = next(c for c in chain.children if getattr(c, "PROVIDER_CODE", "") == "akshare")
        # Reconstructed HK/US valuation needs prices, and akshare's own HK bar endpoint is
        # commonly blocked where yfinance is not.
        self.assertIs(akshare.bar_source, chain)

    def test_a_standalone_provider_falls_back_to_its_own_bars(self) -> None:
        from caishenfolio_core.market.akshare_provider import AkshareMarketDataProvider

        self.assertIsNone(AkshareMarketDataProvider().bar_source)


class MethodDescriptionTests(unittest.TestCase):
    def test_says_the_number_was_computed_not_quoted(self) -> None:
        text = describe_method([PerShareFundamental(
            date(2025, 12, 31), date(2026, 1, 29), eps_ttm=5.0)])

        self.assertIn("推算", text)

    def test_an_assumed_announcement_date_is_disclosed(self) -> None:
        text = describe_method([PerShareFundamental(
            date(2025, 12, 31), date(2026, 3, 31), eps_ttm=5.0, effective_date_estimated=True)])

        self.assertIn("估算", text)
        self.assertIn("偏差", text)

    def test_a_reported_announcement_date_says_no_lookahead(self) -> None:
        text = describe_method([PerShareFundamental(
            date(2025, 12, 31), date(2026, 1, 29), eps_ttm=5.0)])

        self.assertIn("公告日", text)

    def test_no_fundamentals_means_no_claim(self) -> None:
        self.assertEqual(describe_method([]), "")


if __name__ == "__main__":
    unittest.main()
