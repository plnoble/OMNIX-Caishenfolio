from __future__ import annotations

import unittest
from decimal import Decimal

from caishenfolio_core.research.fx_paths import (
    HeldPosition,
    MarketQuote,
    PathCost,
    compare_paths,
)

# The plan's fixed acceptance sample (§7): reference rates around 2026-08-28, deliberately
# NOT bank executable quotes. Every headline number below is taken from the plan itself.
C = Decimal("6.7260")  # USD/CNY: 1 USD = 6.7260 CNY
J = Decimal("159.97")  # USD/JPY: 1 USD = 159.97 JPY


def _quotes(*items: MarketQuote) -> dict[str, MarketQuote]:
    return {f"{q.base_currency}/{q.quote_currency}": q for q in items}


def _sample_quotes() -> dict[str, MarketQuote]:
    return _quotes(
        MarketQuote("USD", "CNY", C, provider="参考", as_of="2026-08-28"),
        MarketQuote("USD", "JPY", J, provider="参考", as_of="2026-08-28"),
    )


def _sample_held() -> HeldPosition:
    # Bought 2,450 JPY for 100 CNY → actual historical rate 24.5 JPY per CNY.
    return HeldPosition(currency="JPY", amount=Decimal("2450"), cost_in_base=Decimal("100"))


class FixedSampleTests(unittest.TestCase):
    """The numbers the product plan wrote down by hand — they must come out of the code."""

    def test_the_plans_worked_example_reproduces_exactly(self) -> None:
        panel = compare_paths(
            base_amount=Decimal("100"),
            base_currency="CNY",
            target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
        )

        by_key = {p.key: p for p in panel.paths}
        self.assertAlmostEqual(float(by_key["base_to_target"].gross_amount), 14.867678, places=6)
        self.assertAlmostEqual(float(by_key["held_to_target"].gross_amount), 15.315372, places=6)
        # 旧路径相对优势 = E / (J / C) - 1 = 3.011%
        self.assertAlmostEqual(float(panel.held_advantage_gross_pct) * 100, 3.011, places=3)

    def test_implied_cross_and_breakeven_match_the_plan(self) -> None:
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )

        # implied CNY/JPY = 159.97 / 6.7260 = 23.783824
        self.assertAlmostEqual(float(panel.implied_cross.rate), 23.783824, places=6)
        self.assertAlmostEqual(float(panel.breakeven_held_rate), 23.783824, places=6)
        # The headline warning states both the actual and the implied rate.
        self.assertTrue(any("24.5" in w and "23.783824" in w for w in panel.warnings))

    def test_when_the_actual_rate_beats_the_implied_one_the_held_path_wins(self) -> None:
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )
        self.assertGreater(float(panel.held_advantage_gross_pct), 0)
        self.assertTrue(any("已有路径占优" in w for w in panel.warnings))

    def test_when_the_actual_rate_trails_the_new_money_path_wins(self) -> None:
        # Bought JPY at 20 per CNY — worse than today's implied 23.78.
        held = HeldPosition("JPY", Decimal("2000"), Decimal("100"))
        panel = compare_paths(Decimal("100"), "CNY", "USD", _sample_quotes(), held)

        self.assertLess(float(panel.held_advantage_gross_pct), 0)
        self.assertTrue(any("新资金路径占优" in w for w in panel.warnings))


