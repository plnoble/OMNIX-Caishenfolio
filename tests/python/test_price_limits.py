from __future__ import annotations

import unittest
from datetime import datetime, timedelta, timezone

from caishenfolio_core.data.markets import is_st_name, price_limit_pct
from caishenfolio_core.research.backtest import CostModel, ma_cross_backtest


class PriceLimitTests(unittest.TestCase):
    def test_main_board_is_ten_percent(self) -> None:
        for symbol in ("SSE:600000", "SSE:601398", "SZSE:000001", "SZSE:002415"):
            with self.subTest(symbol=symbol):
                self.assertEqual(price_limit_pct(symbol), 0.10)

    def test_growth_boards_are_twenty_percent(self) -> None:
        # A 10% cap on these invents limit days that never happened.
        for symbol in ("SZSE:300750", "SZSE:301029", "SSE:688981", "SSE:689009"):
            with self.subTest(symbol=symbol):
                self.assertEqual(price_limit_pct(symbol), 0.20)

    def test_beijing_exchange_is_thirty_percent(self) -> None:
        for symbol in ("BSE:920819", "BSE:430047", "BSE:830799", "BSE:872925"):
            with self.subTest(symbol=symbol):
                self.assertEqual(price_limit_pct(symbol), 0.30)

    def test_st_names_are_five_percent_on_the_main_board(self) -> None:
        self.assertEqual(price_limit_pct("SSE:600000", "ST某某"), 0.05)
        self.assertEqual(price_limit_pct("SZSE:000001", "*ST某某"), 0.05)

    def test_st_on_a_growth_board_stays_twenty_percent(self) -> None:
        # The tighter ST cap does not apply to ChiNext or STAR listings.
        self.assertEqual(price_limit_pct("SZSE:300750", "ST某某"), 0.20)
        self.assertEqual(price_limit_pct("SSE:688981", "*ST某某"), 0.20)

    def test_foreign_listings_have_no_daily_cap(self) -> None:
        # Enforcing one here skips signals that were perfectly tradable.
        for symbol in ("HKEX:00700", "NASDAQ:AAPL", "NYSE:SPY", "TSE:7203"):
            with self.subTest(symbol=symbol):
                self.assertIsNone(price_limit_pct(symbol))

    def test_funds_and_bonds_have_no_fixed_cap(self) -> None:
        self.assertIsNone(price_limit_pct("FUND:110022"))
        self.assertIsNone(price_limit_pct("SSE:113050"))

    def test_unknown_venues_yield_no_cap(self) -> None:
        self.assertIsNone(price_limit_pct("LSE:VOD"))
        self.assertIsNone(price_limit_pct("nonsense"))

    def test_st_detection(self) -> None:
        self.assertTrue(is_st_name("ST某某"))
        self.assertTrue(is_st_name("*ST某某"))
        self.assertTrue(is_st_name("st某某"))
        self.assertFalse(is_st_name("某某股份"))
        self.assertFalse(is_st_name(None))


def _bars(closes: list[float]) -> list[dict]:
    start = datetime(2024, 1, 1, tzinfo=timezone.utc)
    return [
        {"timestamp_utc": (start + timedelta(days=i)).isoformat(), "close": c}
        for i, c in enumerate(closes)
    ]


class BacktestLimitTests(unittest.TestCase):
    def _closes(self) -> list[float]:
        # Flat, then a strong run so the fast MA crosses up on a large single-day gain.
        return [10.0] * 25 + [10.0 * (1.15 ** (i + 1)) for i in range(20)]

    def test_a_growth_board_stock_is_not_capped_at_ten_percent(self) -> None:
        closes = self._closes()
        main = ma_cross_backtest(_bars(closes), symbol="SSE:600000", fast=5, slow=20)
        growth = ma_cross_backtest(_bars(closes), symbol="SZSE:300750", fast=5, slow=20)

        self.assertEqual(main.cost_model["limit_up_pct"], 0.10)
        self.assertEqual(growth.cost_model["limit_up_pct"], 0.20)
        # 15% daily moves are limit days on the main board but ordinary on ChiNext.
        self.assertGreater(main.skipped_signals, growth.skipped_signals)

    def test_a_us_listing_has_the_limit_rule_switched_off(self) -> None:
        result = ma_cross_backtest(_bars(self._closes()), symbol="NASDAQ:AAPL", fast=5, slow=20)

        self.assertFalse(result.cost_model["enforce_limit"])
        self.assertEqual(result.skipped_signals, 0)

    def test_an_st_name_tightens_the_cap(self) -> None:
        result = ma_cross_backtest(
            _bars(self._closes()), symbol="SSE:600000", fast=5, slow=20, name="ST某某")

        self.assertEqual(result.cost_model["limit_up_pct"], 0.05)

    def test_disabling_the_rule_is_still_respected(self) -> None:
        result = ma_cross_backtest(
            _bars(self._closes()), symbol="SZSE:300750", fast=5, slow=20,
            costs=CostModel(enforce_limit=False))

        self.assertFalse(result.cost_model["enforce_limit"])
        self.assertEqual(result.skipped_signals, 0)


if __name__ == "__main__":
    unittest.main()
