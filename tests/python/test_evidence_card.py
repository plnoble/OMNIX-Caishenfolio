from __future__ import annotations

import json
import unittest

from caishenfolio_core.research.evidence_card import (
    BANNED_PHRASES,
    assemble_card,
    build_historical_check,
    build_market_behaviour,
    build_portfolio_impact,
    conclude,
)


def _bar(date_str: str, close: float, volume: float = 1_000_000) -> dict[str, object]:
    return {"date": date_str, "open": close, "high": close, "low": close, "close": close, "volume": volume}


def _bars(n: int, start: float = 10.0, step: float = 0.01) -> dict[str, object]:
    """n daily bars closing upward; dates are sequential from 2025-01-01."""
    from datetime import date, timedelta

    first = date(2025, 1, 1)
    bars = []
    for i in range(n):
        bars.append(_bar((first + timedelta(days=i)).isoformat(), start + step * i))
    return {"ok": True, "provider": "fixture", "data": bars, "warnings": ["fixture_synthetic_data"]}


def _financials(growth: list[float]) -> dict[str, object]:
    rows = []
    for i, g in enumerate(growth):
        rows.append({
            "period": f"2024Q{i + 1}",
            "revenue": 1000.0 * (1 + g) ** i,
            "net_profit": 100.0,
            "eps": 1.0,
            "roe": 0.1,
            "revenue_growth": g,
            "profit_growth": 0.05,
        })
    return {"ok": True, "provider": "fixture", "data": rows}


def _valuation() -> dict[str, object]:
    return {
        "ok": True,
        "provider": "fixture",
        "data": {"as_of": "2025-12-31"},
        "readings": [
            {
                "metric": "市盈率 PE",
                "current": 12.0,
                "band": "历史低位",
                "percentile": 8.0,
                "sample_size": 120,
                "plain": "分位只描述位置，不代表便宜或应当买入。",
            },
            {
                "metric": "市净率 PB",
                "current": None,
                "band": None,
                "sample_size": 0,
            },
        ],
    }


class AssembleCardTests(unittest.TestCase):
    def test_everything_available_produces_all_sections(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            {"ok": True, "data": {"price": 10.5, "source_count": 3, "spread_pct": 0.2, "sources": "ak/tenc"}},
            _valuation(),
            _financials([0.1, 0.12, 0.08, 0.1]),
            adjustment="raw",
        ).to_dict()

        self.assertEqual(card["symbol"], "SSE:600000")
        self.assertEqual(card["data_quality"]["bar_count"], 200)
        self.assertEqual(card["data_quality"]["quote_sources"], 3)
        self.assertTrue(card["valuation"]["available"])
        self.assertTrue(card["fundamentals"]["available"])
        self.assertTrue(card["market_behaviour"]["available"])
        self.assertTrue(card["historical_check"]["available"])
        self.assertIn("非投资建议", card["disclaimer"])

    def test_missing_critical_data_stops_at_insufficient(self) -> None:
        card = assemble_card(
            "SSE:600000",
            {"ok": False, "provider": "fixture", "data": [], "warnings": []},
            None,
            None,
            None,
            adjustment="raw",
        ).to_dict()

        self.assertEqual(card["conclusion"]["status"], "insufficient")
        self.assertIn("证据不足", card["conclusion"]["summary"])
        self.assertIn("K线", card["data_quality"]["missing"])

    def test_every_evidence_item_carries_a_basis(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            {"ok": True, "data": {"price": 10.5, "source_count": 2, "spread_pct": 0.1, "sources": "a/b"}},
            _valuation(),
            _financials([0.1, 0.1, 0.1, 0.1]),
            adjustment="raw",
        ).to_dict()

        for section in ("evidence_for", "evidence_against", "unknowns"):
            for item in card[section]:
                self.assertTrue(item["text"], f"{section} item has no text")
                self.assertTrue(item["basis"], f"{section} item has no basis: {item}")

    def test_neutral_facts_do_not_count_as_evidence(self) -> None:
        # Trend/volatility/conditional-history items are neutral by construction; a card
        # built only from them must not claim a leaning.
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            None,
            None,
            None,
            adjustment="raw",
        ).to_dict()

        self.assertNotEqual(card["conclusion"]["status"], "positive")
        self.assertNotEqual(card["conclusion"]["status"], "negative")

    def test_a_risk_breach_outranks_everything(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            {"ok": True, "data": {"price": 10.5, "source_count": 1, "spread_pct": 0.0, "sources": "a"}},
            _valuation(),
            _financials([0.1, 0.1, 0.1, 0.1]),
            adjustment="raw",
            portfolio={
                "candidate_amount": 90_000.0,
                "candidate_currency": "CNY",
                "portfolio_value": 100_000.0,
                "portfolio_currency": "CNY",
                "current_weight_of_symbol": 0.05,
                "single_position_limit": 0.2,
                "cash_available": 95_000.0,
                "horizon_days": 365,
            },
        ).to_dict()

        # 90k/190k ≈ 47% + 5% existing → far above the 20% limit.
        self.assertEqual(card["conclusion"]["status"], "risk_exceeded")
        impact = card["portfolio_impact"]
        self.assertGreater(impact["single_position_weight_after"] * 100, 20)
        self.assertTrue(any("超过你设定的上限" in note for note in impact["notes"]))
        self.assertTrue(any("重新生成一次" in item for item in card["invalidation"]))

    def test_an_incoming_candidate_that_fits_shows_both_weights(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            None,
            None,
            None,
            adjustment="raw",
            portfolio={
                "candidate_amount": 10_000.0,
                "candidate_currency": "CNY",
                "portfolio_value": 100_000.0,
                "portfolio_currency": "CNY",
                "current_weight_of_symbol": 0.3,
                "single_position_limit": 0.5,
                "cash_available": 50_000.0,
            },
        ).to_dict()

        impact = card["portfolio_impact"]
        self.assertAlmostEqual(impact["single_position_weight_before"], 0.3)
        # Existing-cash default: the total stays 100 000 → 30 000 + 10 000 = 40%.
        self.assertAlmostEqual(impact["single_position_weight_after"], 0.4, places=4)
        self.assertIn("总资产不变", " ".join(impact["notes"]))
        self.assertEqual(card["conclusion"]["status"] != "risk_exceeded", True)

    def test_external_money_grows_the_denominator_and_keeps_cash(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            None,
            None,
            None,
            adjustment="raw",
            portfolio={
                "candidate_amount": 10_000.0,
                "candidate_currency": "CNY",
                "portfolio_value": 100_000.0,
                "portfolio_currency": "CNY",
                "current_weight_of_symbol": 0.3,
                "single_position_limit": 0.5,
                "cash_available": 50_000.0,
                "funding": "external",
            },
        ).to_dict()

        impact = card["portfolio_impact"]
        # External money dilutes the OLD stake onto the new denominator first (recheck-3
        # R3-01): (0.3 × 100 000 + 10 000) / 110 000 = 40 000/110 000 = 36.3636% — the old
        # "0.3 + 10 000/110 000" double-counted the old stake.
        self.assertAlmostEqual(impact["single_position_weight_after"], 40_000 / 110_000, places=4)
        self.assertAlmostEqual(impact["cash_weight_after"], 50_000 / 110_000, places=4)
        self.assertIn("外部新增资金", " ".join(impact["notes"]))


