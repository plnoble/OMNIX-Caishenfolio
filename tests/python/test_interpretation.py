from __future__ import annotations

import unittest

from caishenfolio_core.research.interpretation import (
    MIN_CONDITIONAL_SAMPLES,
    band_for,
    conditional_forward_outcome,
    expanding_percentiles,
    explain,
    read_metric,
)


class BandTests(unittest.TestCase):
    def test_each_region_gets_a_name(self) -> None:
        cases = [
            (5.0, "历史低位"),
            (20.0, "历史低位"),
            (30.0, "偏低"),
            (50.0, "中性"),
            (70.0, "偏高"),
            (95.0, "历史高位"),
        ]
        for percentile, expected in cases:
            with self.subTest(percentile=percentile):
                self.assertEqual(band_for(percentile).label, expected)

    def test_no_percentile_means_no_band(self) -> None:
        self.assertIsNone(band_for(None))

    def test_a_band_names_a_position_without_telling_anyone_what_to_do(self) -> None:
        for percentile in (5.0, 50.0, 95.0):
            band = band_for(percentile)
            for word in ("买入", "卖出", "建议", "应该"):
                self.assertNotIn(word, band.label)
                self.assertNotIn(word, band.description)


class ExplanationTests(unittest.TestCase):
    def test_each_indicator_says_what_it_is_how_to_read_it_and_what_it_hides(self) -> None:
        for name in ("市盈率 PE", "市净率 PB", "股息率", "carry"):
            with self.subTest(name=name):
                text = explain(name)
                self.assertIn("what", text)
                self.assertIn("read", text)
                # The caveat is the part a beginner most needs and is most often omitted.
                self.assertIn("caveat", text)
                self.assertTrue(text["caveat"])

    def test_the_pe_caveat_warns_that_cheap_can_mean_broken(self) -> None:
        self.assertIn("低 PE 不等于安全", explain("市盈率 PE")["caveat"])

    def test_an_unknown_indicator_returns_nothing_rather_than_inventing(self) -> None:
        self.assertEqual(explain("不存在的指标"), {})


class ExpandingPercentileTests(unittest.TestCase):
    def test_early_points_have_no_percentile_until_enough_history_exists(self) -> None:
        result = expanding_percentiles([float(i) for i in range(100)], minimum=60)

        self.assertTrue(all(v is None for v in result[:60]))
        self.assertIsNotNone(result[60])

    def test_each_point_is_ranked_only_against_its_own_past(self) -> None:
        # Rising series: every new point is the highest so far, so each ranks at the top.
        result = expanding_percentiles([float(i) for i in range(100)], minimum=60)

        for value in result[60:]:
            self.assertGreater(value, 95.0)

    def test_a_falling_series_ranks_at_the_bottom(self) -> None:
        result = expanding_percentiles([float(100 - i) for i in range(100)], minimum=60)

        for value in result[60:]:
            self.assertLess(value, 5.0)

    def test_missing_values_stay_missing(self) -> None:
        values = [1.0] * 60 + [None, 2.0]
        result = expanding_percentiles(values, minimum=60)

        self.assertIsNone(result[60])
        self.assertIsNotNone(result[61])


