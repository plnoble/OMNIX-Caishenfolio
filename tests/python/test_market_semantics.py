from __future__ import annotations

import unittest

from caishenfolio_core.data.markets import (
    AssetClass,
    MarketRegion,
    canonical_exchange,
    classify_cn_code,
    cn_exchange_for_code,
    fx_pair,
    fx_symbol,
    is_nav_priced,
    parse_asset_class,
    parse_region,
    quote_currency,
    region_of,
    resolve_exchange,
    yahoo_ticker,
)
from caishenfolio_core.data.models import SymbolId


class ExchangeRegistryTests(unittest.TestCase):
    def test_region_and_currency_per_venue(self) -> None:
        cases = [
            ("SSE:600000", MarketRegion.CN, "CNY"),
            ("SZSE:000001", MarketRegion.CN, "CNY"),
            ("HKEX:00700", MarketRegion.HK, "HKD"),
            ("NASDAQ:AAPL", MarketRegion.US, "USD"),
            ("TSE:7203", MarketRegion.JP, "JPY"),
            ("FUND:110022", MarketRegion.CN, "CNY"),
        ]
        for symbol, region, currency in cases:
            with self.subTest(symbol=symbol):
                self.assertEqual(region_of(symbol), region)
                self.assertEqual(quote_currency(symbol), currency)

    def test_aliases_collapse_to_one_identity(self) -> None:
        for raw, expected in [
            ("SH:600000", "SSE:600000"),
            ("SZ:000001", "SZSE:000001"),
            ("HK:00700", "HKEX:00700"),
            ("OF:110022", "FUND:110022"),
            ("TYO:7203", "TSE:7203"),
        ]:
            with self.subTest(raw=raw):
                self.assertEqual(SymbolId.parse(raw).normalized().value, expected)

    def test_unknown_venue_is_preserved_not_dropped(self) -> None:
        self.assertIsNone(resolve_exchange("LSE"))
        self.assertIsNone(canonical_exchange("LSE"))
        self.assertEqual(SymbolId.parse("LSE:VOD").normalized().value, "LSE:VOD")
        self.assertEqual(region_of("LSE:VOD"), MarketRegion.GLOBAL)
        self.assertIsNone(quote_currency("LSE:VOD"))


class FxTests(unittest.TestCase):
    def test_pair_carries_quote_currency(self) -> None:
        self.assertEqual(fx_pair("FX:USDCNY"), ("USD", "CNY"))
        self.assertEqual(quote_currency("FX:USDCNY"), "CNY")
        self.assertEqual(fx_symbol("usd", "cny"), "FX:USDCNY")
        self.assertEqual(region_of("FX:USDCNY"), MarketRegion.GLOBAL)

    def test_rejects_malformed_pairs(self) -> None:
        for raw in ["FX:XXXYYY", "FX:USD", "NASDAQ:AAPL"]:
            with self.subTest(raw=raw):
                self.assertIsNone(fx_pair(raw))


class VendorTickerTests(unittest.TestCase):
    def test_yahoo_ticker_per_venue(self) -> None:
        self.assertEqual(yahoo_ticker("TSE:7203"), "7203.T")
        self.assertEqual(yahoo_ticker("HKEX:700"), "0700.HK")
        self.assertEqual(yahoo_ticker("SSE:600000"), "600000.SS")
        self.assertEqual(yahoo_ticker("SZSE:000001"), "000001.SZ")
        self.assertEqual(yahoo_ticker("NASDAQ:AAPL"), "AAPL")
        self.assertEqual(yahoo_ticker("FX:USDCNY"), "USDCNY=X")

    def test_off_exchange_funds_have_no_yahoo_ticker(self) -> None:
        self.assertIsNone(yahoo_ticker("FUND:110022"))
        self.assertIsNone(yahoo_ticker("LSE:VOD"))


class CnCodeClassificationTests(unittest.TestCase):
    def test_venue_guess_puts_convertible_bonds_on_the_right_exchange(self) -> None:
        # 110***/113*** are SSE convertible bonds, 159*** are SZSE ETFs — both start with "1".
        self.assertEqual(cn_exchange_for_code("113050"), "SSE")
        self.assertEqual(cn_exchange_for_code("110059"), "SSE")
        self.assertEqual(cn_exchange_for_code("159915"), "SZSE")
        self.assertEqual(cn_exchange_for_code("128036"), "SZSE")
        self.assertEqual(cn_exchange_for_code("600000"), "SSE")
        self.assertEqual(cn_exchange_for_code("000001"), "SZSE")
        self.assertEqual(cn_exchange_for_code("830799"), "BSE")

    def test_asset_class_per_code(self) -> None:
        cases = [
            ("600000", "SSE", AssetClass.EQUITY),
            ("688981", "SSE", AssetClass.EQUITY),
            ("510300", "SSE", AssetClass.ETF),
            ("518880", "SSE", AssetClass.ETF),
            ("113050", "SSE", AssetClass.CONVERTIBLE_BOND),
            ("019547", "SSE", AssetClass.BOND),
            ("000001", "SZSE", AssetClass.EQUITY),
            ("300750", "SZSE", AssetClass.EQUITY),
            ("159915", "SZSE", AssetClass.ETF),
            ("128036", "SZSE", AssetClass.CONVERTIBLE_BOND),
            ("110022", "FUND", AssetClass.MUTUAL_FUND),
        ]
        for code, exchange, expected in cases:
            with self.subTest(code=code, exchange=exchange):
                self.assertEqual(classify_cn_code(code, exchange), expected)

    def test_same_code_differs_by_exchange(self) -> None:
        # SSE 000001 is 上证指数; SZSE 000001 is 平安银行.
        self.assertEqual(classify_cn_code("000001", "SSE"), AssetClass.INDEX)
        self.assertEqual(classify_cn_code("000001", "SZSE"), AssetClass.EQUITY)


class LegacyParsingTests(unittest.TestCase):
    def test_legacy_market_strings_still_parse(self) -> None:
        for raw, expected in [
            ("ashare", MarketRegion.CN),
            ("etf", MarketRegion.CN),
            ("hk", MarketRegion.HK),
            ("us", MarketRegion.US),
            ("jp", MarketRegion.JP),
        ]:
            with self.subTest(raw=raw):
                self.assertEqual(parse_region(raw), expected)

    def test_legacy_asset_names_still_parse(self) -> None:
        self.assertEqual(parse_asset_class("fund"), AssetClass.MUTUAL_FUND)
        self.assertEqual(parse_asset_class("mutual_fund"), AssetClass.MUTUAL_FUND)
        self.assertEqual(parse_asset_class("cb"), AssetClass.CONVERTIBLE_BOND)
        self.assertIsNone(parse_asset_class("nonsense"))

    def test_only_mutual_funds_are_nav_priced(self) -> None:
        self.assertTrue(is_nav_priced(AssetClass.MUTUAL_FUND))
        self.assertFalse(is_nav_priced(AssetClass.ETF))
        self.assertFalse(is_nav_priced(AssetClass.EQUITY))


if __name__ == "__main__":
    unittest.main()