class WordingGuardTests(unittest.TestCase):
    """The plan's banned phrases must never appear in any string the card produces."""

    @staticmethod
    def _all_strings(payload: dict) -> list[str]:
        out: list[str] = []

        def walk(node) -> None:
            if isinstance(node, dict):
                for v in node.values():
                    walk(v)
            elif isinstance(node, list):
                for v in node:
                    walk(v)
            elif isinstance(node, str):
                out.append(node)

        walk(payload)
        return out

    def test_no_banned_phrase_in_any_card_string(self) -> None:
        # Build the richest card we can so the guard walks as much wording as possible,
        # including the risk-exceeded notes and conflict reasons.
        cards = [
            assemble_card(
                "SSE:600000",
                _bars(200),
                {"ok": True, "data": {"price": 10.5, "source_count": 3, "spread_pct": 0.4, "sources": "a/b/c"}},
                _valuation(),
                _financials([0.1, 0.1, 0.1, 0.1]),
                adjustment="raw",
                portfolio={
                    "candidate_amount": 90_000.0,
                    "candidate_currency": "CNY",
                    "portfolio_value": 100_000.0,
                    "portfolio_currency": "CNY",
                    "current_weight_of_symbol": 0.05,
                    "single_position_limit": 0.2,
                    "cash_available": 95_000.0,
                    "horizon_days": 365,
                },
            ).to_dict(),
            assemble_card(
                "SSE:600000",
                _bars(200),
                None,
                None,
                None,
                adjustment="raw",
            ).to_dict(),
        ]

        strings = [s for card in cards for s in self._all_strings(card)]
        for phrase in BANNED_PHRASES:
            for text in strings:
                self.assertNotIn(phrase, text, msg=f"banned phrase {phrase!r} found")

    def test_the_positive_leaning_carries_its_own_disclaimer(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            {"ok": True, "data": {"price": 10.5, "source_count": 2, "spread_pct": 0.1, "sources": "a/b"}},
            _valuation(),
            _financials([0.1, 0.1, 0.1, 0.1]),
            adjustment="raw",
        ).to_dict()

        if card["conclusion"]["status"] == "positive":
            self.assertIn("不是操作依据", card["conclusion"]["summary"])
            self.assertIn("不是收益预测", card["conclusion"]["reasons"][0])


class SectionBuilderTests(unittest.TestCase):
    def test_market_behaviour_needs_thirty_bars(self) -> None:
        block, items = build_market_behaviour(_bars(29))
        self.assertFalse(block["available"])
        self.assertEqual(items[0].stance, "unknown")

    def test_historical_check_needs_a_hundred_twenty_bars(self) -> None:
        block, items = build_historical_check(_bars(119))
        self.assertFalse(block["available"])
        self.assertIn("不足", block["reason"])

    def test_portfolio_impact_refuses_cross_currency_guessing(self) -> None:
        impact = build_portfolio_impact(
            candidate_amount=1000.0,
            candidate_currency="USD",
            portfolio_value=100_000.0,
            portfolio_currency="CNY",
            current_weight_of_symbol=0.1,
            single_position_limit=0.2,
            cash_available=50_000.0,
            region_weights=None,
            currency_weights=None,
        ).to_dict()

        # No rate was given: weights are None (not zero), and the reason is stated.
        self.assertIsNone(impact["candidate_weight_after"])
        self.assertIsNone(impact["single_position_weight_after"])
        self.assertTrue(any("未提供汇率" in note for note in impact["notes"]))

    def test_over_cash_candidate_is_flagged(self) -> None:
        impact = build_portfolio_impact(
            candidate_amount=80_000.0,
            candidate_currency="CNY",
            portfolio_value=100_000.0,
            portfolio_currency="CNY",
            current_weight_of_symbol=0.0,
            single_position_limit=0.5,
            cash_available=20_000.0,
            region_weights=None,
            currency_weights=None,
        ).to_dict()

        self.assertTrue(any("超过可用现金" in note for note in impact["notes"]))


class ConclusionTests(unittest.TestCase):
    def test_conflict_beats_leaning(self) -> None:
        conclusion = conclude(
            evidence_for=[object()],  # type: ignore[arg-type]
            evidence_against=[object()],  # type: ignore[arg-type]
            unknowns=[],
            risk_reasons=[],
            missing_data=[],
        )
        self.assertEqual(conclusion.status, "conflicting")

    def test_insufficient_when_critical_data_is_missing(self) -> None:
        conclusion = conclude(
            evidence_for=[],
            evidence_against=[],
            unknowns=[],
            risk_reasons=[],
            missing_data=["K线"],
        )
        self.assertEqual(conclusion.status, "insufficient")


class SerializationTests(unittest.TestCase):
    def test_the_card_round_trips_through_json(self) -> None:
        card = assemble_card(
            "SSE:600000",
            _bars(200),
            None,
            None,
            None,
            adjustment="raw",
        ).to_dict()

        text = json.dumps(card, ensure_ascii=False)
        self.assertIn("SSE:600000", text)
        parsed = json.loads(text)
        self.assertEqual(parsed["conclusion"]["status"], card["conclusion"]["status"])


if __name__ == "__main__":
    unittest.main()


