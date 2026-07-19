from __future__ import annotations

import os
import unittest
from datetime import date
from unittest import mock

from caishenfolio_core.market.akshare_provider import AkshareMarketDataProvider
from caishenfolio_core.market.factory import create_market_provider
from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.market.network import humanize_market_error, trust_env_enabled
from caishenfolio_core.server.app import AnalyticsApp, dispatch


class FactoryTests(unittest.TestCase):
    def test_fixture_via_env(self) -> None:
        with mock.patch.dict(os.environ, {"CAISHENFOLIO_MARKET_PROVIDER": "fixture"}):
            provider = create_market_provider()
        self.assertIsInstance(provider, FixtureMarketDataProvider)

    def test_akshare_explicit_name(self) -> None:
        provider = create_market_provider("akshare")
        self.assertIsInstance(provider, AkshareMarketDataProvider)

    def test_auto_name(self) -> None:
        provider = create_market_provider("auto", use_cache=False)
        self.assertEqual(provider.PROVIDER_CODE, "auto")


class AkshareFailClosedTests(unittest.TestCase):
    def test_missing_dependency_fails_closed(self) -> None:
        provider = AkshareMarketDataProvider()
        provider._ak = None  # type: ignore[attr-defined]
        result = provider.historical_bars("SSE:600000", date(2024, 1, 2), date(2024, 1, 5))
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        self.assertIn("fail_closed", result.warnings)
        self.assertIn("akshare", (result.error or "").lower())

    def test_invalid_symbol_fails_closed(self) -> None:
        provider = AkshareMarketDataProvider()
        # Even if akshare is installed, invalid format must fail closed without inventing.
        result = provider.historical_bars("NOT_A_SYMBOL", date(2024, 1, 2), date(2024, 1, 5))
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)

    def test_health_reports_provider_fields(self) -> None:
        app = AnalyticsApp(market=AkshareMarketDataProvider())
        status, health = dispatch(app, "GET", "/health")
        self.assertEqual(status, 200)
        self.assertEqual(health["phase"], "P4")
        self.assertEqual(health["market_provider"], "akshare")
        self.assertIn("market_provider_ready", health)
        self.assertFalse(health["market_data_synthetic"])
        self.assertIn("http_trust_env", health)

    def test_diagnostics_route(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(app, "GET", "/market/diagnostics")
        self.assertEqual(status, 200)
        self.assertEqual(payload["market_provider"], "fixture")
        self.assertTrue(payload["tips"])
        self.assertIn("SSE:600000", payload["supported_examples"])

    def test_humanize_proxy_error(self) -> None:
        msg = humanize_market_error(ProxyError("Unable to connect to proxy"))
        self.assertIn("代理", msg)
        self.assertIn("CAISHENFOLIO_HTTP_TRUST_ENV", msg)


class ProxyError(Exception):
    pass


class NetworkPolicyTests(unittest.TestCase):
    def test_trust_env_default_true(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("CAISHENFOLIO_HTTP_TRUST_ENV", None)
            self.assertTrue(trust_env_enabled())

    def test_trust_env_can_disable(self) -> None:
        with mock.patch.dict(os.environ, {"CAISHENFOLIO_HTTP_TRUST_ENV": "0"}):
            self.assertFalse(trust_env_enabled())


@unittest.skipUnless(
    os.environ.get("CAISHENFOLIO_RUN_LIVE_MARKET_TESTS") == "1",
    "Set CAISHENFOLIO_RUN_LIVE_MARKET_TESTS=1 to hit real network upstream.",
)
class AkshareLiveNetworkTests(unittest.TestCase):
    def test_ashare_real_bars(self) -> None:
        provider = AkshareMarketDataProvider()
        if not provider.ready:
            self.skipTest("akshare not installed")
        result = provider.historical_bars("SSE:600000", date(2024, 1, 2), date(2024, 1, 10))
        self.assertTrue(result.ok, msg=result.error)
        self.assertIsNotNone(result.data)
        assert result.data is not None
        self.assertGreater(len(result.data), 0)
        self.assertEqual(result.data[0].provenance.get("synthetic"), "false")
        self.assertIn("real_market_data", result.warnings)


if __name__ == "__main__":
    unittest.main()