class DirectionTests(unittest.TestCase):
    """A quote supplied in the inverted direction must produce identical math (plan §7)."""

    def test_inverted_quotes_reach_the_same_answer(self) -> None:
        forward = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )
        inverted = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _quotes(
                MarketQuote("CNY", "USD", Decimal(1) / C),
                MarketQuote("JPY", "USD", Decimal(1) / J),
            ),
            _sample_held(),
        )

        for panel in (forward, inverted):
            self.assertAlmostEqual(float(panel.held_advantage_gross_pct), 0.030112, places=6)
        f = {p.key: p for p in forward.paths}
        i = {p.key: p for p in inverted.paths}
        self.assertAlmostEqual(
            float(f["held_to_target"].gross_amount),
            float(i["held_to_target"].gross_amount),
            places=6,
        )

    def test_inversion_is_recorded_not_hidden(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _quotes(MarketQuote("CNY", "USD", Decimal(1) / C)),
        )
        payload = panel.to_dict()
        # The payload carries the quote in the canonical direction the math used, and the
        # direction it was actually supplied in stays visible in provenance.
        self.assertIn("USD/CNY", payload["quotes"])
        quote = payload["quotes"]["USD/CNY"]
        self.assertEqual(quote["direction"], "1 USD = 6.726000 CNY")
        self.assertAlmostEqual(quote["rate"], 6.726, places=6)
        self.assertEqual(quote["provenance"].get("inverted_from"), "CNY/USD")


class FailClosedTests(unittest.TestCase):
    def test_a_missing_quote_yields_unknown_paths_not_estimates(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _quotes(MarketQuote("USD", "CNY", C)),  # USD/JPY absent
            _sample_held(),
        )

        by_key = {p.key: p for p in panel.paths}
        self.assertEqual(by_key["held_to_target"].status, "missing_quote")
        self.assertIsNone(by_key["held_to_target"].gross_amount)
        self.assertIsNone(panel.held_advantage_gross_pct)
        self.assertTrue(any("缺少关键报价" in w for w in panel.warnings))
        # The direct path does not need the missing quote, so it still computes.
        self.assertAlmostEqual(float(by_key["base_to_target"].gross_amount), 14.867678, places=6)

    def test_unknown_costs_mean_gross_only_never_free(self) -> None:
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )

        held_path = next(p for p in panel.paths if p.key == "held_to_target")
        self.assertEqual(held_path.status, "gross_only")
        self.assertIsNone(held_path.net_amount)
        self.assertIn("比例成本未填写", held_path.cost_detail["status"])
        self.assertIsNone(panel.held_advantage_net_pct)


class CostTests(unittest.TestCase):
    def test_fees_can_flip_the_advantage(self) -> None:
        # 3.011% gross advantage; a 5% cost on the held path more than eats it.
        # The direct path is explicitly free — PathCost() alone would mean "unknown".
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _sample_quotes(),
            _sample_held(),
            costs={
                "held_to_target": PathCost(fee_pct=Decimal("5"), fixed_fee="0"),
                "base_to_target": PathCost.zero(),
            },
        )

        self.assertAlmostEqual(float(panel.held_advantage_gross_pct) * 100, 3.011, places=3)
        self.assertLess(float(panel.held_advantage_net_pct), 0)

    def test_percentage_cost_reduces_the_notional_exactly(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _sample_quotes(),
            None,
            costs={"base_to_target": PathCost(
                spread_pct=Decimal("0.2"), fee_pct=Decimal("0.1"), fixed_fee=Decimal("0"))},
        )

        direct = next(p for p in panel.paths if p.key == "base_to_target")
        gross = Decimal("100") / C
        expected_net = gross * (Decimal(1) - Decimal("0.003"))
        self.assertAlmostEqual(float(direct.net_amount), float(expected_net), places=8)

    def test_fixed_fee_in_base_currency_is_converted_to_target(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _sample_quotes(),
            None,
            # WP2: the spread must be explicitly stated (0 = confirmed none) for a net figure.
            costs={"base_to_target": PathCost(
                spread_pct=Decimal("0"), fixed_fee=Decimal("67.26"), fee_currency="CNY")},
        )

        direct = next(p for p in panel.paths if p.key == "base_to_target")
        # 67.26 CNY = 10 USD at 6.7260.
        self.assertAlmostEqual(float(direct.net_amount), 14.867678 - 10, places=6)

    def test_fixed_fee_in_an_unconvertible_currency_fails_closed(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _sample_quotes(),
            None,
            costs={"base_to_target": PathCost(
                spread_pct=Decimal("0"), fixed_fee=Decimal("5"), fee_currency="EUR")},
        )

        direct = next(p for p in panel.paths if p.key == "base_to_target")
        self.assertIsNone(direct.net_amount)
        self.assertIn("无法折算", direct.cost_detail["status"])

    def test_via_path_pays_the_spread_twice(self) -> None:
        spread = {"spread_pct": Decimal("1"), "fixed_fee": Decimal("0")}
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _sample_quotes(),
            _sample_held(),
            costs={
                "base_to_target": PathCost(**spread),
                "base_via_held_to_target": PathCost(**spread),
            },
        )

        by_key = {p.key: p for p in panel.paths}
        direct_net = float(by_key["base_to_target"].net_amount)
        via_net = float(by_key["base_via_held_to_target"].net_amount)
        # Same gross (no-arbitrage identity), but two legs pay two spreads.
        self.assertAlmostEqual(
            float(by_key["base_to_target"].gross_amount),
            float(by_key["base_via_held_to_target"].gross_amount),
            places=6,
        )
        self.assertLess(via_net, direct_net)


