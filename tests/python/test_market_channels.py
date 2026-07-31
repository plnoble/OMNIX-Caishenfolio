from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.data.models import AssetClass, FxQuote, NavPoint, ProviderResult, Quote
from caishenfolio_core.market.akshare_provider import AkshareMarketDataProvider
from caishenfolio_core.market.base import supports_fx, supports_nav, supports_quotes
from caishenfolio_core.market.composite import CompositeMarketDataProvider
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.market.yfinance_provider import _to_yahoo_symbol
from caishenfolio_core.data.models import SymbolId
from caishenfolio_core.server.app import AnalyticsApp, dispatch

START = date(2026, 1, 5)
END = date(2026, 1, 30)


class CapabilityDiscoveryTests(unittest.TestCase):
    def test_fixture_implements_all_three_channels(self) -> None:
        provider = FixtureMarketDataProvider()
        self.assertTrue(supports_quotes(provider))
        self.assertTrue(supports_nav(provider))
        self.assertTrue(supports_fx(provider))

    def test_real_providers_declare_their_channels(self) -> None:
        akshare = AkshareMarketDataProvider()
        self.assertTrue(supports_quotes(akshare))
        self.assertTrue(supports_nav(akshare))
        self.assertTrue(supports_fx(akshare))


class QuoteChannelTests(unittest.TestCase):
    def test_quote_covers_every_market_in_scope(self) -> None:
        provider = FixtureMarketDataProvider()
        for symbol, currency in [
            ("SSE:600000", "CNY"),
            ("HKEX:00700", "HKD"),
            ("NASDAQ:AAPL", "USD"),
            ("TSE:7203", "JPY"),
            ("FUND:110022", "CNY"),
            ("SSE:113050", "CNY"),
        ]:
            with self.subTest(symbol=symbol):
                result = provider.latest_quote(symbol)
                self.assertTrue(result.ok, msg=result.error)
                assert isinstance(result.data, Quote)
                self.assertEqual(result.data.symbol, symbol)
                self.assertEqual(result.data.currency, currency)
                self.assertGreater(result.data.price, 0)

    def test_quote_resolves_venue_aliases(self) -> None:
        result = FixtureMarketDataProvider().latest_quote("SH:600000")
        self.assertTrue(result.ok)
        assert result.data is not None
        self.assertEqual(result.data.symbol, "SSE:600000")

    def test_unknown_symbol_fails_closed(self) -> None:
        result = FixtureMarketDataProvider().latest_quote("NASDAQ:NOPE")
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("fail_closed", result.warnings)


class NavChannelTests(unittest.TestCase):
    def test_fund_nav_series_has_no_fabricated_ohlc_fields(self) -> None:
        result = FixtureMarketDataProvider().nav_series("FUND:110022", START, END)

        self.assertTrue(result.ok, msg=result.error)
        assert result.data is not None
        self.assertTrue(result.data)
        point = result.data[0]
        self.assertIsInstance(point, NavPoint)
        self.assertEqual(point.currency, "CNY")
        self.assertFalse(hasattr(point, "open"))
        self.assertFalse(hasattr(point, "volume"))

    def test_nav_channel_rejects_non_funds(self) -> None:
        result = FixtureMarketDataProvider().nav_series("SSE:600000", START, END)
        self.assertFalse(result.ok)
        self.assertIn("fail_closed", result.warnings)

    def test_akshare_nav_rejects_exchange_listed_symbols_before_any_network_call(self) -> None:
        provider = AkshareMarketDataProvider()
        provider._ak = object()  # type: ignore[attr-defined]
        result = provider.nav_series("SSE:600000", START, END)
        self.assertFalse(result.ok)
        self.assertIn("unsupported_symbol", result.warnings)


class FxChannelTests(unittest.TestCase):
    def test_direct_and_inverted_pairs(self) -> None:
        provider = FixtureMarketDataProvider()

        direct = provider.fx_rate("USD", "CNY")
        self.assertTrue(direct.ok)
        assert isinstance(direct.data, FxQuote)
        self.assertEqual(direct.data.rate, 7.2)
        self.assertEqual(direct.data.symbol, "FX:USDCNY")

        inverted = provider.fx_rate("CNY", "USD")
        self.assertTrue(inverted.ok)
        assert inverted.data is not None
        self.assertAlmostEqual(inverted.data.rate, 1 / 7.2, places=10)

    def test_unknown_pair_fails_closed(self) -> None:
        result = FixtureMarketDataProvider().fx_rate("GBP", "KRW")
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)


