from __future__ import annotations

import json
import tempfile
import unittest
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

from caishenfolio_core.data.policy_rate import FALLBACK_POLICY_RATES, PolicyRate
from caishenfolio_core.market.policy_rates import (
    PolicyRateService,
    parse_china_lpr,
    parse_ecb_mro,
    parse_effr,
    parse_hkma_base_rate,
    parse_japan_bank_rate,
)
from caishenfolio_core.research.fx_carry import build_panel

# Captured from the live endpoints on 2026-08-01.
_EFFR_PAYLOAD = json.dumps({
    "refRates": [{
        "effectiveDate": "2026-07-30", "type": "EFFR", "percentRate": 3.63,
        "targetRateFrom": 3.50, "targetRateTo": 3.75, "volumeInBillions": 121,
    }]
})

_ECB_PAYLOAD = json.dumps({
    "dataSets": [{"series": {"0:0:0:0:0:0:0": {"observations": {"0": [2.4, 0, 0, None, None]}}}}],
    "structure": {"dimensions": {"observation": [{"values": [{"id": "2026-08-01"}]}]}},
})

_HKMA_PAYLOAD = json.dumps({
    "result": {"records": [{"end_of_date": "2026-07-31", "discount_win_base_rate": 4.25}]}
})


class EffrParsingTests(unittest.TestCase):
    def test_reads_the_rate_as_a_decimal(self) -> None:
        rate = parse_effr(_EFFR_PAYLOAD)

        self.assertIsNotNone(rate)
        self.assertEqual(rate.currency, "USD")
        self.assertAlmostEqual(rate.rate, 0.0363)
        self.assertEqual(rate.as_of, date(2026, 7, 30))
        self.assertFalse(rate.stale)

    def test_carries_the_target_range_as_context(self) -> None:
        self.assertIn("3.50%–3.75%", parse_effr(_EFFR_PAYLOAD).note)

    def test_a_changed_payload_returns_nothing_rather_than_a_guess(self) -> None:
        self.assertIsNone(parse_effr('{"refRates": []}'))
        self.assertIsNone(parse_effr('{"unexpected": 1}'))
        self.assertIsNone(parse_effr("<html>503</html>"))


class EcbParsingTests(unittest.TestCase):
    def test_reads_the_main_refinancing_rate(self) -> None:
        rate = parse_ecb_mro(_ECB_PAYLOAD)

        self.assertEqual(rate.currency, "EUR")
        self.assertAlmostEqual(rate.rate, 0.024)
        self.assertEqual(rate.as_of, date(2026, 8, 1))

    def test_takes_the_latest_observation_when_several_are_returned(self) -> None:
        payload = json.dumps({
            "dataSets": [{"series": {"0:0": {"observations": {"0": [2.0], "1": [2.4]}}}}],
            "structure": {"dimensions": {"observation": [
                {"values": [{"id": "2026-07-01"}, {"id": "2026-08-01"}]}]}},
        })

        rate = parse_ecb_mro(payload)

        self.assertAlmostEqual(rate.rate, 0.024)
        self.assertEqual(rate.as_of, date(2026, 8, 1))

    def test_a_changed_payload_returns_nothing(self) -> None:
        self.assertIsNone(parse_ecb_mro('{"dataSets": []}'))
        self.assertIsNone(parse_ecb_mro("not json"))


class HkmaParsingTests(unittest.TestCase):
    def test_reads_the_base_rate(self) -> None:
        rate = parse_hkma_base_rate(_HKMA_PAYLOAD)

        self.assertEqual(rate.currency, "HKD")
        self.assertAlmostEqual(rate.rate, 0.0425)
        self.assertEqual(rate.as_of, date(2026, 7, 31))

    def test_tolerates_the_field_being_renamed(self) -> None:
        payload = json.dumps({"result": {"records": [{"end_of_date": "2026-07-31",
                                                      "base_rate": 4.25}]}})

        self.assertAlmostEqual(parse_hkma_base_rate(payload).rate, 0.0425)

    def test_a_record_without_a_base_rate_returns_nothing(self) -> None:
        payload = json.dumps({"result": {"records": [{"end_of_date": "2026-07-31"}]}})

        self.assertIsNone(parse_hkma_base_rate(payload))


