from __future__ import annotations

import unittest

from caishenfolio_core.research.indicators import (
    average_true_range,
    bollinger_bands,
    exponential_moving_average,
    macd,
    percentile_rank,
    relative_strength_index,
    simple_moving_average,
)


class AlignmentRulesTests(unittest.TestCase):
    """Every indicator keeps input length and uses None, never 0, for undefined points."""

    def test_series_length_always_matches_the_input(self) -> None:
        values = [float(i) for i in range(30)]
        highs = [v + 1 for v in values]
        lows = [v - 1 for v in values]

        self.assertEqual(len(simple_moving_average(values, 5)), len(values))
        self.assertEqual(len(exponential_moving_average(values, 5)), len(values))
        self.assertEqual(len(relative_strength_index(values, 14)), len(values))
        self.assertEqual(len(bollinger_bands(values, 20).upper), len(values))
        self.assertEqual(len(average_true_range(highs, lows, values, 14)), len(values))
        self.assertEqual(len(macd(values).macd), len(values))

    def test_insufficient_history_is_none_not_zero(self) -> None:
        values = [1.0, 2.0, 3.0]

        sma = simple_moving_average(values, 5)
        self.assertEqual(sma, [None, None, None])
        # A zero here would look like a real price and could trigger a rule.
        self.assertNotIn(0.0, sma)
        self.assertEqual(relative_strength_index(values, 14), [None, None, None])

    def test_leading_points_are_none_until_the_window_fills(self) -> None:
        sma = simple_moving_average([1.0, 2.0, 3.0, 4.0, 5.0], 3)
        self.assertEqual(sma[:2], [None, None])
        self.assertEqual(sma[2], 2.0)

    def test_rejects_an_impossible_window(self) -> None:
        with self.assertRaises(ValueError):
            simple_moving_average([1.0, 2.0], 0)
        with self.assertRaises(ValueError):
            macd([1.0] * 40, fast=26, slow=12)


class MovingAverageTests(unittest.TestCase):
    def test_simple_moving_average_is_the_arithmetic_mean(self) -> None:
        values = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
        sma = simple_moving_average(values, 3)

        self.assertEqual(sma[2], 2.0)  # (1+2+3)/3
        self.assertEqual(sma[3], 3.0)
        self.assertEqual(sma[5], 5.0)  # (4+5+6)/3

    def test_a_flat_series_averages_to_itself(self) -> None:
        sma = simple_moving_average([7.0] * 10, 4)
        self.assertTrue(all(v == 7.0 for v in sma[3:]))

    def test_exponential_moving_average_seeds_from_the_simple_average(self) -> None:
        values = [1.0, 2.0, 3.0, 4.0, 5.0]
        ema = exponential_moving_average(values, 3)

        self.assertEqual(ema[2], 2.0)
        # multiplier = 2/(3+1) = 0.5; (4 - 2) * 0.5 + 2 = 3
        self.assertEqual(ema[3], 3.0)
        self.assertEqual(ema[4], 4.0)

    def test_ema_reacts_faster_than_sma_to_a_jump(self) -> None:
        values = [10.0] * 10 + [20.0]
        sma = simple_moving_average(values, 5)
        ema = exponential_moving_average(values, 5)

        self.assertGreater(ema[-1], sma[-1])


class RsiTests(unittest.TestCase):
    def test_an_unbroken_rise_is_one_hundred(self) -> None:
        rsi = relative_strength_index([float(i) for i in range(1, 30)], 14)
        self.assertEqual(rsi[14], 100.0)

    def test_an_unbroken_fall_is_zero(self) -> None:
        rsi = relative_strength_index([float(i) for i in range(30, 1, -1)], 14)
        self.assertEqual(rsi[14], 0.0)

    def test_a_flat_series_has_no_strength_either_way(self) -> None:
        # No gains and no losses: neither overbought nor oversold, and not a division error.
        rsi = relative_strength_index([5.0] * 30, 14)
        self.assertEqual(rsi[14], 50.0)

    def test_stays_within_bounds_on_mixed_data(self) -> None:
        values = [10, 11, 10.5, 12, 11.5, 13, 12.5, 14, 13, 15, 14.5, 16, 15, 17, 16.5, 18, 17]
        rsi = relative_strength_index([float(v) for v in values], 14)

        for point in rsi:
            if point is not None:
                self.assertGreaterEqual(point, 0.0)
                self.assertLessEqual(point, 100.0)


