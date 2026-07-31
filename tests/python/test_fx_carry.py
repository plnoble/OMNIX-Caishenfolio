from __future__ import annotations

import unittest
from datetime import date

from caishenfolio_core.research.fx_carry import build_panel


class CarryTests(unittest.TestCase):
    def test_carry_is_the_rate_gap_and_can_be_negative(self) -> None:
        panel = build_panel(
            "CNY",
            rates={"USD": 7.2, "JPY": 0.048},
            policy_rates={"CNY": 0.030, "USD": 0.045, "JPY": 0.005},
        )

        usd = next(leg for leg in panel.legs if leg.base_currency == "USD")
        jpy = next(leg for leg in panel.legs if leg.base_currency == "JPY")

        # Holding USD against CNY earns the gap; holding JPY pays it.
        self.assertAlmostEqual(usd.carry, 0.015, places=10)
        self.assertAlmostEqual(jpy.carry, -0.025, places=10)

    def test_the_base_currency_has_no_leg_against_itself(self) -> None:
        panel = build_panel("CNY", rates={"CNY": 1.0, "USD": 7.2})

        self.assertEqual([leg.base_currency for leg in panel.legs], ["USD"])

    def test_missing_policy_rate_yields_no_carry_rather_than_zero(self) -> None:
        panel = build_panel("CNY", rates={"KRW": 0.0053}, policy_rates={"KRW": None})

        leg = panel.legs[0]
        self.assertIsNone(leg.carry)
        self.assertTrue(any("无法计算利差" in note for note in panel.notes))

    def test_current_rate_is_placed_within_its_own_history(self) -> None:
        panel = build_panel(
            "CNY",
            rates={"USD": 7.25},
            rate_history={"USD": [6.4 + i * 0.01 for i in range(100)]},
        )

        leg = panel.legs[0]
        # History spans 6.40 to 7.39, so 7.25 sits in the upper part but not at the top.
        self.assertIsNotNone(leg.percentile)
        self.assertAlmostEqual(leg.percentile, 85.5, places=1)
        self.assertAlmostEqual(leg.low, 6.4, places=10)
        self.assertAlmostEqual(leg.high, 7.39, places=10)
        self.assertEqual(leg.sample_size, 100)

    def test_a_rate_at_the_bottom_of_its_range_ranks_low(self) -> None:
        panel = build_panel(
            "CNY",
            rates={"JPY": 0.042},
            rate_history={"JPY": [0.045 + i * 0.0002 for i in range(80)]},
        )

        self.assertLess(panel.legs[0].percentile, 5.0)

    def test_no_history_means_no_position_not_a_fabricated_one(self) -> None:
        panel = build_panel("CNY", rates={"USD": 7.2})

        self.assertIsNone(panel.legs[0].percentile)
        self.assertEqual(panel.legs[0].sample_size, 0)
        self.assertTrue(any("无法定位当前水平" in note for note in panel.notes))

    def test_built_in_rates_are_labelled_as_possibly_stale(self) -> None:
        panel = build_panel("CNY", rates={"USD": 7.2})

        self.assertTrue(any("可能已过期" in note for note in panel.notes))

    def test_supplied_rates_carry_no_stale_warning(self) -> None:
        panel = build_panel("CNY", rates={"USD": 7.2}, policy_rates={"CNY": 0.03, "USD": 0.045})

        self.assertFalse(any("可能已过期" in note for note in panel.notes))


class ExposureTests(unittest.TestCase):
    def test_converts_each_balance_and_weights_it(self) -> None:
        panel = build_panel(
            "CNY",
            rates={"USD": 7.2, "JPY": 0.048},
            exposures={"CNY": 50_000.0, "USD": 10_000.0, "JPY": 1_000_000.0},
        )

        by_currency = {e.currency: e for e in panel.exposures}
        self.assertAlmostEqual(by_currency["USD"].value_in_base, 72_000.0, places=6)
        self.assertAlmostEqual(by_currency["JPY"].value_in_base, 48_000.0, places=6)
        self.assertAlmostEqual(by_currency["CNY"].value_in_base, 50_000.0, places=6)

        total = 72_000.0 + 48_000.0 + 50_000.0
        self.assertAlmostEqual(by_currency["USD"].weight, 72_000.0 / total, places=10)
        self.assertAlmostEqual(sum(e.weight for e in panel.exposures), 1.0, places=10)

    def test_an_unconvertible_balance_has_no_weight_rather_than_zero(self) -> None:
        panel = build_panel("CNY", rates={"USD": 7.2}, exposures={"CNY": 1000.0, "GBP": 500.0})

        gbp = next(e for e in panel.exposures if e.currency == "GBP")
        # Weight zero would read as "this money is worth nothing".
        self.assertIsNone(gbp.value_in_base)
        self.assertIsNone(gbp.weight)

    def test_no_balances_produces_an_empty_exposure_list(self) -> None:
        self.assertEqual(build_panel("CNY", rates={"USD": 7.2}).exposures, [])


class DisclosureTests(unittest.TestCase):
    def test_the_panel_states_that_carry_does_not_time_conversion(self) -> None:
        payload = build_panel("CNY", rates={"USD": 7.2}).to_dict()

        disclaimer = str(payload["disclaimer"])
        self.assertIn("非投资建议", disclaimer)
        self.assertIn("不预测汇率走向", disclaimer)
        self.assertIn("不构成换汇时点判断", disclaimer)

    def test_nothing_in_the_panel_instructs(self) -> None:
        panel = build_panel(
            "CNY",
            rates={"USD": 7.2, "JPY": 0.048},
            rate_history={"USD": [6.5 + i * 0.01 for i in range(100)]},
            exposures={"USD": 10_000.0},
        )

        for note in panel.notes:
            for word in ("建议", "应该", "推荐", "最佳时机"):
                self.assertNotIn(word, note)

    def test_records_the_date_it_was_built_for(self) -> None:
        panel = build_panel("CNY", rates={"USD": 7.2}, as_of=date(2026, 7, 31))
        self.assertEqual(panel.to_dict()["as_of"], "2026-07-31")


if __name__ == "__main__":
    unittest.main()
