"""Technical indicators over a close/OHLC series.

Two rules run through this module:

* A point with insufficient history is ``None``, never 0. A zero would silently become a
  tradable signal in a backtest and a visible line on a chart.
* Every series returned is the same length as its input, so indicator[i] always lines up with
  bar[i] and callers cannot misalign them by slicing.

These are descriptive calculations. Nothing here decides to buy or sell; rules that consume
these values live in the backtest module and are supplied by the user.
"""

from __future__ import annotations

from dataclasses import dataclass

Series = list[float | None]


def simple_moving_average(values: list[float], window: int) -> Series:
    """Arithmetic mean of the last ``window`` points."""
    _require_window(window)
    out: Series = [None] * len(values)
    if window > len(values):
        return out

    running = sum(values[:window])
    out[window - 1] = running / window
    for i in range(window, len(values)):
        running += values[i] - values[i - window]
        out[i] = running / window
    return out


def exponential_moving_average(values: list[float], window: int) -> Series:
    """EMA seeded with the first SMA, which is the convention charting packages use."""
    _require_window(window)
    out: Series = [None] * len(values)
    if window > len(values):
        return out

    multiplier = 2.0 / (window + 1)
    previous = sum(values[:window]) / window
    out[window - 1] = previous
    for i in range(window, len(values)):
        previous = (values[i] - previous) * multiplier + previous
        out[i] = previous
    return out


@dataclass(frozen=True, slots=True)
class MacdResult:
    macd: Series
    signal: Series
    histogram: Series


def macd(
    values: list[float],
    fast: int = 12,
    slow: int = 26,
    signal: int = 9,
) -> MacdResult:
    """MACD line, its signal EMA, and the histogram between them."""
    if fast >= slow:
        raise ValueError(f"快线周期必须小于慢线（收到 fast={fast}, slow={slow}）。")

    fast_ema = exponential_moving_average(values, fast)
    slow_ema = exponential_moving_average(values, slow)
    macd_line: Series = [
        None if f is None or s is None else f - s for f, s in zip(fast_ema, slow_ema)
    ]

    # The signal EMA is computed over the defined part of the MACD line only, then written back
    # into place, so the leading Nones do not shift it.
    defined = [(i, v) for i, v in enumerate(macd_line) if v is not None]
    signal_line: Series = [None] * len(values)
    if len(defined) >= signal:
        raw = exponential_moving_average([v for _, v in defined], signal)
        for (index, _), computed in zip(defined, raw):
            signal_line[index] = computed

    histogram: Series = [
        None if m is None or s is None else m - s for m, s in zip(macd_line, signal_line)
    ]
    return MacdResult(macd_line, signal_line, histogram)


def relative_strength_index(values: list[float], window: int = 14) -> Series:
    """Wilder's RSI. A run with no losses is 100 by definition, not a division error."""
    _require_window(window)
    out: Series = [None] * len(values)
    if len(values) <= window:
        return out

    gains = 0.0
    losses = 0.0
    for i in range(1, window + 1):
        change = values[i] - values[i - 1]
        gains += max(change, 0.0)
        losses += max(-change, 0.0)

    avg_gain = gains / window
    avg_loss = losses / window
    out[window] = _rsi_from(avg_gain, avg_loss)

    for i in range(window + 1, len(values)):
        change = values[i] - values[i - 1]
        # Wilder smoothing, not a plain moving average.
        avg_gain = (avg_gain * (window - 1) + max(change, 0.0)) / window
        avg_loss = (avg_loss * (window - 1) + max(-change, 0.0)) / window
        out[i] = _rsi_from(avg_gain, avg_loss)

    return out


@dataclass(frozen=True, slots=True)
class BollingerBands:
    upper: Series
    middle: Series
    lower: Series


def bollinger_bands(
    values: list[float],
    window: int = 20,
    deviations: float = 2.0,
) -> BollingerBands:
    """Middle SMA with bands at ``deviations`` population standard deviations."""
    _require_window(window)
    middle = simple_moving_average(values, window)
    upper: Series = [None] * len(values)
    lower: Series = [None] * len(values)

    for i in range(window - 1, len(values)):
        mean = middle[i]
        if mean is None:
            continue
        window_values = values[i - window + 1 : i + 1]
        variance = sum((v - mean) ** 2 for v in window_values) / window
        spread = deviations * (variance ** 0.5)
        upper[i] = mean + spread
        lower[i] = mean - spread

    return BollingerBands(upper, middle, lower)


def average_true_range(
    highs: list[float],
    lows: list[float],
    closes: list[float],
    window: int = 14,
) -> Series:
    """Wilder's ATR — the volatility measure position sizing and stops are built on."""
    _require_window(window)
    if not (len(highs) == len(lows) == len(closes)):
        raise ValueError("最高价、最低价、收盘价序列长度必须一致。")

    out: Series = [None] * len(closes)
    if len(closes) <= window:
        return out

    true_ranges = [highs[0] - lows[0]]
    for i in range(1, len(closes)):
        previous_close = closes[i - 1]
        true_ranges.append(
            max(
                highs[i] - lows[i],
                abs(highs[i] - previous_close),
                abs(lows[i] - previous_close),
            )
        )

    average = sum(true_ranges[1 : window + 1]) / window
    out[window] = average
    for i in range(window + 1, len(closes)):
        average = (average * (window - 1) + true_ranges[i]) / window
        out[i] = average

    return out


def percentile_rank(values: list[float], current: float) -> float | None:
    """Where ``current`` sits within ``values``, as a percentage.

    This is what turns "PE is 28" into "PE is at the 12th percentile of its own ten-year
    history" — a fact about position in a distribution, not a judgement about value.
    """
    if not values:
        return None
    below = sum(1 for v in values if v < current)
    equal = sum(1 for v in values if v == current)
    # Midpoint of ties, so an unchanged value does not jump between 0% and 100%.
    return (below + equal / 2.0) / len(values) * 100.0


def _rsi_from(avg_gain: float, avg_loss: float) -> float:
    if avg_loss == 0.0:
        return 100.0 if avg_gain > 0.0 else 50.0
    return 100.0 - 100.0 / (1.0 + avg_gain / avg_loss)


def _require_window(window: int) -> None:
    if window < 1:
        raise ValueError(f"周期必须至少为 1（收到 {window}）。")
