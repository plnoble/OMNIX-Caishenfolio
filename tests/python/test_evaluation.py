from __future__ import annotations

import unittest
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

from caishenfolio_core.research.evaluation import (
    OutOfSampleReport,
    describe,
    evaluate,
    out_of_sample_report,
    split_bars,
)


def _curve(values: list[float]) -> list[dict[str, Any]]:
    return [{"equity": v} for v in values]


def _trades(returns: list[float]) -> list[dict[str, Any]]:
    return [{"side": "sell", "trade_return_grossish": r} for r in returns]


class MetricsTests(unittest.TestCase):
    def test_total_return_comes_from_the_curve_ends(self) -> None:
        metrics = evaluate(_curve([1.0, 1.1, 1.2]))
        self.assertAlmostEqual(metrics.total_return, 0.2, places=10)

    def test_max_drawdown_measures_peak_to_trough_and_its_length(self) -> None:
        # Rises to 1.5, falls to 1.2 four bars later, then recovers.
        metrics = evaluate(_curve([1.0, 1.5, 1.4, 1.3, 1.2, 1.6]))

        self.assertAlmostEqual(metrics.max_drawdown, -0.2, places=10)
        self.assertEqual(metrics.max_drawdown_bars, 3)

    def test_a_curve_that_only_rises_has_no_drawdown(self) -> None:
        metrics = evaluate(_curve([1.0, 1.1, 1.2, 1.3]))
        self.assertEqual(metrics.max_drawdown, 0.0)

    def test_win_rate_and_profit_factor(self) -> None:
        metrics = evaluate(_curve([1.0, 1.2]), _trades([0.10, -0.05, 0.20, -0.05]))

        self.assertEqual(metrics.trades, 4)
        self.assertAlmostEqual(metrics.win_rate, 0.5, places=10)
        # (0.10 + 0.20) / (0.05 + 0.05)
        self.assertAlmostEqual(metrics.profit_factor, 3.0, places=10)
        self.assertAlmostEqual(metrics.average_win, 0.15, places=10)
        self.assertAlmostEqual(metrics.average_loss, -0.05, places=10)

    def test_counts_the_longest_losing_run(self) -> None:
        metrics = evaluate(_curve([1.0, 0.9]), _trades([-0.01, -0.02, 0.05, -0.01, -0.01, -0.01]))
        self.assertEqual(metrics.max_consecutive_losses, 3)

    def test_a_rule_that_never_loses_has_no_profit_factor_rather_than_infinity(self) -> None:
        metrics = evaluate(_curve([1.0, 1.5]), _trades([0.1, 0.2]))

        self.assertEqual(metrics.win_rate, 1.0)
        self.assertIsNone(metrics.profit_factor)
        self.assertEqual(metrics.max_consecutive_losses, 0)

    def test_excess_over_buy_hold_can_be_negative(self) -> None:
        metrics = evaluate(_curve([1.0, 1.05]), buy_hold_return=0.30)

        self.assertAlmostEqual(metrics.total_return, 0.05, places=10)
        # Beating nothing at all is the bar that matters.
        self.assertAlmostEqual(metrics.excess_over_buy_hold, -0.25, places=10)

    def test_return_over_max_drawdown_prices_the_pain(self) -> None:
        metrics = evaluate(_curve([1.0, 1.5, 1.2, 2.0]))

        self.assertIsNotNone(metrics.return_over_max_drawdown)
        self.assertAlmostEqual(metrics.return_over_max_drawdown, 1.0 / 0.2, places=6)

    def test_too_short_a_curve_reports_nothing_rather_than_zero(self) -> None:
        metrics = evaluate(_curve([1.0]))

        self.assertIsNone(metrics.total_return)
        self.assertIsNone(metrics.max_drawdown)
        self.assertIsNone(metrics.win_rate)


@dataclass
class _FakeResult:
    equity_curve: list[dict[str, Any]]
    trade_log: list[dict[str, Any]]
    buy_hold_return: float | None = None


@dataclass
class _Bar:
    timestamp_utc: datetime
    close: float


def _bars(count: int) -> list[_Bar]:
    start = datetime(2020, 1, 1, tzinfo=timezone.utc)
    return [_Bar(start + timedelta(days=i), 10.0 + i * 0.01) for i in range(count)]


