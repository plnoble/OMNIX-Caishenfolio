"""WP-02 acceptance vectors: the "convert now" answer and the future critical rate.

These are the vectors the task book spells out as A5–A9. They are written against the
contract *before* the implementation exists, so they fail on purpose until P3/P4 fill it in.

Fixture amounts are synthetic: 100 CNY and 2,450 JPY belong to the plan's fixed worked
example, never to the user's ledger.
"""

from __future__ import annotations

import unittest
from decimal import Decimal

from caishenfolio_core.market.fixture import FixtureMarketDataProvider
from caishenfolio_core.research.fx_paths import (
    HeldPosition,
    MarketQuote,
    PathCost,
    compare_paths,
)
from caishenfolio_core.server.app import AnalyticsApp, dispatch


def _quotes(*items: MarketQuote) -> dict[str, MarketQuote]:
    return {f"{q.base_currency}/{q.quote_currency}": q for q in items}


def _fixture_quotes() -> dict[str, MarketQuote]:
    """The route's own fixture pool: USD/CNY = 7.2, USD/JPY = 150."""
    return _quotes(
        MarketQuote("USD", "CNY", Decimal("7.2"), provider="fixture", as_of="2026-08-28"),
        MarketQuote("USD", "JPY", Decimal("150"), provider="fixture", as_of="2026-08-28"),
    )


def _held() -> HeldPosition:
    # 2,450 JPY for 100 CNY → 24.5 JPY per CNY. Synthetic.
    return HeldPosition(currency="JPY", amount=Decimal("2450"), cost_in_base=Decimal("100"))


class A5ConvertNowTests(unittest.TestCase):
    """A5: 到账额、盈亏，且基准名称与冻结时点必须随结果一起返回（D3）。"""

    def test_the_now_result_carries_its_baseline_name_and_frozen_at(self) -> None:
        panel = compare_paths(
            base_amount=Decimal("100"),
            base_currency="CNY",
            target_currency="USD",
            quotes=_fixture_quotes(),
            held=_held(),
        )

        now = panel.now
        # 2,450 JPY ÷ 150 = 16.333333 USD.
        self.assertAlmostEqual(float(now.gross_amount), 16.333333, places=6)
        # The comparison baseline is stated, not implied: name, pair and the moment it froze.
        self.assertTrue(now.baseline.name)
        self.assertEqual(now.baseline.quote_pair, "USD/CNY")
        self.assertEqual(now.baseline.frozen_at, "2026-08-28")
        self.assertAlmostEqual(float(now.baseline.rate), 7.2, places=6)
        # Profit/loss against that baseline: +17.6% (16.333333 ÷ (100 ÷ 7.2) − 1).
        self.assertAlmostEqual(float(now.advantage_vs_baseline_pct) * 100, 17.6, places=3)
        # And the absolute P&L in the base currency, only because the cost is known:
        # 16.333333 USD × 7.2 = 117.6 CNY against a 100 CNY cost → +17.6 CNY.
        self.assertTrue(now.cost_known)
        self.assertAlmostEqual(float(now.pnl_vs_cost_in_base), 17.6, places=6)
        # Costs are a separate question (A9); this figure is stated on gross amounts.
        self.assertEqual(now.pnl_basis, "gross")

    def test_the_route_serializes_the_baseline_with_the_result(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                "held": {"currency": "JPY", "amount": 2450, "cost_in_base": 100},
            },
        )

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        now = payload["now"]
        self.assertAlmostEqual(now["gross_amount"], 16.333333, places=6)
        self.assertIn("name", now["baseline"])
        # The freeze moment is the as_of of the quote the panel actually used — not "now",
        # and not a date the panel invented.
        self.assertEqual(now["baseline"]["frozen_at"], payload["quotes"]["USD/CNY"]["as_of"])
        self.assertTrue(now["baseline"]["frozen_at"])
        self.assertAlmostEqual(now["baseline"]["rate"], payload["quotes"]["USD/CNY"]["rate"], places=6)
        self.assertIn("USD/CNY", now["baseline"]["quote_pair"])