class SellSideCardTests(unittest.TestCase):
    """计划 §9 质量门：卖出分析必须列出税费、机会成本和组合影响。"""

    PORTFOLIO = {
        "candidate_amount": 5_000.0,
        "candidate_currency": "CNY",
        "portfolio_value": 100_000.0,
        "portfolio_currency": "CNY",
        "current_weight_of_symbol": 0.10,
        "single_position_limit": 0.20,
        "cash_available": 20_000.0,
    }

    def test_sell_card_flips_stance_relative_to_buy(self) -> None:
        buy = assemble_card("SSE:600000", _bars(300), None, _valuation(), _financials([0.1] * 4),
                            "raw", portfolio=self.PORTFOLIO, side="buy")
        sell = assemble_card("SSE:600000", _bars(300), None, _valuation(), _financials([0.1] * 4),
                             "raw", portfolio=self.PORTFOLIO, side="sell")

        # The same facts answer the opposite question: for/against swap.
        self.assertEqual(sell.side, "sell")
        self.assertEqual(
            [i.text for i in buy.evidence_for], [i.text for i in sell.evidence_against])
        self.assertEqual(
            [i.text for i in buy.evidence_against], [i.text for i in sell.evidence_for])

    def test_sell_impact_reduces_weight_and_spells_out_costs(self) -> None:
        card = assemble_card("SSE:600000", _bars(300), None, _valuation(), _financials([0.1] * 4),
                             "raw", portfolio=self.PORTFOLIO, side="sell")
        impact = card.portfolio_impact
        assert impact is not None
        # 10% held, 5% sold → 5% after; cash 20% → 25%.
        self.assertAlmostEqual(impact.single_position_weight_after, 0.05)
        self.assertAlmostEqual(impact.cash_weight_after, 0.25)
        text = json.dumps(impact.to_dict(), ensure_ascii=False)
        self.assertIn("费用", text)
        self.assertIn("机会成本", text)
        self.assertIn("券商账单", text)

    def test_sell_more_than_held_is_flagged_not_clamped_silently(self) -> None:
        portfolio = dict(self.PORTFOLIO, candidate_amount=30_000.0)
        card = assemble_card("SSE:600000", _bars(300), None, _valuation(), _financials([0.1] * 4),
                             "raw", portfolio=portfolio, side="sell")
        impact = card.portfolio_impact
        assert impact is not None
        self.assertAlmostEqual(impact.single_position_weight_after, 0.0)
        joined = " ".join(impact.notes)
        self.assertIn("超过该标的当前占比", joined)

    def test_sell_card_wording_passes_the_ban_scan(self) -> None:
        card = assemble_card("SSE:600000", _bars(300), None, _valuation(), _financials([0.1] * 4),
                             "raw", portfolio=self.PORTFOLIO, side="sell")
        strings = json.dumps(card.to_dict(), ensure_ascii=False)
        for phrase in BANNED_PHRASES:
            self.assertNotIn(phrase, strings)

    def test_route_accepts_side_sell(self) -> None:
        from caishenfolio_core.research.evidence_card import assemble_card as ac
        card = ac("SSE:600000", _bars(300), None, None, None, "raw", side="sell")
        self.assertEqual(card.side, "sell")


class HistoricalStabilityTests(unittest.TestCase):
    """计划 §9 历史检验：样本外（前后半切分）稳定性与参数敏感性。"""

    def test_stability_and_sensitivity_fields_are_present(self) -> None:
        payload = _bars(300)
        block, items = build_historical_check(payload)
        self.assertTrue(block["available"])
        self.assertIn("stability", block)
        self.assertIn("sensitivity", block)
        self.assertIsInstance(block["stability"]["agrees"], bool)
        self.assertIsInstance(block["sensitivity"]["agrees"], bool)
        # The two new evidence items carry the split and sensitivity statements.
        self.assertGreaterEqual(len(items), 3)
        joined = " ".join(i.text for i in items)
        self.assertTrue("方向" in joined)

    def test_disagreeing_halves_are_reported_as_not_persisting(self) -> None:
        # First half falls steadily, second half rises sharply: the conditional pattern must
        # not read as stable. Construct closes that make the halves disagree.
        from datetime import date, timedelta
        first = date(2024, 1, 1)
        closes: list[float] = []
        # older half: prices far below current (so conditional outcomes differ)
        for i in range(150):
            closes.append(50.0 + 0.5 * i)
        for i in range(150):
            closes.append(200.0 - 0.1 * i)
        bars = {"ok": True, "provider": "fixture", "data": [
            _bar((first + timedelta(days=i)).isoformat(), c) for i, c in enumerate(closes)
        ]}
        block, items = build_historical_check(bars)
        self.assertFalse(block["stability"]["agrees"])
        joined = " ".join(i.text for i in items)
        self.assertIn("不一致", joined)


