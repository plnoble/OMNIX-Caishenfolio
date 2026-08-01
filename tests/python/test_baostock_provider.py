from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.data.bar_interval import BarInterval
from caishenfolio_core.data.models import Adjustment
from caishenfolio_core.market.baostock_provider import (
    BaostockMarketDataProvider,
    _rows_to_bars,
    to_baostock_code,
)
from caishenfolio_core.market.factory import create_market_provider


class CodeMappingTests(unittest.TestCase):
    def test_maps_shanghai_and_shenzhen(self) -> None:
        self.assertEqual(to_baostock_code("SSE", "600000"), "sh.600000")
        self.assertEqual(to_baostock_code("SZSE", "000001"), "sz.000001")
        self.assertEqual(to_baostock_code("SSE", "688981"), "sh.688981")

    def test_pads_short_codes(self) -> None:
        self.assertEqual(to_baostock_code("SZSE", "1"), "sz.000001")

    def test_a_bare_code_routes_through_the_shared_classifier(self) -> None:
        # 113*** is an SSE convertible bond, which a naive "1 means Shenzhen" rule gets wrong.
        self.assertEqual(to_baostock_code("", "113050"), "sh.113050")
        self.assertEqual(to_baostock_code("", "159915"), "sz.159915")

    def test_a_foreign_venue_never_falls_through_to_a_cn_code(self) -> None:
        for exchange, code in [("HKEX", "00700"), ("NASDAQ", "AAPL"), ("TSE", "7203")]:
            with self.subTest(exchange=exchange):
                self.assertIsNone(to_baostock_code(exchange, code))

    def test_beijing_is_not_covered(self) -> None:
        self.assertIsNone(to_baostock_code("BSE", "830799"))


class RowParsingTests(unittest.TestCase):
    def _row(self, day: str, close: str = "10.5") -> list[str]:
        return [day, "sh.600000", "10.4", "10.6", "10.3", close, "1000000", "10500000"]

    def test_reads_a_well_formed_row(self) -> None:
        bars = _rows_to_bars([self._row("2026-07-31")], "SSE:600000",
                             Adjustment.RAW, "baostock", "sh.600000")

        bar = bars[0]
        self.assertEqual(bar.timestamp_utc.date(), date(2026, 7, 31))
        self.assertEqual(bar.close, 10.5)
        self.assertEqual(bar.currency, "CNY")
        self.assertEqual(bar.provenance["synthetic"], "false")
        self.assertEqual(bar.amount, 10500000.0)

    def test_a_suspended_day_is_skipped_not_zero_priced(self) -> None:
        # BaoStock returns empty price fields for suspended sessions.
        rows = [self._row("2026-07-30"), ["2026-07-31", "sh.600000", "", "", "", "", "", ""]]

        bars = _rows_to_bars(rows, "SSE:600000", Adjustment.RAW, "baostock", "sh.600000")

        self.assertEqual(len(bars), 1)
        self.assertEqual(bars[0].timestamp_utc.date(), date(2026, 7, 30))

    def test_a_zero_close_is_not_a_price(self) -> None:
        bars = _rows_to_bars([self._row("2026-07-31", close="0")], "SSE:600000",
                             Adjustment.RAW, "baostock", "sh.600000")
        self.assertEqual(bars, [])

    def test_short_or_broken_rows_are_skipped(self) -> None:
        rows = [["2026-07-31"], ["not-a-date", "sh.600000", "1", "1", "1", "1", "1", "1"]]
        self.assertEqual(_rows_to_bars(rows, "SSE:600000", Adjustment.RAW, "b", "sh.600000"), [])


class ProviderContractTests(unittest.TestCase):
    def test_rejects_unsupported_venues_before_any_network_call(self) -> None:
        provider = BaostockMarketDataProvider()
        provider._bs = object()  # type: ignore[attr-defined]

        result = provider.historical_bars(
            "NASDAQ:AAPL", date(2026, 1, 1), date(2026, 2, 1))

        self.assertFalse(result.ok)
        self.assertIn("unsupported_exchange", result.warnings)

    def test_rejects_intraday_intervals(self) -> None:
        provider = BaostockMarketDataProvider()
        provider._bs = object()  # type: ignore[attr-defined]

        result = provider.historical_bars(
            "SSE:600000", date(2026, 1, 1), date(2026, 2, 1), interval=BarInterval.M5)

        self.assertFalse(result.ok)
        self.assertIn("unsupported_interval", result.warnings)

    def test_missing_library_fails_closed_with_an_install_hint(self) -> None:
        provider = BaostockMarketDataProvider()
        provider._bs = None  # type: ignore[attr-defined]

        result = provider.historical_bars("SSE:600000", date(2026, 1, 1), date(2026, 2, 1))

        self.assertFalse(result.ok)
        self.assertIn("pip install baostock", result.error or "")
        self.assertIn("fail_closed", result.warnings)

    def test_reversed_dates_are_refused(self) -> None:
        provider = BaostockMarketDataProvider()
        provider._bs = object()  # type: ignore[attr-defined]

        result = provider.historical_bars("SSE:600000", date(2026, 2, 1), date(2026, 1, 1))
        self.assertFalse(result.ok)

    def test_selectable_by_name_and_present_in_the_auto_chain(self) -> None:
        self.assertIsInstance(
            create_market_provider("baostock", use_cache=False), BaostockMarketDataProvider)

        auto = create_market_provider("auto", use_cache=False)
        codes = [getattr(c, "PROVIDER_CODE", "") for c in auto.children]
        self.assertIn("baostock", codes)
        # It answers without a key, so it must come before the key-based sources.
        self.assertLess(codes.index("baostock"), codes.index("tushare"))


if __name__ == "__main__":
    unittest.main()