class LprParsingTests(unittest.TestCase):
    def test_reads_the_one_year_lpr_from_the_last_row(self) -> None:
        rows = [
            {"TRADE_DATE": "2026-06-20", "LPR1Y": 3.10, "LPR5Y": 3.60},
            {"TRADE_DATE": "2026-07-21", "LPR1Y": 3.00, "LPR5Y": 3.50},
        ]

        rate = parse_china_lpr(rows)

        self.assertEqual(rate.currency, "CNY")
        self.assertAlmostEqual(rate.rate, 0.030)
        self.assertEqual(rate.as_of, date(2026, 7, 21))

    def test_no_recognisable_column_returns_nothing(self) -> None:
        self.assertIsNone(parse_china_lpr([{"date": "2026-07-21", "other": 3.0}]))


class JapanParsingTests(unittest.TestCase):
    def test_reads_the_latest_decided_rate(self) -> None:
        rows = [
            {"时间": "2026年06月", "前值": 0.75, "现值": 1.0, "发布日期": "2026-06-16"},
            {"时间": "2026年07月", "前值": 1.00, "现值": 1.0, "发布日期": "2026-07-31"},
        ]

        rate = parse_japan_bank_rate(rows)

        self.assertEqual(rate.currency, "JPY")
        self.assertAlmostEqual(rate.rate, 0.010)
        self.assertEqual(rate.as_of, date(2026, 7, 31))

    def test_a_scheduled_meeting_is_skipped_not_read_as_zero(self) -> None:
        rows = [
            {"时间": "2026年07月", "前值": 1.00, "现值": 1.0, "发布日期": "2026-07-31"},
            # The table lists the next meeting before it happens, with no value yet.
            {"时间": "2026年09月", "前值": 1.00, "现值": float("nan"), "发布日期": "2026-09-18"},
        ]

        rate = parse_japan_bank_rate(rows)

        self.assertAlmostEqual(rate.rate, 0.010)
        self.assertEqual(rate.as_of, date(2026, 7, 31))

    def test_no_decided_row_returns_nothing(self) -> None:
        rows = [{"时间": "2026年09月", "现值": None, "发布日期": "2026-09-18"}]

        self.assertIsNone(parse_japan_bank_rate(rows))


class ServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self._dir = tempfile.TemporaryDirectory()
        self.cache = Path(self._dir.name) / "policy_rates.json"
        self.addCleanup(self._dir.cleanup)

    def _service(self, fetchers, now=None) -> PolicyRateService:
        return PolicyRateService(cache_path=self.cache, fetchers=fetchers, now=now)

    def test_prefers_a_fetched_rate(self) -> None:
        fetched = PolicyRate("USD", 0.0363, "EFFR", "纽约联储", date(2026, 7, 30))

        rates = self._service({"USD": lambda _t: fetched}).rates(["USD"])

        self.assertEqual(rates["USD"], fetched)
        self.assertFalse(rates["USD"].stale)

    def test_a_failing_source_falls_back_and_says_so(self) -> None:
        def explode(_timeout):
            raise TimeoutError("connection timed out")

        rates = self._service({"USD": explode}).rates(["USD"])

        self.assertTrue(rates["USD"].stale)
        self.assertEqual(rates["USD"].rate, FALLBACK_POLICY_RATES["USD"].rate)

    def test_a_currency_with_no_source_falls_back(self) -> None:
        rates = self._service({}).rates(["JPY"])

        self.assertTrue(rates["JPY"].stale)

    def test_an_absurd_rate_is_refused_as_a_changed_payload(self) -> None:
        nonsense = PolicyRate("USD", 3.63, "EFFR", "纽约联储", date(2026, 7, 30))

        rates = self._service({"USD": lambda _t: nonsense}).rates(["USD"])

        # 363% is a percent read as a decimal, not a policy rate.
        self.assertTrue(rates["USD"].stale)
        self.assertEqual(rates["USD"].rate, FALLBACK_POLICY_RATES["USD"].rate)

    def test_an_unknown_currency_gets_no_rate_rather_than_a_neighbours(self) -> None:
        rates = self._service({}).rates(["KRW"])

        self.assertTrue(rates["KRW"].stale)
        self.assertEqual(rates["KRW"].rate, 0.0)
        self.assertIn("没有该币种", rates["KRW"].note)

    def test_every_requested_currency_is_answered(self) -> None:
        rates = self._service({}).rates(["usd", "CNY", " JPY "])

        self.assertEqual(sorted(rates), ["CNY", "JPY", "USD"])

    def test_a_fresh_cache_is_used_instead_of_refetching(self) -> None:
        calls = []

        def counting(_timeout):
            calls.append(1)
            return PolicyRate("USD", 0.0363, "EFFR", "纽约联储", date(2026, 7, 30))

        self._service({"USD": counting}).rates(["USD"])
        second = self._service({"USD": counting}).rates(["USD"])

        self.assertEqual(len(calls), 1)
        self.assertAlmostEqual(second["USD"].rate, 0.0363)
        self.assertFalse(second["USD"].stale)

    def test_an_expired_cache_is_refetched(self) -> None:
        calls = []

        def counting(_timeout):
            calls.append(1)
            return PolicyRate("USD", 0.0363, "EFFR", "纽约联储", date(2026, 7, 30))

        self._service({"USD": counting}).rates(["USD"])
        later = lambda: datetime.now(timezone.utc) + timedelta(days=3)  # noqa: E731
        self._service({"USD": counting}, now=later).rates(["USD"])

        self.assertEqual(len(calls), 2)

    def test_a_stale_fallback_is_never_cached_as_if_it_were_fetched(self) -> None:
        def explode(_timeout):
            raise TimeoutError("down")

        service = PolicyRateService(
            cache_path=self.cache,
            fetchers={"USD": explode, "EUR": lambda _t: PolicyRate("EUR", 0.024, "MRO", "ECB")},
        )
        service.rates(["USD", "EUR"])

        cached = json.loads(self.cache.read_text(encoding="utf-8"))["rates"]
        self.assertIn("EUR", cached)
        self.assertNotIn("USD", cached)

    def test_an_unreadable_cache_is_ignored_rather_than_fatal(self) -> None:
        self.cache.write_text("{ not json", encoding="utf-8")

        rates = self._service({"USD": lambda _t: PolicyRate("USD", 0.0363, "E", "F")}).rates(["USD"])

        self.assertAlmostEqual(rates["USD"].rate, 0.0363)