class A6FutureCriticalRateTests(unittest.TestCase):
    """A6: 未来临界 USD/JPY，单位必须是 USD/JPY，且与旧 breakeven_held_rate 不同字段不同值（R1）。"""

    def test_the_critical_rate_is_in_usd_jpy_and_is_not_the_old_breakeven(self) -> None:
        panel = compare_paths(
            Decimal("100"), "CNY", "USD", _fixture_quotes(), _held()
        )

        critical = panel.future_critical
        # T = e × C = 24.5 (JPY/CNY) × 7.2 (CNY/USD) = 176.4 JPY per USD.
        self.assertTrue(critical.solvable)
        self.assertAlmostEqual(float(critical.rate), 176.4, places=6)
        self.assertEqual(critical.pair, "USD/JPY")
        self.assertIn("USD", critical.unit)
        self.assertIn("JPY", critical.unit)
        self.assertAlmostEqual(float(critical.current_rate), 150.0, places=6)

        # The old field answers a different question and must keep its own value: J ÷ C = 20.833.
        self.assertAlmostEqual(float(panel.breakeven_held_rate), 20.833333, places=6)
        self.assertNotAlmostEqual(float(critical.rate), float(panel.breakeven_held_rate), places=3)

    def test_at_the_critical_rate_the_two_paths_tie(self) -> None:
        # Sanity check of the formula itself, at the rate the panel reports.
        panel = compare_paths(Decimal("100"), "CNY", "USD", _fixture_quotes(), _held())
        tied = _quotes(
            MarketQuote("USD", "CNY", Decimal("7.2"), provider="fixture", as_of="2026-08-28"),
            MarketQuote(
                "USD", "JPY", panel.future_critical.rate, provider="fixture", as_of="2026-08-28"
            ),
        )

        at_tie = compare_paths(Decimal("100"), "CNY", "USD", tied, _held())

        self.assertAlmostEqual(float(at_tie.held_advantage_gross_pct), 0.0, places=9)

    def test_an_unknown_cost_makes_the_critical_rate_unsolvable_with_a_reason(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _fixture_quotes(),
            HeldPosition(currency="JPY", amount=Decimal("2450"), cost_in_base=None),
        )

        critical = panel.future_critical
        self.assertFalse(critical.solvable)
        self.assertIsNone(critical.rate)
        self.assertTrue(critical.reason)
        self.assertIn("成本", critical.reason)


class A7RequiredMoveTests(unittest.TestCase):
    """A7: 「相对当前还需变动多少百分比」，任务书给的定义是 (r − T) / r。"""

    def test_the_task_books_160_to_150_example_is_6_25_percent(self) -> None:
        quotes = _quotes(
            MarketQuote("USD", "CNY", Decimal("7.5"), provider="fixture", as_of="2026-08-28"),
            MarketQuote("USD", "JPY", Decimal("160"), provider="fixture", as_of="2026-08-28"),
        )
        # 2,000 JPY for 100 CNY → 20 JPY/CNY; critical = 20 × 7.5 = 150.
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            quotes,
            HeldPosition(currency="JPY", amount=Decimal("2000"), cost_in_base=Decimal("100")),
        )

        critical = panel.future_critical
        self.assertAlmostEqual(float(critical.rate), 150.0, places=6)
        self.assertAlmostEqual(float(critical.current_rate), 160.0, places=6)
        # (160 − 150) / 160 = +6.25%: the rate has to fall by 6.25% for the two paths to tie.
        self.assertAlmostEqual(float(critical.required_move_pct) * 100, 6.25, places=6)

    def test_a_critical_rate_above_the_market_reports_a_negative_move(self) -> None:
        panel = compare_paths(Decimal("100"), "CNY", "USD", _fixture_quotes(), _held())

        # (150 − 176.4) / 150 = −17.6%: USD/JPY must RISE for the held path to lose its edge.
        self.assertAlmostEqual(
            float(panel.future_critical.required_move_pct) * 100, -17.6, places=3
        )