class PathShapeTests(unittest.TestCase):
    def test_all_five_paths_are_present(self) -> None:
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )

        keys = {p.key for p in panel.paths}
        self.assertEqual(
            keys,
            {"held_to_target", "base_to_target", "base_via_held_to_target", "hold", "split"},
        )

    def test_every_path_states_its_distance_to_the_best(self) -> None:
        # Plan §7: 与其他路径的绝对差和百分比差. Without costs the basis is gross.
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )

        by_key = {p.key: p for p in panel.paths}
        best = by_key[panel.to_dict()["best_path"]]
        self.assertEqual(best.diff_vs_best["abs"], 0.0)
        self.assertEqual(best.diff_vs_best["basis"], "gross")

        direct = by_key["base_to_target"]
        diff = direct.diff_vs_best
        self.assertEqual(diff["best_path"], "held_to_target")
        self.assertEqual(diff["basis"], "gross")
        # 14.867678 vs 15.315372 → -0.447694 USD, -2.923% (the mirror of +3.011%).
        self.assertAlmostEqual(diff["abs"], -0.447694, places=6)
        self.assertAlmostEqual(diff["pct"], -2.923, places=3)
        self.assertIn("USD", diff["abs_in_target"])

        # The hold path is not an exchange: no comparison, no fabricated diff.
        self.assertIsNone(by_key["hold"].diff_vs_best)

    def test_with_costs_the_difference_is_stated_on_the_net_basis(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _sample_quotes(),
            _sample_held(),
            costs={
                # 5% on the held path eats the 3.011% gross advantage → the free direct
                # path is now the best. Every exchange path has costs, so the whole
                # comparison is stated net; one gross-only path would drop it to gross.
                "held_to_target": PathCost(fee_pct=Decimal("5"), fixed_fee="0"),
                "base_to_target": PathCost.zero(),
                "base_via_held_to_target": PathCost.zero(),
            },
        )

        by_key = {p.key: p for p in panel.paths}
        held = by_key["held_to_target"]
        direct = by_key["base_to_target"]
        self.assertEqual(held.diff_vs_best["basis"], "net")
        self.assertEqual(direct.diff_vs_best["abs"], 0.0)
        self.assertLess(held.diff_vs_best["abs"], 0)

    def test_hold_is_valuation_not_an_exchange(self) -> None:
        panel = compare_paths(Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held())

        hold = next(p for p in panel.paths if p.key == "hold")
        self.assertEqual(hold.status, "no_exchange")
        self.assertIsNone(hold.net_amount)
        self.assertAlmostEqual(float(hold.gross_amount), 15.315372, places=6)

    def test_split_half_and_half_is_the_average_of_the_two(self) -> None:
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        )

        split = next(p for p in panel.paths if p.key == "split")
        self.assertAlmostEqual(float(split.gross_amount), (15.315372 + 14.867678) / 2, places=6)

    def test_every_computed_path_carries_a_traceable_formula(self) -> None:
        panel = compare_paths(Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held())

        for path in panel.paths:
            if path.status != "missing_quote":
                self.assertTrue(path.formula, f"{path.key} lacks a formula")
                self.assertTrue(path.starting_asset and path.opportunity_cost)

    def test_a_held_path_needs_no_held_position_to_compare_new_money(self) -> None:
        panel = compare_paths(Decimal("100"), "CNY", "USD", _sample_quotes(), None)

        keys = {p.key for p in panel.paths}
        self.assertEqual(keys, {"base_to_target"})

    def test_disclaimer_and_reference_not_executable_note_are_present(self) -> None:
        payload = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        ).to_dict()

        self.assertIn("非投资建议", payload["disclaimer"])
        self.assertIn("参考价", payload["disclaimer"])