class CarryPanelIntegrationTests(unittest.TestCase):
    def test_the_panel_reports_each_rates_source_and_date(self) -> None:
        policy = {
            "CNY": PolicyRate("CNY", 0.030, "1年期LPR", "中国人民银行", date(2026, 7, 21)),
            "USD": PolicyRate("USD", 0.0363, "EFFR", "纽约联储", date(2026, 7, 30)),
        }

        leg = build_panel("CNY", rates={"USD": 7.2}, policy_rates=policy).legs[0]

        self.assertAlmostEqual(leg.carry, 0.0063)
        self.assertEqual(leg.base_rate_info.source, "纽约联储")
        self.assertEqual(leg.quote_rate_info.as_of, date(2026, 7, 21))
        self.assertFalse(leg.rates_stale)

    def test_a_stale_rate_marks_the_leg_and_the_notes(self) -> None:
        policy = {
            "CNY": PolicyRate("CNY", 0.030, "1年期LPR", "中国人民银行", date(2026, 7, 21)),
            "USD": FALLBACK_POLICY_RATES["USD"],
        }

        panel = build_panel("CNY", rates={"USD": 7.2}, policy_rates=policy)

        self.assertTrue(panel.legs[0].rates_stale)
        self.assertTrue(any("可能已过期" in note for note in panel.notes))

    def test_a_stale_note_names_only_the_currencies_on_screen(self) -> None:
        panel = build_panel("CNY", rates={"USD": 7.2})

        note = next(n for n in panel.notes if "可能已过期" in n)
        self.assertIn("USD", note)
        self.assertIn("CNY", note)
        self.assertNotIn("EUR", note)

    def test_bare_numbers_are_still_accepted(self) -> None:
        leg = build_panel(
            "CNY", rates={"USD": 7.2}, policy_rates={"CNY": 0.03, "USD": 0.045}).legs[0]

        self.assertAlmostEqual(leg.carry, 0.015)
        self.assertFalse(leg.rates_stale)

    def test_the_dict_payload_exposes_the_rate_provenance(self) -> None:
        payload = build_panel(
            "CNY",
            rates={"USD": 7.2},
            policy_rates={"USD": PolicyRate("USD", 0.0363, "EFFR", "纽约联储", date(2026, 7, 30))},
        ).to_dict()

        leg = payload["legs"][0]
        self.assertEqual(leg["base_rate_info"]["source"], "纽约联储")
        self.assertEqual(leg["base_rate_info"]["as_of"], "2026-07-30")
        self.assertIn("rates_stale", leg)


if __name__ == "__main__":
    unittest.main()