class A8UnknownCostStillValuatesTests(unittest.TestCase):
    """A8/R5: 数量已知、历史成本未知 —— 估值与可算路径照给，只有「收益」不可得。"""

    def test_the_route_keeps_the_held_position_when_the_cost_is_unknown(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                # No cost_in_base: the ledger has the cash but no receipt for how it was bought.
                "held": {"currency": "JPY", "amount": 2450},
                # An explicit cost model, so the net arrival is judged on its own merits
                # rather than tripping over the cost-model tri-state (that is A9).
                "costs": {
                    "held_to_target": {"spread_pct": 0, "fixed_fee": 0},
                    "base_to_target": {"spread_pct": 0, "fixed_fee": 0},
                },
            },
        )

        self.assertEqual(status, 200)
        self.assertTrue(payload["ok"])
        # The held pot is NOT dropped: it is still valued and still compared.
        self.assertIsNotNone(payload["held"])
        self.assertEqual(payload["held"]["currency"], "JPY")
        self.assertEqual(payload["held"]["amount"], 2450.0)
        self.assertIsNone(payload["held"]["cost_in_base"])
        self.assertIsNone(payload["held"]["implied_rate"])

        by_key = {p["key"]: p for p in payload["paths"]}
        # Convert-what-you-hold is computable and its net is too: 2,450 ÷ 150.
        self.assertAlmostEqual(by_key["held_to_target"]["gross_amount"], 16.333333, places=6)
        self.assertAlmostEqual(by_key["held_to_target"]["net_amount"], 16.333333, places=6)
        # The fresh-money path is unaffected.
        self.assertAlmostEqual(by_key["base_to_target"]["gross_amount"], 13.888889, places=6)
        # The detour comparison still works per unit of new money.
        self.assertIn("base_via_held_to_target", by_key)

        # Only the answers that NEED a cost are missing, and they say why.
        self.assertIsNone(payload["held_advantage_gross_pct"])
        self.assertIsNone(payload["now"]["advantage_vs_baseline_pct"])
        self.assertIsNone(payload["now"]["pnl_vs_cost_in_base"])
        self.assertFalse(payload["now"]["cost_known"])
        self.assertFalse(payload["future_critical"]["solvable"])
        self.assertTrue(
            any("成本" in warning for warning in payload["warnings"])
            or any("成本" in note for note in payload["notes"])
        )

    def test_an_explicitly_empty_holding_is_refused_rather_than_dropped(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                "held": {"currency": "JPY", "amount": 0},
            },
        )

        # A zero holding is a caller mistake, not "no holding": silently treating it as absent
        # would hide the bug. The route reports it with the same 422 it uses for bad semantics.
        self.assertEqual(status, 422)
        self.assertFalse(payload["ok"])
        self.assertIn("持有金额", payload["error"])


class A9PartialCostsStayGrossTests(unittest.TestCase):
    """A9/R7: 部分费用未填 → 该路径 gross_only、净额 None，不得用 0.0 冒充未知。"""

    def test_a_lone_fixed_fee_never_produces_a_net_figure(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _fixture_quotes(),
            _held(),
            costs={
                "held_to_target": PathCost(spread_pct=None, fixed_fee=Decimal("2")),
                "base_to_target": PathCost(spread_pct=Decimal("0"), fixed_fee=Decimal("0")),
            },
        )

        held = {p.key: p for p in panel.paths}["held_to_target"]
        self.assertEqual(held.status, "gross_only")
        self.assertIsNone(held.net_amount)
        # The unknown component must not appear as 0 in the detail either.
        self.assertNotIn("fixed_fee_in_target", held.cost_detail)
        self.assertNotIn("multiplicative_fraction", held.cost_detail)
        # The path with an explicit zero cost is unaffected and still has its net.
        base = {p.key: p for p in panel.paths}["base_to_target"]
        self.assertEqual(base.status, "ok")
        self.assertIsNotNone(base.net_amount)

    def test_the_route_reports_gross_only_rather_than_a_zero_net(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                "held": {"currency": "JPY", "amount": 2450, "cost_in_base": 100},
                "costs": {"held_to_target": {"fixed_fee": 2}},
            },
        )

        self.assertEqual(status, 200)
        by_key = {p["key"]: p for p in payload["paths"]}
        self.assertEqual(by_key["held_to_target"]["status"], "gross_only")
        self.assertIsNone(by_key["held_to_target"]["net_amount"])
        self.assertIsNotNone(by_key["held_to_target"]["gross_amount"])