class SerializationTests(unittest.TestCase):
    def test_floats_pass_through_str_never_binary(self) -> None:
        panel = compare_paths(
            100,  # int, but floats take the same _dec(str()) path
            "CNY",
            "USD",
            _quotes(MarketQuote("USD", "CNY", 6.726)),
            None,
        )

        direct = next(p for p in panel.paths if p.key == "base_to_target")
        # 100 / 6.726 = 14.867677... — a float input of 6.726 must not become
        # 6.725999999... on its way into Decimal.
        self.assertAlmostEqual(float(direct.gross_amount), 14.867678, places=6)

    def test_boundary_rounding_is_half_up_at_six_decimals(self) -> None:
        payload = compare_paths(
            Decimal("100"), "CNY", "USD", _sample_quotes(), _sample_held()
        ).to_dict()

        self.assertEqual(payload["base_amount"], 100.0)
        # 14.86767808... → 14.867678 at the serialization boundary, raw value untouched.
        self.assertEqual(payload["paths"][0]["gross_amount"], 15.315372)


if __name__ == "__main__":
    unittest.main()


class FeeFlipTests(unittest.TestCase):
    """计划 §7 验收：加入费用后可以出现优势反转。"""

    def test_fees_can_flip_the_held_path_advantage(self) -> None:
        # Gross: held path wins by ~3.011% (the plan's worked example).
        gross = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
        )
        self.assertIsNotNone(gross.held_advantage_gross_pct)
        self.assertGreater(float(gross.held_advantage_gross_pct), 0.0)

        # Give the held path a large conversion cost and the new path none: the ranking flips.
        with_fees = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={"held_to_target": PathCost(spread_pct="5", fixed_fee="0"), "base_to_target": PathCost(spread_pct="0", fixed_fee="0")},
        )
        self.assertIsNotNone(with_fees.held_advantage_net_pct)
        self.assertLess(float(with_fees.held_advantage_net_pct), 0.0)

    def test_zero_cost_is_distinct_from_unknown_cost(self) -> None:
        # An explicitly free path has a net figure; an unspecified cost model stays gross_only.
        explicit = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={"held_to_target": PathCost.zero(), "base_to_target": PathCost.zero()},
        )
        held = {p.key: p for p in explicit.paths}["held_to_target"]
        self.assertEqual(held.status, "ok")
        self.assertIsNotNone(held.net_amount)


