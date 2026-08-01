from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.market.eastmoney_fund_provider import (
    EastmoneyFundNavProvider,
    parse_nav_series,
)
from caishenfolio_core.market.factory import create_market_provider

# Beijing-midnight stamps, the form the real payload uses.
_D0 = 1751212800000  # 2025-06-30
_D1 = 1751299200000  # 2025-07-01
_D2 = 1751385600000  # 2025-07-02

_PAYLOAD = (
    'var fS_name = "示例基金";'
    'var Data_netWorthTrend = ['
    '{"x":%d,"y":1.2000,"equityReturn":0.5,"unitMoney":""},'
    '{"x":%d,"y":1.2120,"equityReturn":1.0,"unitMoney":""},'
    '{"x":%d,"y":1.2000,"equityReturn":-0.99,"unitMoney":"每份派现金0.0100元"}'
    "];"
    "var Data_ACWorthTrend = [[%d,2.4000],[%d,2.4120],[%d,2.4120]];"
    "var Data_grandTotal = [];"
) % (_D0, _D1, _D2, _D0, _D1, _D2)

_WINDOW = (date(2025, 1, 1), date(2025, 12, 31))


class ParsingTests(unittest.TestCase):
    def test_reads_the_series_in_date_order(self) -> None:
        result = parse_nav_series(_PAYLOAD, "FUND:000001", *_WINDOW)

        self.assertTrue(result.ok, result.error)
        days = [p.as_of for p in result.data]
        self.assertEqual(days, [date(2025, 6, 30), date(2025, 7, 1), date(2025, 7, 2)])

    def test_carries_nav_cumulative_nav_and_daily_return(self) -> None:
        point = parse_nav_series(_PAYLOAD, "FUND:000001", *_WINDOW).data[1]

        self.assertEqual(point.nav, 1.2120)
        self.assertEqual(point.accumulated_nav, 2.4120)
        # equityReturn is a percentage upstream; the model stores a ratio.
        self.assertAlmostEqual(point.daily_return, 0.01)
        self.assertEqual(point.currency, "CNY")
        self.assertEqual(point.provenance["synthetic"], "false")

    def test_the_window_is_honoured(self) -> None:
        result = parse_nav_series(_PAYLOAD, "FUND:000001", date(2025, 7, 1), date(2025, 7, 1))

        self.assertTrue(result.ok, result.error)
        self.assertEqual([p.as_of for p in result.data], [date(2025, 7, 1)])

    def test_a_day_without_a_published_nav_is_dropped_not_zeroed(self) -> None:
        payload = (
            "var Data_netWorthTrend = ["
            '{"x":%d,"y":1.2000,"equityReturn":0.5},'
            '{"x":%d,"y":0,"equityReturn":null},'
            '{"x":%d,"y":null,"equityReturn":null}'
            "];"
        ) % (_D0, _D1, _D2)

        result = parse_nav_series(payload, "FUND:000001", *_WINDOW)

        self.assertTrue(result.ok, result.error)
        self.assertEqual([p.nav for p in result.data], [1.2000])

    def test_missing_cumulative_series_is_tolerated(self) -> None:
        payload = 'var Data_netWorthTrend = [{"x":%d,"y":1.2000,"equityReturn":0.5}];' % _D0

        point = parse_nav_series(payload, "FUND:000001", *_WINDOW).data[0]

        self.assertIsNone(point.accumulated_nav)

    def test_a_payload_without_the_series_fails_closed(self) -> None:
        result = parse_nav_series("<html>404</html>", "FUND:000001", *_WINDOW)

        self.assertFalse(result.ok)
        self.assertIn("parse_error", result.warnings)
        self.assertIn("fail_closed", result.warnings)

    def test_a_malformed_series_fails_closed(self) -> None:
        result = parse_nav_series("var Data_netWorthTrend = [{oops];", "FUND:000001", *_WINDOW)

        self.assertFalse(result.ok)
        self.assertIn("fail_closed", result.warnings)

    def test_an_empty_window_is_a_failure_not_an_empty_success(self) -> None:
        result = parse_nav_series(_PAYLOAD, "FUND:000001", date(2020, 1, 1), date(2020, 12, 31))

        self.assertFalse(result.ok)
        self.assertIn("empty_window", result.warnings)


class ProviderContractTests(unittest.TestCase):
    def test_rejects_non_fund_symbols_before_any_network_call(self) -> None:
        result = EastmoneyFundNavProvider().nav_series(
            "SSE:600000", date(2025, 1, 1), date(2025, 2, 1))

        self.assertFalse(result.ok)
        self.assertIn("unsupported_symbol", result.warnings)

    def test_reversed_dates_are_refused(self) -> None:
        result = EastmoneyFundNavProvider().nav_series(
            "FUND:000001", date(2025, 2, 1), date(2025, 1, 1))

        self.assertFalse(result.ok)

    def test_bars_are_out_of_scope_rather_than_faked(self) -> None:
        result = EastmoneyFundNavProvider().historical_bars(
            "FUND:000001", date(2025, 1, 1), date(2025, 2, 1))

        self.assertFalse(result.ok)
        self.assertIn("unsupported_capability", result.warnings)

    def test_selectable_by_name_and_present_in_the_auto_chain(self) -> None:
        self.assertIsInstance(
            create_market_provider("eastmoney_fund", use_cache=False), EastmoneyFundNavProvider)

        auto = create_market_provider("auto", use_cache=False)
        codes = [getattr(c, "PROVIDER_CODE", "") for c in auto.children]
        self.assertIn("eastmoney_fund", codes)
        # akshare stays the primary; this is the fallback when its endpoint moves.
        self.assertLess(codes.index("akshare"), codes.index("eastmoney_fund"))


if __name__ == "__main__":
    unittest.main()