class SplitTests(unittest.TestCase):
    def test_split_is_chronological(self) -> None:
        bars = _bars(100)
        first, second = split_bars(bars, 0.7)

        self.assertEqual(len(first), 70)
        self.assertEqual(len(second), 30)
        # A shuffled split would let the rule see the future.
        self.assertLess(first[-1].timestamp_utc, second[0].timestamp_utc)

    def test_rejects_an_impossible_ratio(self) -> None:
        for ratio in (0.0, 1.0, -0.5, 1.5):
            with self.subTest(ratio=ratio):
                with self.assertRaises(ValueError):
                    split_bars(_bars(10), ratio)

    def test_too_little_data_reports_nothing(self) -> None:
        # Twenty bars cannot support a claim; saying nothing beats a made-up number.
        self.assertIsNone(out_of_sample_report(_bars(20), lambda b: _FakeResult(_curve([1.0]), [])))


class OutOfSampleTests(unittest.TestCase):
    def _report(self, in_curve: list[float], out_curve: list[float], out_trades: list[float],
                buy_hold: float | None = None) -> OutOfSampleReport:
        calls: list[int] = []

        def run(bars: list[_Bar]) -> _FakeResult:
            calls.append(len(bars))
            if len(calls) == 1:
                return _FakeResult(_curve(in_curve), _trades([0.05] * 20))
            return _FakeResult(_curve(out_curve), _trades(out_trades), buy_hold)

        report = out_of_sample_report(_bars(400), run, ratio=0.7)
        assert report is not None
        return report

    def test_a_rule_that_collapses_out_of_sample_is_called_out(self) -> None:
        # Doubles in sample, loses money out of sample.
        report = self._report([1.0 + i * 0.005 for i in range(280)],
                              [1.0 - i * 0.002 for i in range(120)],
                              [-0.02] * 8)

        self.assertFalse(report.survives_out_of_sample)
        self.assertTrue(any("亏损" in f for f in report.findings))
        self.assertTrue(any("过拟合" in f for f in report.findings))

    def test_losing_to_buy_and_hold_is_stated_plainly(self) -> None:
        report = self._report([1.0 + i * 0.003 for i in range(280)],
                              [1.0 + i * 0.0001 for i in range(120)],
                              [0.01] * 12,
                              buy_hold=0.50)

        self.assertTrue(any("跑输买入持有" in f for f in report.findings))

    def test_a_thin_sample_is_flagged(self) -> None:
        report = self._report([1.0 + i * 0.003 for i in range(280)],
                              [1.0 + i * 0.001 for i in range(120)],
                              [0.02, 0.03])

        self.assertTrue(any("样本太小" in f for f in report.findings))

    def test_a_long_losing_run_is_flagged(self) -> None:
        report = self._report([1.0 + i * 0.003 for i in range(280)],
                              [1.0 + i * 0.001 for i in range(120)],
                              [-0.01] * 6 + [0.30] * 10)

        self.assertTrue(any("连续亏损" in f for f in report.findings))

    def test_no_trades_out_of_sample_says_so_and_stops(self) -> None:
        report = self._report([1.0 + i * 0.003 for i in range(280)],
                              [1.0] * 120,
                              [])

        self.assertFalse(report.survives_out_of_sample)
        self.assertEqual(len(report.findings), 1)
        self.assertIn("没有产生任何交易", report.findings[0])

    def test_a_rule_that_holds_up_still_carries_a_caution(self) -> None:
        report = self._report([1.0 + i * 0.002 for i in range(280)],
                              [1.0 + i * 0.002 for i in range(120)],
                              [0.02] * 15,
                              buy_hold=0.05)

        self.assertTrue(report.survives_out_of_sample)
        # Passing once is not evidence, and the wording has to say so.
        self.assertTrue(any("单次回测不构成证据" in f for f in report.findings))

    def test_the_report_carries_the_disclaimer(self) -> None:
        report = self._report([1.0 + i * 0.002 for i in range(280)],
                              [1.0 + i * 0.002 for i in range(120)],
                              [0.02] * 15)

        payload = report.to_dict()
        self.assertIn("非投资建议", str(payload["disclaimer"]))
        self.assertIn("历史表现不代表未来", str(payload["disclaimer"]))

    def test_findings_never_instruct(self) -> None:
        report = self._report([1.0 + i * 0.002 for i in range(280)],
                              [1.0 + i * 0.002 for i in range(120)],
                              [0.02] * 15)

        for finding in report.findings:
            for word in ("建议买", "建议卖", "应该买", "应该卖", "推荐"):
                self.assertNotIn(word, finding)


class DescribeTests(unittest.TestCase):
    def test_describe_works_on_a_bare_report(self) -> None:
        metrics = evaluate(_curve([1.0, 1.1]), _trades([0.05] * 12))
        report = OutOfSampleReport(70, "2020-03-11", metrics, metrics, 0.0, [])

        self.assertTrue(describe(report))


if __name__ == "__main__":
    unittest.main()