class SplitFixedFeeTests(unittest.TestCase):
    """反例 #6：两条分批路径各有固定费时，完整扣除两笔费用。"""

    def _split_case(self, frac: Decimal, fee1: str, fee2: str) -> PathResult:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            split_fraction=frac,
            costs={
                "held_to_target": PathCost(spread_pct="0", fixed_fee=fee1, fee_currency="USD"),
                "base_to_target": PathCost(spread_pct="0", fixed_fee=fee2, fee_currency="USD"),
            },
        )
        return {p.key: p for p in panel.paths}["split"]

    def test_fifty_fifty_charges_both_fixed_fees_in_full(self) -> None:
        split = self._split_case(Decimal("0.5"), "1", "1")

        self.assertEqual(split.status, "ok")
        # Gross: 1225/159.97 + 50/6.7260 = 15.091524…; both fees bite in full → net −2.
        self.assertAlmostEqual(float(split.gross_amount), 15.091525, places=5)
        self.assertAlmostEqual(float(split.net_amount), 13.091525, places=5)
        detail = split.cost_detail
        self.assertAlmostEqual(detail["fixed_fees_charged"], 2.0, places=6)

    def test_zero_share_skips_that_leg_entirely(self) -> None:
        # frac = 1: only the held leg runs → only its fee applies.
        full_held = self._split_case(Decimal("1"), "1", "1")
        self.assertAlmostEqual(float(full_held.net_amount), float(Decimal(2450) / J - 1), places=6)

        # frac = 0: only the base leg runs.
        full_base = self._split_case(Decimal("0"), "1", "1")
        self.assertAlmostEqual(float(full_base.net_amount), float(Decimal(100) / C - 1), places=6)

    def test_per_base_ranking_includes_fixed_fees(self) -> None:
        split = self._split_case(Decimal("0.5"), "1", "1")

        # The per-base ranking value must not exceed the net outcome scaled by cost: with
        # both fees included it stays strictly below the fee-less gross normalization.
        self.assertIsNotNone(split.net_per_base_amount)
        gross_like = split.gross_per_base_amount
        self.assertLess(float(split.net_per_base_amount), float(gross_like))

    def test_unconvertible_fee_currency_is_gross_only_not_zero_fee(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            split_fraction=Decimal("0.5"),
            costs={
                "held_to_target": PathCost(spread_pct="0", fixed_fee="1", fee_currency="JPY"),
                "base_to_target": PathCost(spread_pct="0", fixed_fee="1", fee_currency="USD"),
            },
        )
        split = {p.key: p for p in panel.paths}["split"]
        self.assertEqual(split.status, "gross_only")
        self.assertEqual(split.cost_detail.get("status"), "固定费币种无法折算")


class ComparisonBasisTests(unittest.TestCase):
    """反例 #5：部分路径费用未知时，顶层最佳路径与 diff_vs_best 完全一致。"""

    def test_partial_costs_force_gross_basis_everywhere(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={"held_to_target": PathCost(spread_pct="1", fixed_fee="0")},
            # base_to_target cost model missing → gross-only.
        ).to_dict()

        self.assertEqual(panel["comparison_basis"], "gross")
        best = panel["best_path"]
        for path in panel["paths"]:
            diff = path.get("diff_vs_best")
            if diff is not None:
                self.assertEqual(diff["best_path"], best)
                self.assertEqual(diff["basis"], "gross")

    def test_full_costs_use_net_basis_everywhere(self) -> None:
        # EVERY exchange path needs a cost model for the net basis; the via-held path is
        # an exchange path too and its absence would correctly force the gross basis.
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={
                "held_to_target": PathCost(spread_pct="1", fixed_fee="0"),
                "base_to_target": PathCost(spread_pct="0.5", fixed_fee="0"),
                "base_via_held_to_target": PathCost(spread_pct="1.5", fixed_fee="0"),
            },
        ).to_dict()

        self.assertEqual(panel["comparison_basis"], "net")
        best = panel["best_path"]
        for path in panel["paths"]:
            diff = path.get("diff_vs_best")
            if diff is not None:
                self.assertEqual(diff["best_path"], best)
                self.assertEqual(diff["basis"], "net")


