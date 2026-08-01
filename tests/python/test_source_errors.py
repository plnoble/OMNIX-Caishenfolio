from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.data.models import ProviderResult, Quote
from caishenfolio_core.market.composite import CompositeMarketDataProvider
from caishenfolio_core.market.errors import (
    DataParseError,
    DataSourceUnavailableError,
    RateLimitError,
    SymbolNotSupportedError,
    classify,
    warning_tags,
)

AS_OF = date(2026, 7, 31)


class ClassifyTests(unittest.TestCase):
    def test_recognises_a_rate_limit(self) -> None:
        for message in [
            "HTTP 429 Too Many Requests",
            "rate limit exceeded",
            "请求过于频繁，请稍后再试",
            "访问频率过高",
        ]:
            with self.subTest(message=message):
                error = classify(Exception(message))
                self.assertIsInstance(error, RateLimitError)
                self.assertTrue(error.retryable)

    def test_recognises_an_unreachable_source(self) -> None:
        for message in [
            "HTTP 503 Service Unavailable",
            "Connection refused",
            "read timed out",
            "Unable to connect to proxy",
            "连接超时",
        ]:
            with self.subTest(message=message):
                error = classify(Exception(message))
                self.assertIsInstance(error, DataSourceUnavailableError)
                self.assertFalse(error.retryable)

    def test_a_broken_payload_is_a_parse_error(self) -> None:
        self.assertIsInstance(classify(ValueError("could not convert")), DataParseError)
        self.assertIsInstance(classify(KeyError("close")), DataParseError)
        self.assertFalse(classify(ValueError("x")).retryable)

    def test_an_unknown_failure_defaults_to_unavailable_not_rate_limited(self) -> None:
        # Assuming a rate limit would make the caller wait on a source that is simply broken.
        error = classify(Exception("something went sideways"))
        self.assertIsInstance(error, DataSourceUnavailableError)
        self.assertFalse(error.retryable)

    def test_an_already_typed_error_passes_through(self) -> None:
        original = SymbolNotSupportedError("no coverage")
        self.assertIs(classify(original), original)

    def test_reads_a_retry_after_hint(self) -> None:
        error = classify(Exception("429 rate limit, Retry-After: 30"))
        self.assertIsInstance(error, RateLimitError)
        self.assertEqual(error.retry_after_seconds, 30.0)

    def test_warning_tags_carry_the_shape_into_the_payload(self) -> None:
        self.assertEqual(
            warning_tags(RateLimitError("x")), ("rate_limited", "fail_closed", "retryable"))
        self.assertEqual(
            warning_tags(DataParseError("x")), ("parse_error", "fail_closed"))


class _Provider:
    """A source that fails a set number of times before succeeding."""

    def __init__(self, code: str, failures: list[Exception | None]) -> None:
        self.PROVIDER_CODE = code
        self.ready = True
        self._failures = list(failures)
        self.calls = 0

    def search(self, query: str = "", limit: int = 10) -> list:
        return []

    def historical_bars(self, *args, **kwargs):  # noqa: ANN002, ANN003
        return ProviderResult.failure(self.PROVIDER_CODE, "no bars")

    def latest_quote(self, symbol: str) -> ProviderResult[Quote]:
        index = self.calls
        self.calls += 1
        failure = self._failures[index] if index < len(self._failures) else None
        if failure is not None:
            raise failure
        return ProviderResult.success(
            self.PROVIDER_CODE,
            Quote(symbol=symbol, price=10.0, currency="CNY", as_of=AS_OF,
                  provider=self.PROVIDER_CODE),
        )


class BackoffTests(unittest.TestCase):
    def _composite(self, providers: list[_Provider]) -> tuple[CompositeMarketDataProvider, list[float]]:
        waits: list[float] = []
        composite = CompositeMarketDataProvider(
            providers, base_backoff_seconds=0.1, sleep=waits.append)
        return composite, waits

    def test_a_rate_limited_source_is_retried_rather_than_abandoned(self) -> None:
        provider = _Provider("a", [Exception("429 too many requests"), None])
        composite, waits = self._composite([provider])

        result = composite.latest_quote("SSE:600000")

        self.assertTrue(result.ok, msg=result.error)
        self.assertEqual(provider.calls, 2)
        self.assertEqual(len(waits), 1)

    def test_backoff_grows_between_attempts(self) -> None:
        provider = _Provider("a", [Exception("429"), Exception("429"), None])
        composite, waits = self._composite([provider])

        composite.latest_quote("SSE:600000")

        self.assertEqual(waits, [0.1, 0.2])

    def test_a_retry_after_hint_overrides_the_default_backoff(self) -> None:
        provider = _Provider("a", [Exception("429 Retry-After: 3"), None])
        composite, waits = self._composite([provider])

        composite.latest_quote("SSE:600000")

        self.assertEqual(waits, [3.0])

    def test_backoff_is_capped(self) -> None:
        provider = _Provider("a", [Exception("429 Retry-After: 900"), None])
        composite = CompositeMarketDataProvider(
            [provider], base_backoff_seconds=0.1, max_backoff_seconds=5.0, sleep=lambda s: None)

        result = composite.latest_quote("SSE:600000")
        self.assertTrue(result.ok)

    def test_a_broken_source_is_not_retried(self) -> None:
        broken = _Provider("a", [Exception("connection refused"), None])
        backup = _Provider("b", [None])
        composite, waits = self._composite([broken, backup])

        result = composite.latest_quote("SSE:600000")

        # Retrying an unreachable source only delays reaching one that works.
        self.assertEqual(broken.calls, 1)
        self.assertEqual(waits, [])
        self.assertIn("resolved_by:b", result.warnings)

    def test_persistent_rate_limiting_falls_through_to_the_next_source(self) -> None:
        limited = _Provider("a", [Exception("429")] * 10)
        backup = _Provider("b", [None])
        composite, _ = self._composite([limited, backup])

        result = composite.latest_quote("SSE:600000")

        self.assertTrue(result.ok)
        self.assertIn("resolved_by:b", result.warnings)
        # Two retries then give up: three calls in total.
        self.assertEqual(limited.calls, 3)

    def test_the_error_shape_survives_into_the_failure_message(self) -> None:
        composite, _ = self._composite([_Provider("a", [Exception("429")] * 10)])

        result = composite.latest_quote("SSE:600000")

        self.assertFalse(result.ok)
        self.assertIn("[rate_limited]", result.error or "")
        self.assertIn("fail_closed", result.warnings)

    def test_retries_can_be_disabled(self) -> None:
        provider = _Provider("a", [Exception("429"), None])
        composite = CompositeMarketDataProvider([provider], rate_limit_retries=0, sleep=lambda s: None)

        result = composite.latest_quote("SSE:600000")

        self.assertFalse(result.ok)
        self.assertEqual(provider.calls, 1)


if __name__ == "__main__":
    unittest.main()