class EvidenceGateTests(unittest.TestCase):
    """审核反例矩阵 #10/#11/#12：数据质量不投票、最大损失门禁、真实数据日。"""

    def _quality_only_card(self, **portfolio: object) -> dict[str, object]:
        """Bars + a two-source agreeing quote, no valuation, no financials."""
        quote = {
            "ok": True,
            "provider": "fixture",
            "data": {
                "price": 10.0, "currency": "CNY", "as_of": "2025-07-30",
                "source_count": 2, "spread_pct": 0.1,
                "sources": "a/b", "outliers": "",
            },
        }
        return assemble_card(
            "SSE:600000", _bars(200), quote, None, None, "raw",
            portfolio=portfolio or None,
        ).to_dict()

    def test_agreeing_sources_alone_do_not_support_a_buying_conclusion(self) -> None:
        """反例 #10：K线在、两源一致、估值与财务缺失 → 不得输出 positive。"""
        card = self._quality_only_card()
        # The cross-check is a neutral gate, not directional evidence.
        self.assertTrue(all(
            i["stance"] != "positive"
            for i in card["evidence_for"] + card["evidence_against"] + card["unknowns"]
            if "交叉核对" in i["text"]
        ))
        self.assertIn(card["conclusion"]["status"], ("insufficient", "conflicting", "risk_exceeded"))
        self.assertNotEqual(card["conclusion"]["status"], "positive")

    def test_spread_within_tolerance_is_not_a_conflict(self) -> None:
        quote = {
            "ok": True, "provider": "fixture",
            "data": {
                "price": 10.0, "currency": "CNY", "as_of": "2025-07-30",
                "source_count": 2, "spread_pct": 0.5, "sources": "a/b", "outliers": "",
            },
        }
        card = assemble_card("SSE:600000", _bars(200), quote, None, None, "raw").to_dict()
        self.assertEqual(card["data_quality"]["conflicts"], [])
        # Beyond the tolerance it IS a conflict.
        wide = dict(quote)
        wide["data"] = {**quote["data"], "spread_pct": 5.0}
        card2 = assemble_card("SSE:600000", _bars(200), wide, None, None, "raw").to_dict()
        self.assertEqual(len(card2["data_quality"]["conflicts"]), 1)

    def test_max_loss_threshold_flips_the_conclusion_to_risk_exceeded(self) -> None:
        """反例 #11：历史回撤超过用户上限 → risk_exceeded，禁止 positive。"""

        # A falling-then-recovering series has a real drawdown; threshold below it must gate.
        from datetime import date, timedelta
        first = date(2024, 1, 1)
        closes = [100.0 - 0.4 * i for i in range(150)] + [40.0 + 0.1 * i for i in range(150)]
        bars = {"ok": True, "provider": "fixture", "data": [
            _bar((first + timedelta(days=i)).isoformat(), c) for i, c in enumerate(closes)
        ]}
        quote = {
            "ok": True, "provider": "fixture",
            "data": {"price": 55.0, "currency": "CNY", "as_of": "2024-12-30",
                     "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""},
        }
        card2 = assemble_card(
            "SSE:600000", bars, quote, _valuation(), _financials([0.1] * 4), "raw",
            portfolio={
                "candidate_amount": 1000.0,
                "candidate_currency": "CNY",
                "portfolio_value": 100_000.0,
                "portfolio_currency": "CNY",
                "current_weight_of_symbol": 0.1,
                "single_position_limit": 0.5,
                "cash_available": 50_000.0,
                "max_loss_pct": 0.2,
            },
        ).to_dict()
        self.assertEqual(card2["conclusion"]["status"], "risk_exceeded")
        joined = " ".join(card2["invalidation"])
        self.assertIn("最大可承受损失", joined)

    def test_as_of_is_the_last_real_data_date_not_the_request_end(self) -> None:
        """反例 #12：周末请求，顶部显示最后真实数据日。"""
        # Bars end 2025-07-18 (a Friday); request end is a Sunday.
        bars = _bars(200)
        card = assemble_card("SSE:600000", bars, None, None, None, "raw",
                             as_of="2099-01-01").to_dict()
        # The card date must be the last bar's date, not the request's end parameter.
        self.assertEqual(card["as_of"], _bar_date_of(bars))
        self.assertIn("bars", card["data_quality"]["channel_as_of"])


def _bar_date_of(bars: dict[str, object]) -> str:
    return str((bars["data"] or [])[-1]["date"])  # type: ignore[index]


class MaxLossContractTests(unittest.TestCase):
    """反例 #3/#4/#5：UI 百分比 → API 小数的单位契约与风险原因结构化。"""

    def _falling_bars(self) -> dict[str, object]:
        from datetime import date, timedelta
        first = date(2024, 1, 1)
        closes = [100.0 - 0.4 * i for i in range(150)] + [40.0 + 0.1 * i for i in range(150)]
        return {"ok": True, "provider": "fixture", "data": [
            _bar((first + timedelta(days=i)).isoformat(), c) for i, c in enumerate(closes)
        ]}

    def _quote(self) -> dict[str, object]:
        return {"ok": True, "provider": "fixture", "data": {
            "price": 55.0, "currency": "CNY", "as_of": "2024-12-30",
            "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""}}

    def _portfolio(self, max_loss: float) -> dict[str, object]:
        return {
            "candidate_amount": 1000.0, "candidate_currency": "CNY",
            "portfolio_value": 100_000.0, "portfolio_currency": "CNY",
            "current_weight_of_symbol": 0.1, "single_position_limit": 0.5,
            "cash_available": 50_000.0, "max_loss_pct": max_loss,
        }

    def test_twenty_percent_travels_as_a_fraction_and_trips_the_gate(self) -> None:
        """反例 #4：UI 20% → API 0.2；60% 历史回撤 → risk_exceeded。"""
        card = assemble_card(
            "SSE:600000", self._falling_bars(), self._quote(),
            _valuation(), _financials([0.1] * 4), "raw",
            portfolio=self._portfolio(0.2),
        ).to_dict()

        self.assertEqual(card["conclusion"]["status"], "risk_exceeded")
        # And the reason names the drawdown, not the position limit (P1-R03).
        reasons = " ".join(card["conclusion"]["reasons"])
        self.assertIn("历史最大回撤", reasons)
        self.assertIn("20.0%", reasons)
        self.assertNotIn("单一持仓", reasons)

    def test_percent_scale_input_is_rejected_not_guessed(self) -> None:
        """反例 #5：20（百分数值）不是合法小数——显式拒绝，不猜单位。"""
        with self.assertRaises(ValueError):
            assemble_card(
                "SSE:600000", self._falling_bars(), self._quote(),
                _valuation(), _financials([0.1] * 4), "raw",
                portfolio=self._portfolio(20.0),
            )

    def test_zero_and_out_of_range_are_rejected(self) -> None:
        for bad in (0.0, -0.1, 1.5):
            with self.assertRaises(ValueError):
                assemble_card(
                    "SSE:600000", self._falling_bars(), self._quote(),
                    _valuation(), _financials([0.1] * 4), "raw",
                    portfolio=self._portfolio(bad),
                )

    def test_position_limit_reason_stays_specific_when_only_it_fires(self) -> None:
        # No max-loss input; the single-position limit does fire.
        portfolio = dict(self._portfolio(0.2))
        portfolio.pop("max_loss_pct")
        portfolio["single_position_limit"] = 0.05  # any buy breaches 5%
        card = assemble_card(
            "SSE:600000", self._falling_bars(), self._quote(),
            _valuation(), _financials([0.1] * 4), "raw",
            portfolio=portfolio,
        ).to_dict()

        self.assertEqual(card["conclusion"]["status"], "risk_exceeded")
        reasons = " ".join(card["conclusion"]["reasons"])
        self.assertIn("单一持仓", reasons)
        self.assertNotIn("历史最大回撤", reasons)

    def test_both_reasons_listed_when_both_fire(self) -> None:
        portfolio = dict(self._portfolio(0.2))
        portfolio["single_position_limit"] = 0.05
        card = assemble_card(
            "SSE:600000", self._falling_bars(), self._quote(),
            _valuation(), _financials([0.1] * 4), "raw",
            portfolio=portfolio,
        ).to_dict()

        reasons = " ".join(card["conclusion"]["reasons"])
        self.assertIn("单一持仓", reasons)
        self.assertIn("历史最大回撤", reasons)


class ChannelDateHygieneTests(unittest.TestCase):
    """反例 #14/#15：通道日期不混报告期、不同步要有警告。"""

    def test_a_report_period_never_wins_the_headline_date(self) -> None:
        # Bars old, quote old, financials period '2026Q1' — the quarter label must not
        # become the card's as_of.
        quote = {
            "ok": True, "provider": "fixture",
            "data": {"price": 10.0, "currency": "CNY", "as_of": "2024-12-30",
                     "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""},
        }
        valuation = {"ok": True, "provider": "fixture",
                     "data": {"as_of": "2025-12-31"}, "readings": []}
        financials = {"ok": True, "provider": "fixture", "data": [
            {"period": "2026Q1", "revenue": 1.0, "net_profit": 1.0, "eps": 1.0,
             "roe": 0.1, "revenue_growth": 0.1, "profit_growth": 0.05}]}

        card = assemble_card(
            "SSE:600000", _bars(200), quote, valuation, financials, "raw").to_dict()

        # The headline is the newest ISO date among market channels — 2025-12-31 — never 2026Q1.
        self.assertEqual(card["as_of"], "2025-12-31")
        self.assertNotIn("2026Q1", card["as_of"])

    def test_stale_channel_produces_a_data_quality_warning(self) -> None:
        # Quote today, valuation a year and a half old: the desync must be said.
        quote = {
            "ok": True, "provider": "fixture",
            "data": {"price": 10.0, "currency": "CNY", "as_of": "2025-07-19",
                     "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""},
        }
        valuation = {"ok": True, "provider": "fixture",
                     "data": {"as_of": "2024-01-01"}, "readings": []}

        card = assemble_card(
            "SSE:600000", _bars(200), quote, valuation, None, "raw").to_dict()

        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("不同步", warnings)
        self.assertIn("valuation", warnings)

    def test_unparseable_channel_dates_keep_provenance_but_stay_out_of_the_headline(self) -> None:
        quote = {
            "ok": True, "provider": "fixture",
            "data": {"price": 10.0, "currency": "CNY", "as_of": "garbage",
                     "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""},
        }
        card = assemble_card("SSE:600000", _bars(200), quote, None, None, "raw").to_dict()

        # The headline falls back to the bars date; the raw value keeps its slot.
        self.assertEqual(card["as_of"], _bar_date_of(_bars(200)))
        self.assertEqual(card["data_quality"]["channel_as_of"]["quote"], "garbage")


class RegionCurrencyImpactTests(unittest.TestCase):
    """反例 #18：证据卡显示候选交易后的币种/地区变化，或明确标为未知。"""

    def _portfolio(self, **extra: object) -> dict[str, object]:
        base: dict[str, object] = {
            "candidate_amount": 10_000.0, "candidate_currency": "CNY",
            "portfolio_value": 100_000.0, "portfolio_currency": "CNY",
            "current_weight_of_symbol": 0.2, "single_position_limit": 0.5,
            "cash_available": 50_000.0,
            # A complete slice set includes cash as its own region (the ledger's own
            # grouping); without it a cash-funded buy has nowhere to draw weight from.
            "region_weights": {"cn": 0.2, "us": 0.3, "cash": 0.5},
            "currency_weights": {"CNY": 0.6, "USD": 0.4},
        }
        base.update(extra)
        return base

    def test_after_weights_reflect_the_candidate_trade(self) -> None:
        """整改 WP1：旧断言把 110% 的不守恒结果固化成了期望——现金没扣，总和超 1。"""
        card = assemble_card(
            "NASDAQ:AAPL", _bars(200), None, _valuation(), _financials([0.1] * 4), "raw",
            portfolio=self._portfolio(
                instrument_region="us", instrument_currency="USD",
                funding_currency="CNY"),
        ).to_dict()
        impact = card["portfolio_impact"]

        region = impact["region_weight_after"]
        currency = impact["currency_weight_after"]
        # Existing-cash buy: cash 50%→40%, US 30%→40%, CN untouched; sums stay 1.
        self.assertAlmostEqual(region["us"], 0.40, places=6)
        self.assertAlmostEqual(region["cash"], 0.40, places=6)
        self.assertAlmostEqual(region["cn"], 0.20, places=6)
        self.assertAlmostEqual(sum(region.values()), 1.0, places=6)
        # CNY 60%→50%, USD 40%→50%; sums stay 1.
        self.assertAlmostEqual(currency["USD"], 0.50, places=6)
        self.assertAlmostEqual(currency["CNY"], 0.50, places=6)
        self.assertAlmostEqual(sum(currency.values()), 1.0, places=6)

    def test_unknown_metadata_is_said_not_hidden(self) -> None:
        card = assemble_card(
            "NASDAQ:AAPL", _bars(200), None, _valuation(), _financials([0.1] * 4), "raw",
            portfolio=self._portfolio(),  # no instrument_region / instrument_currency
        ).to_dict()
        impact = card["portfolio_impact"]

        self.assertEqual(impact["region_weight_after"], {})
        self.assertEqual(impact["currency_weight_after"], {})
        notes = " ".join(impact["notes"])
        self.assertIn("未知标的所属地区", notes)
        self.assertIn("未知标的计价货币", notes)


class WeightConservationTests(unittest.TestCase):
    """整改计划 WP1：交易后地区/币种权重必须守恒（合计=1），旧测试把 110% 固化成期望。"""

    PORTFOLIO = {
        "candidate_amount": 10_000.0, "candidate_currency": "CNY",
        "portfolio_value": 100_000.0, "portfolio_currency": "CNY",
        "current_weight_of_symbol": 0.2, "single_position_limit": 0.5,
        "cash_available": 50_000.0,
        "region_weights": {"cn": 0.3, "us": 0.2, "cash": 0.5},
        "currency_weights": {"CNY": 0.6, "USD": 0.4},
    }

    def _impact(self, **extra: object) -> dict[str, object]:
        side = str(extra.pop("side", "buy"))
        portfolio = {**self.PORTFOLIO, **extra}
        card = assemble_card(
            "NASDAQ:AAPL", _bars(200), None, _valuation(), _financials([0.1] * 4), "raw",
            portfolio=portfolio, side=side,
        ).to_dict()
        return card["portfolio_impact"]

    def test_existing_cash_buy_keeps_region_and_currency_sums_at_one(self) -> None:
        impact = self._impact(
            instrument_region="us", instrument_currency="USD",
            funding="existing_cash", funding_currency="CNY")

        region = impact["region_weight_after"]
        currency = impact["currency_weight_after"]
        self.assertAlmostEqual(sum(region.values()), 1.0, places=6)
        self.assertAlmostEqual(sum(currency.values()), 1.0, places=6)

    def test_external_money_scales_old_weights_before_adding(self) -> None:
        impact = self._impact(
            instrument_region="us", instrument_currency="USD",
            funding="external", funding_currency="CNY")

        region = impact["region_weight_after"]
        currency = impact["currency_weight_after"]
        self.assertAlmostEqual(sum(region.values()), 1.0, places=6)
        self.assertAlmostEqual(sum(currency.values()), 1.0, places=6)
        # Old buckets scale down by 100/110; the candidate adds 10/110.
        self.assertAlmostEqual(region["cn"], 0.3 * 100 / 110, places=6)
        self.assertAlmostEqual(currency["CNY"], 0.6 * 100 / 110, places=6)

    def test_same_currency_buy_leaves_currency_exposure_unchanged(self) -> None:
        impact = self._impact(
            instrument_region="cn", instrument_currency="CNY",
            funding="existing_cash", funding_currency="CNY")

        # Money moves inside the CNY slice: cash→asset, totals unchanged.
        self.assertAlmostEqual(impact["currency_weight_after"]["CNY"], 0.6, places=6)
        self.assertAlmostEqual(sum(impact["currency_weight_after"].values()), 1.0, places=6)

    def test_sell_moves_region_to_cash_and_keeps_currency_total(self) -> None:
        impact = self._impact(
            side="sell", instrument_region="us", instrument_currency="USD")

        region = impact["region_weight_after"]
        currency = impact["currency_weight_after"]
        # Sell impact must compute after-weights too — not return empties.
        self.assertTrue(region, "卖出卡必须显示地区影响")
        self.assertTrue(currency, "卖出卡必须显示币种影响")
        # Selling 10% of a 100 000 portfolio: US falls, cash rises by the same weight.
        self.assertAlmostEqual(region["us"], 0.2 - 0.1, places=6)
        self.assertAlmostEqual(region["cash"], 0.5 + 0.1, places=6)
        # Proceeds stay in USD: the USD slice is unchanged, total still 1.
        self.assertAlmostEqual(currency["USD"], 0.4, places=6)
        self.assertAlmostEqual(sum(currency.values()), 1.0, places=6)

    def test_non_conserving_input_fails_closed(self) -> None:
        bad = {**self.PORTFOLIO,
               "region_weights": {"cn": 0.5, "us": 0.2, "cash": 0.5},  # sums to 1.2
               "instrument_region": "us", "instrument_currency": "USD"}
        impact = self._impact(**bad)

        self.assertEqual(impact["region_weight_after"], {})
        notes = " ".join(impact["notes"])
        self.assertIn("守恒", notes)

    def test_no_negative_weights_are_produced(self) -> None:
        # Selling more than held clamps at the position's weight — never negative.
        impact = self._impact(
            side="sell", candidate_amount=90_000.0,
            instrument_region="us", instrument_currency="USD")

        region = impact["region_weight_after"]
        self.assertTrue(all(v >= 0 for v in region.values()))
        notes = " ".join(impact["notes"])
        self.assertIn("超过", notes)  # the over-sell note from the sell impact


class ChannelDateDisplayTests(unittest.TestCase):
    """整改 WP4：多频道日期分列展示，不用最新日期冒充统一截止日。"""

    def _card(self, valuation_as_of: str = "2025-01-01", quote_as_of: str = "2025-07-19",
              financial_period: str = "2020Q4") -> dict[str, object]:
        quote = {"ok": True, "provider": "fixture", "data": {
            "price": 10.0, "currency": "CNY", "as_of": quote_as_of,
            "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""}}
        valuation = {"ok": True, "provider": "fixture",
                     "data": {"as_of": valuation_as_of}, "readings": []}
        financials = {"ok": True, "provider": "fixture", "data": [
            {"period": financial_period, "revenue": 1.0, "net_profit": 1.0, "eps": 1.0,
             "roe": 0.1, "revenue_growth": 0.1, "profit_growth": 0.05}]}
        return assemble_card(
            "SSE:600000", _bars(200), quote, valuation, financials, "raw").to_dict()

    def test_all_channel_dates_stay_visible_and_separate(self) -> None:
        """验收 #11：三个日期/报告期全部可见。"""
        card = self._card()
        channels = card["data_quality"]["channel_as_of"]
        self.assertEqual(channels["bars"], _bar_date_of(_bars(200)))
        self.assertEqual(channels["quote"], "2025-07-19")
        self.assertEqual(channels["valuation"], "2025-01-01")
        self.assertEqual(channels["financials"], "2020Q4")
        # The headline is the market date; the quarter never leaks into it.
        self.assertEqual(card["market_as_of"], "2025-07-19")

    def test_small_gap_still_reports_the_desync(self) -> None:
        """验收 #11/#14：估值只落后 30 天也显示差异，不因不足一年而隐藏。"""
        card = self._card(valuation_as_of="2025-06-19")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("不同步", warnings)
        self.assertIn("30 天", warnings)

    def test_stale_financial_period_warns(self) -> None:
        card = self._card(financial_period="2020Q4")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("财务报告期 2020Q4", warnings)

    def test_quarter_period_never_participates_in_date_sorting(self) -> None:
        """验收 #12：2026Q1 保持报告期，不进入 ISO 日期比较。"""
        card = self._card(financial_period="2026Q1")
        # 2026Q1 would win a string max(); the market date must still lead.
        self.assertEqual(card["market_as_of"], "2025-07-19")
        self.assertEqual(card["data_quality"]["channel_as_of"]["financials"], "2026Q1")


class SellConsistencyTests(unittest.TestCase):
    """复审 P1-02：卖出超持仓时现金/地区/币种必须用同一个实际可卖值。"""

    BASE = {
        "candidate_amount": 90_000.0, "candidate_currency": "CNY",
        "portfolio_value": 100_000.0, "portfolio_currency": "CNY",
        "current_weight_of_symbol": 0.2, "single_position_limit": 0.5,
        "cash_available": 50_000.0,
        "region_weights": {"cn": 0.2, "us": 0.2, "cash": 0.6},
        "currency_weights": {"CNY": 0.6, "USD": 0.4},
        "instrument_region": "us", "instrument_currency": "USD",
    }

    def _impact(self, **extra: object) -> dict[str, object]:
        portfolio = {**self.BASE, **extra}
        card = assemble_card(
            "NASDAQ:AAPL", _bars(200), None, _valuation(), _financials([0.1] * 4), "raw",
            portfolio=portfolio, side="sell",
        ).to_dict()
        return card["portfolio_impact"]

    def test_oversell_uses_theSellableAmountEverywhere(self) -> None:
        """请求卖 90%，只持有 20% → 现金/地区全部按 20% 变化，绝无 140% 现金。"""
        impact = self._impact()

        # Cash line: 50% + 20% (the sellable part) = 70%, NOT 50% + 90% = 140%.
        self.assertAlmostEqual(impact["cash_weight_after"], 0.70, places=6)
        # Region: us 20% → 0%, cash 60% → 80% — same 20% move.
        region = impact["region_weight_after"]
        self.assertAlmostEqual(region["us"], 0.0, places=6)
        self.assertAlmostEqual(region["cash"], 0.80, places=6)
        self.assertAlmostEqual(sum(region.values()), 1.0, places=6)
        # The over-sell is still said out loud.
        notes = " ".join(impact["notes"])
        self.assertIn("超过", notes)

    def test_sell_input_weights_not_conserving_fail_closed(self) -> None:
        impact = self._impact(
            region_weights={"cn": 0.5, "us": 0.2, "cash": 0.5})  # sums to 1.2

        self.assertEqual(impact["region_weight_after"], {})
        notes = " ".join(impact["notes"])
        self.assertIn("权重不守恒", notes)

    def test_region_slice_smaller_than_position_fails_closed(self) -> None:
        # The symbol claims 20% of the portfolio but its region slice holds only 10%:
        # inconsistent inputs, no honest after-state exists.
        impact = self._impact(region_weights={"cn": 0.2, "us": 0.1, "cash": 0.7})

        self.assertEqual(impact["region_weight_after"], {})
        notes = " ".join(impact["notes"])
        self.assertIn("地区切片", notes)

    def test_partial_sell_keeps_cash_region_currency_consistent(self) -> None:
        impact = self._impact(candidate_amount=10_000.0)  # sell 10%

        # The cash LINE (cash_available 50% of value) gains the sold 10% → 60%.
        self.assertAlmostEqual(impact["cash_weight_after"], 0.60, places=6)
        region = impact["region_weight_after"]
        # The region SLICE "cash" (60%, holding all non-equity value) gains the same 10% → 70%.
        self.assertAlmostEqual(region["us"], 0.10, places=6)
        self.assertAlmostEqual(region["cash"], 0.70, places=6)
        self.assertAlmostEqual(sum(region.values()), 1.0, places=6)
        currency = impact["currency_weight_after"]
        self.assertAlmostEqual(sum(currency.values()), 1.0, places=6)
        # The two cash views differ by exactly the non-cash, non-equity part of the slice —
        # constant before and after the trade.
        self.assertAlmostEqual(
            region["cash"] - impact["cash_weight_after"], 0.10, places=6)


class DateAnomalyTests(unittest.TestCase):
    """复审 P2-02/P2-03：不可解析日期警告 + 季度级陈旧度（跨年正确）。"""

    def _card(self, quote_as_of: str = "2025-07-19", financial_period: str = "2025Q1") -> dict[str, object]:
        quote = {"ok": True, "provider": "fixture", "data": {
            "price": 10.0, "currency": "CNY", "as_of": quote_as_of,
            "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""}}
        financials = {"ok": True, "provider": "fixture", "data": [
            {"period": financial_period, "revenue": 1.0, "net_profit": 1.0, "eps": 1.0,
             "roe": 0.1, "revenue_growth": 0.1, "profit_growth": 0.05}]}
        return assemble_card(
            "SSE:600000", _bars(200), quote, None, financials, "raw").to_dict()

    def test_unparseable_channel_date_flags_a_format_anomaly(self) -> None:
        card = self._card(quote_as_of="garbage")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("格式异常", warnings)
        self.assertIn("garbage", warnings)
        # Provenance kept: the raw value stays in its channel slot.
        self.assertEqual(card["data_quality"]["channel_as_of"]["quote"], "garbage")

    def test_financial_period_staleness_counts_quarters_across_years(self) -> None:
        # Market 2025-07 (Q3 2025) vs financial 2023Q4: 7 quarters behind → warns.
        card = self._card(financial_period="2023Q4")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("个季度", warnings)

    def test_one_quarter_behind_does_not_warn(self) -> None:
        # Market bars end ~2025-07; financial 2025Q2 is roughly current — no stale warning.
        card = self._card(financial_period="2025Q2")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertNotIn("个季度", warnings)

    def test_bad_period_format_flags_anomaly_not_staleness(self) -> None:
        card = self._card(financial_period="2026XX")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("格式异常", warnings)
        self.assertIn("2026XX", warnings)


class SellInvariantTests(unittest.TestCase):
    """recheck-2 F-02：部分卖出绕过切片校验 + 货币权重不守恒直传。"""

    BASE = {
        "candidate_amount": 5_000.0, "candidate_currency": "CNY",
        "portfolio_value": 100_000.0, "portfolio_currency": "CNY",
        "current_weight_of_symbol": 0.2, "single_position_limit": 0.5,
        "cash_available": 50_000.0,
        "region_weights": {"cn": 0.2, "us": 0.2, "cash": 0.6},
        "currency_weights": {"CNY": 0.6, "USD": 0.4},
        "instrument_region": "us", "instrument_currency": "USD",
    }

    def _impact(self, **extra: object) -> dict[str, object]:
        portfolio = {**self.BASE, **extra}
        card = assemble_card(
            "NASDAQ:AAPL", _bars(200), None, _valuation(), _financials([0.1] * 4), "raw",
            portfolio=portfolio, side="sell",
        ).to_dict()
        return card["portfolio_impact"]

    def test_partial_sell_with_inconsistent_region_slice_fails_closed(self) -> None:
        """复审反例：持仓 20%、US 切片 10%、只卖 5% → 必须拒绝，不得输出 us=5%。

        旧实现只比较 candidate_weight(5%) < 切片(10%)，放行了这张数学上不可能的卡
        （卖出后持仓还剩 15%，却装在只剩 5% 的地区里）。
        """
        impact = self._impact(region_weights={"cn": 0.2, "us": 0.1, "cash": 0.7})

        self.assertEqual(impact["region_weight_after"], {})
        notes = " ".join(impact["notes"])
        self.assertIn("输入不自洽", notes)
        self.assertIn("小于该标的当前持仓", notes)

    def test_sell_with_non_conserving_currency_weights_fails_closed(self) -> None:
        """复审反例：货币输入合计 120% → 不得原样返回 120% 的交易后分布。"""
        impact = self._impact(currency_weights={"CNY": 0.8, "USD": 0.4})

        self.assertEqual(impact["currency_weight_after"], {})
        notes = " ".join(impact["notes"])
        self.assertIn("权重不守恒", notes)

    def test_sell_with_negative_or_nan_weight_fails_closed(self) -> None:
        for bad in ({"CNY": -0.1, "USD": 1.1}, {"CNY": float("nan"), "USD": 0.4}):
            impact = self._impact(currency_weights=bad)
            self.assertEqual(impact["currency_weight_after"], {}, bad)
            notes = " ".join(impact["notes"])
            self.assertIn("非法值", notes)

    def test_every_nonempty_after_distribution_sums_to_one(self) -> None:
        """参数化：正常卖出、部分卖出、超卖——非空 after 分布必须非负/有限/合计 1。"""
        import math

        for amount in (2_000.0, 5_000.0, 90_000.0):
            impact = self._impact(candidate_amount=amount)
            for key in ("region_weight_after", "currency_weight_after"):
                dist = impact[key]
                if not dist:
                    continue
                self.assertTrue(
                    all(math.isfinite(v) and v >= -1e-6 for v in dist.values()),
                    (amount, key, dist))
                self.assertAlmostEqual(sum(dist.values()), 1.0, places=4, msg=f"{amount} {key}")


class StrictDateTests(unittest.TestCase):
    """recheck-2 F-03：紧凑格式拒绝、解析值排序、仅异常财报期也告警。"""

    def test_compact_date_form_is_rejected(self) -> None:
        from caishenfolio_core.research.evidence_card import _is_iso_date

        self.assertFalse(_is_iso_date("20250719"))
        self.assertTrue(_is_iso_date("2025-07-19"))
        # Leap-day round-trips: real leap day passes, fake one fails.
        self.assertTrue(_is_iso_date("2024-02-29"))
        self.assertFalse(_is_iso_date("2025-02-29"))

    def test_mixed_formats_pick_the_true_latest_by_parsed_date(self) -> None:
        # quote is a real date; valuation arrives in the compact form. The headline must
        # be the parsed-latest REAL date, not a lexicographic accident.
        quote = {"ok": True, "provider": "fixture", "data": {
            "price": 10.0, "currency": "CNY", "as_of": "2025-07-20",
            "source_count": 1, "spread_pct": 0.0, "sources": "a", "outliers": ""}}
        valuation = {"ok": True, "provider": "fixture",
                     "data": {"as_of": "20250719"}, "readings": []}

        card = assemble_card(
            "SSE:600000", _bars(200), quote, valuation, None, "raw").to_dict()

        self.assertEqual(card["market_as_of"], "2025-07-20")
        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("格式异常", warnings)
        self.assertIn("20250719", warnings)

    def test_only_a_bad_financial_period_still_warns(self) -> None:
        # No bars/quote/valuation dates at all; financials is malformed.
        financials = {"ok": True, "provider": "fixture", "data": [
            {"period": "2026XX", "revenue": 1.0, "net_profit": 1.0, "eps": 1.0,
             "roe": 0.1, "revenue_growth": 0.1, "profit_growth": 0.05}]}
        # Empty bars so the bars channel has no date either.
        card = assemble_card(
            "SSE:600000", {"ok": True, "provider": "fixture", "data": []},
            None, None, financials, "raw").to_dict()

        warnings = " ".join(card["data_quality"]["warnings"])
        self.assertIn("格式异常", warnings)
        self.assertIn("2026XX", warnings)


class ExternalFundingDilutionTests(unittest.TestCase):
    """recheck-3 R3-01：外部资金买入必须稀释原持仓——旧公式把旧占比原样加上新金额/新总额。"""

    BASE = {
        "candidate_amount": 10_000.0, "candidate_currency": "CNY",
        "portfolio_value": 100_000.0, "portfolio_currency": "CNY",
        "current_weight_of_symbol": 0.2, "single_position_limit": 0.28,
        "cash_available": 50_000.0,
        "region_weights": {"cn": 0.5, "us": 0.2, "cash": 0.3},
        "currency_weights": {"CNY": 0.6, "USD": 0.4},
        "instrument_region": "us", "instrument_currency": "USD",
        "funding": "external",
    }

    def _card(self, **extra: object) -> dict[str, object]:
        portfolio = {**self.BASE, **extra}
        return assemble_card(
            "NASDAQ:AAPL", _bars(200), None, _valuation(), _financials([0.1] * 4), "raw",
            portfolio=portfolio,
        ).to_dict()

    def test_review_counterexample_27_27(self) -> None:
        """复审反例：20% 持仓 + 10 000 外部 → 30 000/110 000 = 27.2727%（非 29.09%）。

        28% 上限在此数据集上不再触发 risk_exceeded。
        """
        card = self._card()
        impact = card["portfolio_impact"]

        self.assertAlmostEqual(
            impact["single_position_weight_after"], 30_000 / 110_000, places=6)
        self.assertAlmostEqual(impact["single_position_weight_before"], 0.2, places=6)
        # The 28% limit is NOT breached — the old formula's 29.09% tripped it wrongly.
        self.assertNotEqual(card["conclusion"]["status"], "risk_exceeded")
        # Cash is untouched by external money but diluted by the new total.
        self.assertAlmostEqual(impact["cash_weight_after"], 50_000 / 110_000, places=6)

    def test_symbol_after_equals_region_after_when_region_holds_only_it(self) -> None:
        """跨维度一致性：US 地区只有此标的时，标的 after == 地区 after。"""
        card = self._card(region_weights={"cn": 0.5, "us": 0.2, "cash": 0.3})
        impact = card["portfolio_impact"]

        us_after = impact["region_weight_after"]["us"]
        self.assertAlmostEqual(
            impact["single_position_weight_after"], us_after, places=6,
            msg="标的 after 与只含它的地区 after 必须一致（旧公式会出现 29.09% vs 27.27%）")

    def test_existing_cash_buy_still_adds_simply(self) -> None:
        """存量现金：总额不变 → 旧公式在该分支本就正确（20% + 10% = 30%），不得回归。"""
        card = self._card(
            funding="existing_cash", funding_currency="CNY",
            region_weights={"cn": 0.6, "us": 0.1, "cash": 0.3})
        impact = card["portfolio_impact"]

        self.assertAlmostEqual(impact["single_position_weight_after"], 0.30, places=6)

    def test_zero_old_position_external_buy(self) -> None:
        card = self._card(current_weight_of_symbol=0.0)
        impact = card["portfolio_impact"]

        # Nothing to dilute: the new position is simply amount / new total.
        self.assertAlmostEqual(impact["single_position_weight_after"], 10_000 / 110_000, places=6)

    def test_symbol_after_never_exceeds_its_region_after(self) -> None:
        """一般情形：标的 after 不大于所属地区/计价货币 after。"""
        card = self._card()
        impact = card["portfolio_impact"]

        self.assertLessEqual(
            impact["single_position_weight_after"],
            impact["region_weight_after"]["us"] + 1e-9)
        self.assertLessEqual(
            impact["single_position_weight_after"],
            impact["currency_weight_after"]["USD"] + 1e-9)


class NoValidMarketDateTests(unittest.TestCase):
    """recheck-3 R3-02：全部市场日期无效时，标题为未知——拒绝的原始值不得经回退冒充数据日。"""

    def _card_with_bars_date(self, bad_date: str) -> dict[str, object]:
        from datetime import date, timedelta
        first = date(2025, 1, 1)
        bars = {"ok": True, "provider": "fixture", "data": [
            _bar((first + timedelta(days=i)).isoformat(), 10.0) for i in range(200)
        ]}
        # Corrupt the LAST bar's date — the exact value the old fallback read.
        bars["data"][-1]["date"] = bad_date
        return assemble_card(
            "SSE:600000", bars, None, None, None, "raw", as_of="2099-01-01").to_dict()

    def test_rejected_values_never_become_the_headline(self) -> None:
        for bad in ("garbage", "20250719", "2026Q1"):
            with self.subTest(bad=bad):
                card = self._card_with_bars_date(bad)
                # Headline is UNKNOWN — not the raw value, not the request date either.
                self.assertEqual(card["market_as_of"], "")
                self.assertEqual(card["as_of"], "")
                # Provenance kept + format warning raised.
                self.assertEqual(card["data_quality"]["channel_as_of"]["bars"], bad)
                warnings = " ".join(card["data_quality"]["warnings"])
                self.assertIn("格式异常", warnings)
                self.assertIn(bad, warnings)

    def test_external_illegal_as_of_never_masquerades(self) -> None:
        # An explicitly passed garbage as_of is a candidate only if strictly valid.
        card = self._card_with_bars_date("2025-07-19")  # valid bar date at the end
        card2 = assemble_card(
            "SSE:600000",
            {"ok": True, "provider": "fixture", "data": []},
            None, None, None, "raw", as_of="garbage").to_dict()
        # No channel dates at all and a garbage request date → unknown headline.
        self.assertEqual(card2["market_as_of"], "")
        self.assertNotIn("garbage", card2["market_as_of"])

    def test_valid_dates_still_work_as_before(self) -> None:
        card = self._card_with_bars_date("2025-07-19")
        self.assertEqual(card["market_as_of"], "2025-07-19")