class SplitRankingUnitsTests(unittest.TestCase):
    """反例 #6/#7：分批排名值与净到账同量纲；分批最优时 best_path 正确。"""

    def test_fifty_fifty_net_per_base_matches_the_net_outcome_scale(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            split_fraction=Decimal("0.5"),
            costs={
                "held_to_target": PathCost(spread_pct="0", fixed_fee="1", fee_currency="USD"),
                "base_to_target": PathCost(spread_pct="0", fixed_fee="1", fee_currency="USD"),
            },
        )
        split = {p.key: p for p in panel.paths}["split"]

        # Net 13.091525 USD for 100 CNY of total base outlay → ~13.09 per base_amount.
        self.assertAlmostEqual(float(split.net_amount), 13.091525, places=5)
        self.assertAlmostEqual(float(split.net_per_base_amount), 13.091525, places=5)

    def test_split_zero_and_one_degenerate_to_single_paths(self) -> None:
        costs = {
            "held_to_target": PathCost(spread_pct="0.5", fixed_fee="0"),
            "base_to_target": PathCost(spread_pct="0.5", fixed_fee="0"),
        }
        all_held = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            split_fraction=Decimal("1"), costs=costs,
        )
        all_base = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            split_fraction=Decimal("0"), costs=costs,
        )
        held_only = {p.key: p for p in all_held.paths}
        base_only = {p.key: p for p in all_base.paths}

        # frac=1 → split equals the held path's net (same pot, same cost model).
        self.assertAlmostEqual(
            float(held_only["split"].net_amount),
            float(held_only["held_to_target"].net_amount), places=6)
        # frac=0 → split equals the fresh-money path's net.
        self.assertAlmostEqual(
            float(base_only["split"].net_amount),
            float(base_only["base_to_target"].net_amount), places=6)

    def test_split_sits_between_the_two_single_paths_with_the_right_units(self) -> None:
        """整改 WP5：线性成本下分批是两条单一路径的组合——位于两者之间、量纲正确。

        旧测试名为「分批最优」却断言 split < held，且允许 best_path ∈ (held, split)——
        它没有验证最优，只是没拦住错误。诚实性质：严格介于两条单一路径之间。
        真正的「分批最优」需要阶梯点差/额度优惠等非线性成本，留待实现后加用例。
        """
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            split_fraction=Decimal("0.5"),
            costs={
                "held_to_target": PathCost.zero(),
                "base_to_target": PathCost(spread_pct="5", fixed_fee="0"),
            },
        ).to_dict()

        by_key = {p["key"]: p for p in panel["paths"]}
        split = by_key["split"]
        held_path = by_key["held_to_target"]
        fresh = by_key["base_to_target"]
        # Strictly between the two singles — the honest property under linear costs.
        self.assertGreater(float(split["net_per_base_amount"]), float(fresh["net_per_base_amount"]))
        self.assertLess(float(split["net_per_base_amount"]), float(held_path["net_per_base_amount"]))
        # And the best path is the cheapest single — never a mis-scaled split.
        self.assertEqual(panel["best_path"], "held_to_target")

    def test_via_held_fixed_fee_without_spread_is_gross_only(self) -> None:
        """整改 WP2 取代旧反例 #8：仅固定费（比例成本未知）→ gross_only，净额 None。"""
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={"base_via_held_to_target": PathCost(fixed_fee="1", fee_currency="USD")},
        )
        via = {p.key: p for p in panel.paths}["base_via_held_to_target"]

        self.assertEqual(via.status, "gross_only")
        self.assertIsNone(via.net_amount)

    def test_via_held_explicit_zero_spread_with_fixed_fee_deducts(self) -> None:
        """显式 0 点差 + 固定费 1 → 净额 = 毛额 − 1（两条腿各扣比例 0）。"""
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={"base_via_held_to_target": PathCost(
                spread_pct="0", fixed_fee="1", fee_currency="USD")},
        )
        via = {p.key: p for p in panel.paths}["base_via_held_to_target"]

        self.assertEqual(via.status, "ok")
        self.assertAlmostEqual(float(via.net_amount), 13.867678, places=5)
        self.assertAlmostEqual(float(via.cost_detail["fixed_fee_in_target"]), 1.0, places=6)

    def test_via_held_unconvertible_fee_currency_stays_gross_only(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(),
            held=_sample_held(),
            costs={"base_via_held_to_target": PathCost(fixed_fee="1", fee_currency="JPY")},
        )
        via = {p.key: p for p in panel.paths}["base_via_held_to_target"]
        self.assertEqual(via.status, "gross_only")
        self.assertEqual(via.cost_detail.get("status"), "固定费币种无法折算")