class YahooTickerMappingTests(unittest.TestCase):
    def test_japan_and_fx_map_through_the_exchange_registry(self) -> None:
        cases = [
            ("TSE:7203", "7203.T"),
            ("HKEX:00700", "0700.HK"),
            ("NASDAQ:AAPL", "AAPL"),
            ("FX:USDCNY", "USDCNY=X"),
            ("SSE:600000", "600000.SS"),
        ]
        for symbol, expected in cases:
            with self.subTest(symbol=symbol):
                self.assertEqual(_to_yahoo_symbol(SymbolId.parse(symbol)), expected)

    def test_off_exchange_funds_have_no_yahoo_ticker(self) -> None:
        self.assertIsNone(_to_yahoo_symbol(SymbolId.parse("FUND:110022")))


class _QuoteOnlyProvider:
    PROVIDER_CODE = "quote_only"
    ready = True

    def search(self, query: str = "", limit: int = 10) -> list:
        return []

    def historical_bars(self, *args, **kwargs):  # noqa: ANN002, ANN003
        return ProviderResult.failure(self.PROVIDER_CODE, "no bars")

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        return ProviderResult.failure(self.PROVIDER_CODE, "upstream down")


class CompositeRoutingTests(unittest.TestCase):
    def test_falls_through_to_the_next_capable_provider(self) -> None:
        composite = CompositeMarketDataProvider([_QuoteOnlyProvider(), FixtureMarketDataProvider()])

        result = composite.latest_quote("SSE:600000")

        self.assertTrue(result.ok, msg=result.error)
        self.assertIn("resolved_by:fixture", result.warnings)

    def test_reports_every_failure_and_invents_nothing(self) -> None:
        composite = CompositeMarketDataProvider([_QuoteOnlyProvider()])

        result = composite.latest_quote("SSE:600000")

        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("fail_closed", result.warnings)
        self.assertIn("upstream down", result.error or "")

    def test_missing_capability_is_not_silently_skipped(self) -> None:
        class BarsOnly:
            PROVIDER_CODE = "bars_only"
            ready = True

        composite = CompositeMarketDataProvider([BarsOnly()])
        result = composite.fx_rate("USD", "CNY")

        self.assertFalse(result.ok)
        self.assertIn("无数据源实现 fx_rate", result.error or "")


class RouteTests(unittest.TestCase):
    def setUp(self) -> None:
        self.app = AnalyticsApp(market=FixtureMarketDataProvider())

    def test_quote_route(self) -> None:
        status, payload = dispatch(self.app, "GET", "/market/quote", "symbol=TSE:7203")
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["data"]["currency"], "JPY")

    def test_quote_route_requires_symbol(self) -> None:
        status, _ = dispatch(self.app, "GET", "/market/quote")
        self.assertEqual(status, 400)

    def test_nav_route(self) -> None:
        status, payload = dispatch(
            self.app, "GET", "/market/nav", "symbol=FUND:110022&start=2026-01-05&end=2026-01-30"
        )
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertTrue(payload["data"])
        self.assertIn("nav", payload["data"][0])
        self.assertNotIn("open", payload["data"][0])

    def test_nav_route_rejects_bad_dates(self) -> None:
        status, payload = dispatch(
            self.app, "GET", "/market/nav", "symbol=FUND:110022&start=nope&end=2026-01-30"
        )
        self.assertEqual(status, 200)
        self.assertFalse(payload["ok"])

    def test_fx_route(self) -> None:
        status, payload = dispatch(self.app, "GET", "/market/fx", "base=USD&quote=CNY")
        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        self.assertEqual(payload["data"]["rate"], 7.2)
        self.assertEqual(payload["data"]["symbol"], "FX:USDCNY")

    def test_fx_route_requires_both_currencies(self) -> None:
        status, _ = dispatch(self.app, "GET", "/market/fx", "base=USD")
        self.assertEqual(status, 400)


class FixtureUniverseTests(unittest.TestCase):
    def test_covers_every_market_and_asset_class_in_scope(self) -> None:
        hits = FixtureMarketDataProvider().search("", limit=50)
        assets = {hit.asset_class for hit in hits}
        markets = {hit.market for hit in hits}

        self.assertIn(AssetClass.MUTUAL_FUND, assets)
        self.assertIn(AssetClass.CONVERTIBLE_BOND, assets)
        self.assertIn(AssetClass.FX, assets)
        self.assertIn(AssetClass.ETF, assets)
        self.assertEqual({"cn", "hk", "us", "jp", "global"}, {str(m) for m in markets})


if __name__ == "__main__":
    unittest.main()
