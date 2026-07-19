from __future__ import annotations

import unittest
from datetime import datetime, timezone

from caishenfolio_core.data.models import Adjustment, OhlcvBar, ProviderResult, SymbolId
from caishenfolio_core.security.loopback import ensure_loopback, is_loopback_host
from caishenfolio_core.security.redaction import redact_mapping, redact_text
from caishenfolio_core.server.app import health_payload, validate_bind_host


class LoopbackTests(unittest.TestCase):
    def test_accepts_loopback(self) -> None:
        for host in ("127.0.0.1", "localhost", "::1"):
            self.assertTrue(is_loopback_host(host))
            ensure_loopback(host)
            self.assertEqual(validate_bind_host(host), host)

    def test_rejects_non_loopback(self) -> None:
        for host in ("0.0.0.0", "192.168.1.10", "example.com"):
            self.assertFalse(is_loopback_host(host))
            with self.assertRaises(ValueError):
                ensure_loopback(host)


class RedactionTests(unittest.TestCase):
    def test_redacts_text_and_mapping(self) -> None:
        text = "api_key=abc123 Bearer tokensecret"
        redacted = redact_text(text)
        self.assertNotIn("abc123", redacted)
        self.assertNotIn("tokensecret", redacted)
        mapping = redact_mapping({"api_key": "secret", "symbol": "SSE:600000"})
        self.assertEqual(mapping["api_key"], "[REDACTED]")
        self.assertEqual(mapping["symbol"], "SSE:600000")


class SymbolAndProviderTests(unittest.TestCase):
    def test_symbol_parse(self) -> None:
        symbol = SymbolId.parse("sse:600000")
        self.assertEqual(symbol.value, "SSE:600000")
        self.assertIsNone(SymbolId.try_parse("AAPL"))

    def test_provider_fail_closed(self) -> None:
        result: ProviderResult[list[OhlcvBar]] = ProviderResult.failure("fixture", "unavailable")
        self.assertFalse(result.ok)
        self.assertIsNone(result.data)
        bar = OhlcvBar(
            timestamp_utc=datetime.now(timezone.utc),
            open=1,
            high=2,
            low=0.5,
            close=1.5,
            volume=1000,
            currency="CNY",
            adjustment=Adjustment.RAW,
            provider="fixture",
        )
        ok = ProviderResult.success("fixture", [bar], warnings=("delayed",))
        self.assertTrue(ok.ok)
        self.assertEqual(ok.warnings, ("delayed",))

    def test_health_payload(self) -> None:
        payload = health_payload()
        self.assertEqual(payload["status"], "ok")
        self.assertEqual(payload["product"], "Caishenfolio")
        self.assertFalse(payload["live_trading_enabled"])
        self.assertEqual(payload["phase"], "P4")


if __name__ == "__main__":
    unittest.main()