class CostSemanticsTests(unittest.TestCase):
    """整改计划 WP2：未填写=未知，显式 0=确认无费用；只填固定费不得冒充可算净额。"""

    def test_fixed_fee_without_variable_cost_is_gross_only(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            costs={"held_to_target": PathCost(fixed_fee="1", fee_currency="USD")},
        )
        held = {p.key: p for p in panel.paths}["held_to_target"]
        # spread_pct None = unknown → no net figure despite the fixed fee.
        self.assertEqual(held.status, "gross_only")
        self.assertIsNone(held.net_amount)

    def test_explicit_zero_variable_cost_with_fixed_fee_deducts(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            costs={"held_to_target": PathCost(spread_pct="0", fixed_fee="1", fee_currency="USD")},
        )
        held = {p.key: p for p in panel.paths}["held_to_target"]
        self.assertEqual(held.status, "ok")
        # 2450/159.97 − 1 = 14.315372
        self.assertAlmostEqual(float(held.net_amount), 14.315372, places=5)

    def test_all_costs_explicitly_zero_equals_gross(self) -> None:
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            costs={"held_to_target": PathCost(spread_pct="0", fixed_fee="0", fee_currency="USD")},
        )
        held = {p.key: p for p in panel.paths}["held_to_target"]
        self.assertEqual(held.status, "ok")
        self.assertAlmostEqual(float(held.net_amount), float(held.gross_amount), places=6)

    def test_fee_semantics_identical_across_single_via_and_split(self) -> None:
        """验收 #8：单段/两段/分批在'仅固定费'下同一语义——gross_only。"""
        costs = {
            "held_to_target": PathCost(fixed_fee="1", fee_currency="USD"),
            "base_to_target": PathCost(fixed_fee="1", fee_currency="USD"),
            "base_via_held_to_target": PathCost(fixed_fee="1", fee_currency="USD"),
        }
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            split_fraction=Decimal("0.5"),
            costs=costs,
        )
        by_key = {p.key: p for p in panel.paths}
        for key in ("held_to_target", "base_to_target", "base_via_held_to_target", "split"):
            self.assertEqual(by_key[key].status, "gross_only", key)
            self.assertIsNone(by_key[key].net_amount, key)

    def test_net_arrival_rule_requires_explicit_variable_cost(self) -> None:
        """验收 #6：只填固定费的净到账规则不得触发——比例成本未知。"""
        rule_costs_fixed_only = PathCost(fixed_fee="1", fee_currency="USD")
        self.assertFalse(rule_costs_fixed_only.is_variable_cost_known)
        self.assertTrue(
            PathCost(spread_pct="0", fixed_fee="1", fee_currency="USD").is_variable_cost_known)
        self.assertTrue(
            PathCost(spread_pct="0.5", fixed_fee="0").is_variable_cost_known)
        self.assertFalse(PathCost().is_variable_cost_known)