class A10NetBasisNowResultTests(unittest.TestCase):
    """WP-03 R6/D7: 「现在换」的盈亏按净额口径给出；净额不可知时必须是 None（不是 0，也不是毛额）。

    这些断言钉的是 Python 侧的字段本身——桌面端只是把它显示出来。合成数字沿用任务书的
    固定算例（100 CNY、2,450 JPY、USD/CNY 7.2、USD/JPY 150）。
    """

    def _costs(self, fixed_fee: Decimal | None) -> dict[str, PathCost]:
        return {
            "held_to_target": PathCost(spread_pct=Decimal("0"), fixed_fee=fixed_fee),
            "base_to_target": PathCost(spread_pct=Decimal("0"), fixed_fee=Decimal("0")),
        }

    def test_complete_costs_give_a_net_pnl_that_differs_from_the_gross_one(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _fixture_quotes(),
            _held(),
            costs=self._costs(Decimal("1")),
        )

        now = panel.now
        # Gross arrival 2,450 ÷ 150 = 16.333333 USD → +17.6 CNY against a 100 CNY cost.
        self.assertAlmostEqual(float(now.pnl_vs_cost_in_base), 17.6, places=6)
        # Net arrival 16.333333 − 1 USD fee = 15.333333 USD → 110.4 − 100 = +10.4 CNY.
        self.assertAlmostEqual(float(now.pnl_vs_cost_net_in_base), 10.4, places=6)
        # The two are MEASURABLY different: the net figure is not the gross one relabelled.
        self.assertNotAlmostEqual(
            float(now.pnl_vs_cost_net_in_base), float(now.pnl_vs_cost_in_base), places=6
        )

    def test_an_unknown_net_arrival_leaves_the_net_pnl_unknown(self) -> None:
        panel = compare_paths(
            Decimal("100"),
            "CNY",
            "USD",
            _fixture_quotes(),
            _held(),
            costs=self._costs(None),
        )

        now = panel.now
        # Gross is computable and stays reported as context…
        self.assertAlmostEqual(float(now.pnl_vs_cost_in_base), 17.6, places=6)
        # …but the net answer must be UNKNOWN: never 0, and never the gross number.
        self.assertIsNone(now.pnl_vs_cost_net_in_base)
        self.assertNotEqual(now.pnl_vs_cost_net_in_base, Decimal(0))

    def test_the_route_serializes_the_net_pnl_under_its_own_key(self) -> None:
        app = AnalyticsApp(market=FixtureMarketDataProvider())
        status, payload = dispatch(
            app,
            "POST",
            "/research/fx-paths",
            body={
                "base_currency": "CNY",
                "target_currency": "USD",
                "base_amount": 100,
                "held": {"currency": "JPY", "amount": 2450, "cost_in_base": 100},
                "costs": {
                    "held_to_target": {"spread_pct": 0, "fixed_fee": 1, "fee_currency": "USD"},
                },
            },
        )

        self.assertEqual(status, 200)
        now = payload["now"]
        self.assertIn("pnl_vs_cost_net_in_base", now)
        self.assertAlmostEqual(now["pnl_vs_cost_net_in_base"], 10.4, places=6)
        # The gross figure keeps its own key and its own value (context, not the verdict).
        self.assertAlmostEqual(now["pnl_vs_cost_in_base"], 17.6, places=6)


if __name__ == "__main__":
    unittest.main()
