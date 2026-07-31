from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.market.factory import create_market_provider
from caishenfolio_core.market.tencent_provider import (
    TencentQuoteProvider,
    parse_quote,
    to_tencent_ticker,
)

# A real payload shape, trimmed: flag ~ name ~ code ~ price ~ prev close ~ open ...
_PAYLOAD = (
    'v_sh600000="1~浦发银行~600000~10.50~10.45~10.48~123456~61728~61728~10.50~'
    '32~10.49~15~10.48~9~10.47~7~10.46~5~10.51~11~10.52~9~10.53~6~10.54~4~10.55~3~'
    '20260731150300~0.05~0.48~10.60~10.40~10.50/123456/129600000~123456~12960~0.42~'
    '4.85~~10.60~10.40~1.91~3081.00~3081.00~0.71~11.50~9.40~0.63~-6.25~-1.28~2.16~29343000000";'
)


class TickerMappingTests(unittest.TestCase):
    def test_maps_each_cn_venue_to_its_prefix(self) -> None:
        self.assertEqual(to_tencent_ticker("SSE", "600000"), "sh600000")
        self.assertEqual(to_tencent_ticker("SZSE", "000001"), "sz000001")
        self.assertEqual(to_tencent_ticker("BSE", "830799"), "bj830799")

    def test_pads_short_codes(self) -> None:
        self.assertEqual(to_tencent_ticker("SZSE", "1"), "sz000001")

    def test_falls_back_to_the_shared_classifier_for_unknown_venues(self) -> None:
        # 113*** is an SSE convertible bond, which the naive "1 means Shenzhen" rule gets wrong.
        self.assertEqual(to_tencent_ticker("", "113050"), "sh113050")
        self.assertEqual(to_tencent_ticker("", "159915"), "sz159915")

    def test_rejects_venues_this_source_does_not_serve(self) -> None:
        self.assertIsNone(to_tencent_ticker("NASDAQ", "AAPL"))
        self.assertIsNone(to_tencent_ticker("SSE", "ABC"))
        self.assertIsNone(to_tencent_ticker("TSE", "7203"))
        self.assertIsNone(to_tencent_ticker("FUND", "110022"))

    def test_a_foreign_venue_never_falls_through_to_a_cn_code(self) -> None:
        # HKEX:00700 must not become sz000700 — that would price Tencent as a Shenzhen stock.
        self.assertIsNone(to_tencent_ticker("HKEX", "00700"))
        self.assertIsNone(to_tencent_ticker("HKEX", "700"))


class PayloadParsingTests(unittest.TestCase):
    def test_reads_price_name_and_date(self) -> None:
        result = parse_quote(_PAYLOAD, "SSE:600000")

        self.assertTrue(result.ok, msg=result.error)
        assert result.data is not None
        self.assertEqual(result.data.price, 10.50)
        self.assertEqual(result.data.currency, "CNY")
        self.assertEqual(result.data.as_of, date(2026, 7, 31))
        self.assertEqual(result.data.provenance["name"], "浦发银行")
        self.assertEqual(result.data.provenance["synthetic"], "false")

    def test_a_suspended_instrument_quoting_zero_fails_closed(self) -> None:
        result = parse_quote('v_sh600000="1~浦发银行~600000~0.00~10.45";', "SSE:600000")

        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("fail_closed", result.warnings)
        self.assertIn("停牌", result.error or "")

    def test_an_empty_or_malformed_payload_fails_closed(self) -> None:
        for payload in ['v_pv_none_match="";', "", "garbage", 'v_sh600000="1~名称";']:
            with self.subTest(payload=payload):
                result = parse_quote(payload, "SSE:600000")
                self.assertFalse(result.ok)
                self.assertIn("fail_closed", result.warnings)

    def test_a_non_numeric_price_fails_closed(self) -> None:
        result = parse_quote('v_sh600000="1~浦发银行~600000~--~10.45";', "SSE:600000")

        self.assertFalse(result.ok)
        self.assertIn("parse_error", result.warnings)

    def test_falls_back_to_today_when_no_timestamp_is_present(self) -> None:
        result = parse_quote('v_sh600000="1~浦发银行~600000~10.50~10.45";', "SSE:600000")

        self.assertTrue(result.ok)
        assert result.data is not None
        self.assertEqual(result.data.as_of, date.today())


class ProviderContractTests(unittest.TestCase):
    def test_is_always_ready_because_it_has_no_dependencies(self) -> None:
        self.assertTrue(TencentQuoteProvider().ready)

    def test_rejects_non_cn_symbols_before_any_network_call(self) -> None:
        result = TencentQuoteProvider().latest_quote("NASDAQ:AAPL")

        self.assertFalse(result.ok)
        self.assertIn("unsupported_exchange", result.warnings)

    def test_rejects_a_malformed_symbol(self) -> None:
        result = TencentQuoteProvider().latest_quote("not-a-symbol")

        self.assertFalse(result.ok)
        self.assertIn("fail_closed", result.warnings)

    def test_bars_are_not_this_source_s_job(self) -> None:
        result = TencentQuoteProvider().historical_bars("SSE:600000", date(2026, 1, 1), date(2026, 2, 1))

        self.assertFalse(result.ok)
        self.assertIn("unsupported_capability", result.warnings)

    def test_selectable_by_name_and_present_in_the_auto_chain(self) -> None:
        self.assertIsInstance(create_market_provider("tencent", use_cache=False), TencentQuoteProvider)

        auto = create_market_provider("auto", use_cache=False)
        codes = [getattr(child, "PROVIDER_CODE", "") for child in auto.children]
        self.assertIn("tencent", codes)
        # It answers when akshare cannot, so it must not be last behind the key-based sources.
        self.assertLess(codes.index("tencent"), codes.index("tushare"))


if __name__ == "__main__":
    unittest.main()