class CostInvariantTests(unittest.TestCase):
    """整改 WP5：性质级不变量——不依赖具体样例，错误实现无法通过。"""

    def _panel(self, spread: str | None, fee: str | None) -> dict[str, object]:
        costs: dict[str, PathCost] | None = None
        if spread is not None or fee is not None:
            costs = {
                "held_to_target": PathCost(spread_pct=spread, fixed_fee=fee, fee_currency="USD"),
                "base_to_target": PathCost(spread_pct=spread, fixed_fee=fee, fee_currency="USD"),
            }
        panel = compare_paths(
            base_amount="100", base_currency="CNY", target_currency="USD",
            quotes=_sample_quotes(), held=_sample_held(),
            costs=costs,
        )
        return panel.to_dict()

    def test_higher_costs_never_increase_the_net_arrival(self) -> None:
        zero = self._panel("0", "0")
        small = self._panel("0.5", "0")
        larger = self._panel("1.0", "2")

        nets = []
        for panel in (zero, small, larger):
            path = next(p for p in panel["paths"] if p["key"] == "held_to_target")
            nets.append(path["net_amount"])
        self.assertIsNotNone(nets[0])
        self.assertGreater(float(nets[0]), float(nets[1]))
        self.assertGreater(float(nets[1]), float(nets[2]))

    def test_positive_fixed_fee_never_leaves_net_equal_to_gross(self) -> None:
        panel = self._panel("0", "1")
        path = next(p for p in panel["paths"] if p["key"] == "held_to_target")
        self.assertIsNotNone(path["net_amount"])
        self.assertLess(float(path["net_amount"]), float(path["gross_amount"]))

    def test_unknown_costs_never_produce_status_ok(self) -> None:
        for spread, fee in ((None, "1"), ("0.5", None), (None, None)):
            panel = self._panel(spread, fee)
            for path in panel["paths"]:
                if path["key"] == "hold":
                    continue
                self.assertNotEqual(path["status"], "ok", (spread, fee, path["key"]))
                self.assertIsNone(path["net_amount"])


class DetourUnknownFixedFeeTests(unittest.TestCase):
    """FX-01: the two-leg path must refuse a net exactly as the one-leg path does.

    It used to subtract nothing for an unknown fixed fee and return ``ok``, so the screen
    showed a confident net arrival for a cost nobody had supplied. The existing
    "unknown cost cannot be ok" tests never configured this combination on the detour, which
    is why every one of them passed while the hole was open.
    """

    def _compare(self, cost: PathCost) -> dict:
        quotes = {
            "USD/CNY": MarketQuote("USD", "CNY", Decimal("7.2")),
            "USD/JPY": MarketQuote("USD", "JPY", Decimal("160")),
        }
        held = HeldPosition("JPY", Decimal("1000"), Decimal("100"))
        result = compare_paths(
            Decimal("100"), "CNY", "USD", quotes, held=held,
            costs={k: cost for k in ("held_to_target", "base_to_target", "base_via_held_to_target")},
        )
        return {p.key: p for p in result.paths}

    def test_a_known_spread_with_an_unknown_fee_yields_no_net_on_the_detour(self) -> None:
        paths = self._compare(PathCost(spread_pct=Decimal("1")))

        detour = paths["base_via_held_to_target"]
        self.assertEqual(detour.status, "gross_only")
        self.assertIsNone(detour.net_amount)
        self.assertIn("固定费", detour.cost_detail["status"])

    def test_the_detour_now_agrees_with_the_single_leg_path(self) -> None:
        paths = self._compare(PathCost(spread_pct=Decimal("1")))

        # Same cost model, same verdict: this disagreement was the defect.
        self.assertEqual(
            paths["base_via_held_to_target"].status, paths["base_to_target"].status)

    def test_a_fully_stated_cost_still_produces_a_net(self) -> None:
        paths = self._compare(PathCost(
            spread_pct=Decimal("1"), fixed_fee=Decimal("0"), fee_currency="USD"))

        detour = paths["base_via_held_to_target"]
        self.assertEqual(detour.status, "ok")
        self.assertIsNotNone(detour.net_amount)
        # Two legs, so the spread bites twice.
        self.assertEqual(detour.cost_detail["multiplicative_fraction_per_leg"], 0.01)

    def test_an_unknown_fee_is_never_reported_as_zero(self) -> None:
        detour = self._compare(PathCost(spread_pct=Decimal("1")))["base_via_held_to_target"]

        self.assertNotIn("fixed_fee_in_target", detour.cost_detail)