class ConditionalOutcomeTests(unittest.TestCase):
    def _rising_history(self, n: int = 400) -> tuple[list[float | None], list[float]]:
        # Metric falls while price rises, so "cheap" days precede gains.
        metrics = [float(100 - i * 0.2) for i in range(n)]
        closes = [10.0 + i * 0.05 for i in range(n)]
        return metrics, closes

    def test_reports_what_followed_a_comparable_starting_point(self) -> None:
        metrics, closes = self._rising_history()

        outcome = conditional_forward_outcome(metrics, closes, 0.0, 20.0, horizon_days=60)

        self.assertGreater(outcome.samples, 0)
        self.assertIsNotNone(outcome.median_return)
        self.assertIsNotNone(outcome.worst_return)
        # The worst case must always be reported alongside the median.
        self.assertLessEqual(outcome.worst_return, outcome.median_return)
        self.assertGreaterEqual(outcome.best_return, outcome.median_return)

    def test_no_comparable_history_reports_zero_samples_not_a_number(self) -> None:
        metrics, closes = self._rising_history()

        outcome = conditional_forward_outcome(metrics, closes, 99.9, 100.0, horizon_days=60)

        self.assertEqual(outcome.samples, 0)
        self.assertIsNone(outcome.median_return)
        self.assertIn("没有出现过可比的情形", outcome.summary())

    def test_a_thin_sample_is_declared_unreliable(self) -> None:
        metrics = [float(i) for i in range(70)]
        closes = [10.0 + i * 0.1 for i in range(70)]

        outcome = conditional_forward_outcome(metrics, closes, 0.0, 100.0, horizon_days=5)

        self.assertLess(outcome.samples, MIN_CONDITIONAL_SAMPLES)
        self.assertFalse(outcome.is_reliable)
        self.assertIn("样本太少", outcome.summary())

    def test_win_rate_counts_only_gains(self) -> None:
        # Flat metric so every day qualifies; price alternates up and down over the horizon.
        metrics = [50.0] * 200
        closes = [10.0 + (1.0 if i % 2 else 0.0) for i in range(200)]

        outcome = conditional_forward_outcome(metrics, closes, 0.0, 100.0, horizon_days=1)

        self.assertIsNotNone(outcome.win_rate)
        self.assertGreater(outcome.win_rate, 0.0)
        self.assertLess(outcome.win_rate, 1.0)

    def test_the_summary_always_states_the_worst_case(self) -> None:
        metrics, closes = self._rising_history()
        outcome = conditional_forward_outcome(metrics, closes, 0.0, 40.0, horizon_days=60)

        self.assertTrue(outcome.is_reliable)
        self.assertIn("最差", outcome.summary())
        self.assertIn("中位数", outcome.summary())

    def test_mismatched_series_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            conditional_forward_outcome([1.0, 2.0], [10.0], 0.0, 100.0, 5)

    def test_an_impossible_horizon_is_refused(self) -> None:
        with self.assertRaises(ValueError):
            conditional_forward_outcome([1.0] * 100, [10.0] * 100, 0.0, 100.0, 0)

    def test_percentiles_never_look_ahead(self) -> None:
        # A metric that is low early and high later: with full-sample ranking the early days
        # would rank low, but expanding ranking cannot know the later highs yet.
        metrics = [10.0] * 100 + [90.0] * 100
        percentiles = expanding_percentiles(metrics, minimum=60)

        # Day 60 sees only identical past values, so it ranks mid, not bottom.
        self.assertAlmostEqual(percentiles[60], 50.0, places=6)


class ReadMetricTests(unittest.TestCase):
    def test_assembles_band_explanation_and_outcomes(self) -> None:
        metrics = [float(100 - i * 0.2) for i in range(400)]
        closes = [10.0 + i * 0.05 for i in range(400)]

        reading = read_metric("市盈率 PE", current=20.0, percentile=8.0,
                              metric_values=metrics, closes=closes)

        self.assertEqual(reading.band.label, "历史低位")
        self.assertIn("回本", reading.explanation["what"])
        self.assertEqual(len(reading.outcomes), 2)
        self.assertTrue(any("历史统计，不是预测" in note for note in reading.notes))

    def test_without_history_it_says_so_instead_of_guessing(self) -> None:
        reading = read_metric("市盈率 PE", current=20.0, percentile=None)

        self.assertIsNone(reading.band)
        self.assertEqual(reading.outcomes, [])
        self.assertTrue(any("无法判断当前处于什么位置" in note for note in reading.notes))

    def test_the_reading_never_instructs(self) -> None:
        metrics = [float(100 - i * 0.2) for i in range(400)]
        closes = [10.0 + i * 0.05 for i in range(400)]
        reading = read_metric("市盈率 PE", 20.0, 8.0, metrics, closes)

        blob = str(reading.to_dict())
        for word in ("建议买入", "建议卖出", "应该买", "应该卖", "推荐买"):
            self.assertNotIn(word, blob)

    def test_every_reading_carries_the_caveat_that_history_can_break(self) -> None:
        reading = read_metric("股息率", 0.05, 90.0)

        self.assertTrue(
            any("完全可能走出历史上没出现过的结果" in note for note in reading.notes)
        )


if __name__ == "__main__":
    unittest.main()