class BollingerTests(unittest.TestCase):
    def test_bands_collapse_onto_the_mean_when_there_is_no_variance(self) -> None:
        bands = bollinger_bands([5.0] * 25, 20)

        self.assertEqual(bands.middle[19], 5.0)
        self.assertEqual(bands.upper[19], 5.0)
        self.assertEqual(bands.lower[19], 5.0)

    def test_upper_and_lower_are_symmetric_about_the_middle(self) -> None:
        values = [10.0, 12.0, 11.0, 13.0, 12.0, 14.0, 13.0, 15.0, 14.0, 16.0]
        bands = bollinger_bands(values, 5, deviations=2.0)

        for i in range(4, len(values)):
            middle = bands.middle[i]
            self.assertAlmostEqual(bands.upper[i] - middle, middle - bands.lower[i], places=10)

    def test_wider_deviation_setting_widens_the_bands(self) -> None:
        values = [10.0, 12.0, 11.0, 13.0, 12.0, 14.0]
        narrow = bollinger_bands(values, 5, deviations=1.0)
        wide = bollinger_bands(values, 5, deviations=3.0)

        self.assertGreater(wide.upper[-1], narrow.upper[-1])


class MacdTests(unittest.TestCase):
    def test_histogram_is_the_gap_between_macd_and_signal(self) -> None:
        values = [float(i) for i in range(1, 80)]
        result = macd(values)

        for m, s, h in zip(result.macd, result.signal, result.histogram):
            if m is not None and s is not None:
                self.assertAlmostEqual(h, m - s, places=10)
            else:
                self.assertIsNone(h)

    def test_macd_is_positive_while_the_series_rises(self) -> None:
        result = macd([float(i) for i in range(1, 80)])
        self.assertGreater(result.macd[-1], 0)

    def test_signal_line_stays_aligned_with_its_bar(self) -> None:
        values = [float(i) for i in range(1, 80)]
        result = macd(values)

        # The signal cannot begin before the MACD line it smooths.
        first_macd = next(i for i, v in enumerate(result.macd) if v is not None)
        first_signal = next(i for i, v in enumerate(result.signal) if v is not None)
        self.assertGreater(first_signal, first_macd)


class AtrTests(unittest.TestCase):
    def test_constant_range_gives_that_range(self) -> None:
        closes = [10.0] * 30
        highs = [11.0] * 30
        lows = [9.0] * 30

        atr = average_true_range(highs, lows, closes, 14)
        self.assertAlmostEqual(atr[14], 2.0, places=10)

    def test_a_gap_counts_toward_true_range(self) -> None:
        closes = [10.0] * 20 + [20.0]
        highs = [10.5] * 20 + [20.5]
        lows = [9.5] * 20 + [19.5]

        atr = average_true_range(highs, lows, closes, 14)
        # The gap from 10 to 19.5 is larger than the intraday range, so ATR must rise.
        self.assertGreater(atr[-1], atr[-2])

    def test_mismatched_series_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            average_true_range([1.0, 2.0], [1.0], [1.0, 2.0], 14)


class PercentileRankTests(unittest.TestCase):
    def test_reports_position_within_a_history(self) -> None:
        history = [float(i) for i in range(1, 101)]

        self.assertAlmostEqual(percentile_rank(history, 0.5), 0.0)
        self.assertAlmostEqual(percentile_rank(history, 100.5), 100.0)
        self.assertAlmostEqual(percentile_rank(history, 50.5), 50.0)

    def test_ties_land_in_the_middle_rather_than_at_an_extreme(self) -> None:
        # An unchanged value must not swing between 0% and 100%.
        self.assertAlmostEqual(percentile_rank([5.0] * 10, 5.0), 50.0)

    def test_empty_history_has_no_rank(self) -> None:
        self.assertIsNone(percentile_rank([], 10.0))

    def test_a_cheap_valuation_ranks_low(self) -> None:
        # Ten years of PE between 20 and 60; today's 22 is near the bottom of its own range.
        history = [20.0, 25.0, 30.0, 35.0, 40.0, 45.0, 50.0, 55.0, 60.0, 28.0]
        rank = percentile_rank(history, 22.0)

        self.assertIsNotNone(rank)
        self.assertLess(rank, 20.0)


if __name__ == "__main__":
    unittest.main()
