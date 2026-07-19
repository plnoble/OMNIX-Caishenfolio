from __future__ import annotations

import os
import unittest
from datetime import date
from unittest import mock

from caishenfolio_core.market.alphavantage_provider import AlphaVantageMarketDataProvider
from caishenfolio_core.market.composite import CompositeMarketDataProvider
from caishenfolio_core.market.factory import create_market_provider
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.market.tushare_provider import TushareMarketDataProvider
from caishenfolio_core.market.yfinance_provider import YFinanceMarketDataProvider
from caishenfolio_core.server.app import AnalyticsApp, dispatch


class MultiProviderFactoryTests(unittest.TestCase):
    def test_auto_is_composite(self) -> None:
        provider = create_market_provider("auto", use_cache=False)
        self.assertIsInstance(provider, CompositeMarketDataProvider)
        self.assertEqual(provider.PROVIDER_CODE, "auto")

    def test_fixture_still_available(self) -> None:
        with mock.patch.dict(os.environ, {"CAISHENFOLIO_MARKET_PROVIDER": "fixture"}):
            provider = create_market_provider()
        self.assertIsInstance(provider, FixtureMarketDataProvider)


class CredentialGateTests(unittest.TestCase):
    def test_tushare_without_token_fails_closed(self) -> None:
        provider = TushareMarketDataProvider(token="")
        result = provider.historical_bars("SSE:600000", date(2024, 1, 2), date(2024, 1, 5))
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("missing_credentials", result.warnings)

    def test_alphavantage_without_key_fails_closed(self) -> None:
        provider = AlphaVantageMarketDataProvider(api_key="")
        result = provider.historical_bars("NASDAQ:AAPL", date(2024, 1, 2), date(2024, 1, 5))
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("missing_credentials", result.warnings)

    def test_yfinance_missing_package_fails_closed(self) -> None:
        provider = YFinanceMarketDataProvider()
        provider._yf = None  # type: ignore[attr-defined]
        result = provider.historical_bars("NASDAQ:AAPL", date(2024, 1, 2), date(2024, 1, 5))
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)


class DiagnosticsCatalogTests(unittest.TestCase):
    def test_diagnostics_includes_catalog_and_secret_flags(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(app, "GET", "/market/diagnostics")
        self.assertEqual(status, 200)
        self.assertIn("catalog", payload)
        self.assertTrue(payload["catalog"])
        self.assertIn("tushare_token_configured", payload)
        self.assertIn("alphavantage_api_key_configured", payload)


class CompositeTests(unittest.TestCase):
    def test_composite_uses_first_success(self) -> None:
        class OkProvider:
            PROVIDER_CODE = "ok_src"
            ready = True

            def search(self, query="", limit=10):
                return []

            def historical_bars(self, symbol, start, end, adjustment=None, interval=None):
                from caishenfolio_core.data.models import ProviderResult

                return ProviderResult.success(
                    self.PROVIDER_CODE,
                    [],
                    warnings=("empty_should_not_count",),
                )

        class RealOk:
            PROVIDER_CODE = "real_ok"
            ready = True

            def search(self, query="", limit=10):
                return []

            def historical_bars(self, symbol, start, end, adjustment=None, interval=None):
                from caishenfolio_core.data.models import (
                    Adjustment,
                    OhlcvBar,
                    ProviderResult,
                )
                from datetime import datetime, timezone

                bar = OhlcvBar(
                    timestamp_utc=datetime(2024, 1, 2, tzinfo=timezone.utc),
                    open=1,
                    high=1,
                    low=1,
                    close=1,
                    volume=1,
                    currency="USD",
                    adjustment=Adjustment.RAW,
                    provider=self.PROVIDER_CODE,
                    provenance={"synthetic": "false"},
                )
                return ProviderResult.success(self.PROVIDER_CODE, [bar])

        # first returns ok but empty data → composite continues
        composite = CompositeMarketDataProvider([OkProvider(), RealOk()])
        result = composite.historical_bars("NASDAQ:AAPL", date(2024, 1, 2), date(2024, 1, 3))
        self.assertTrue(result.ok)
        self.assertEqual(result.provider, "real_ok")
        self.assertEqual(len(result.data or []), 1)


if __name__ == "__main__":
    unittest.main()
