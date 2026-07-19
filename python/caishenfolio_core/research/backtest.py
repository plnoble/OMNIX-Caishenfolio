from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass
class BacktestResult:
    ok: bool
    strategy: str
    symbol: str
    bar_count: int
    trades: int
    total_return: float | None
    buy_hold_return: float | None
    max_drawdown: float | None
    equity_curve: list[dict[str, Any]]
    trade_log: list[dict[str, Any]]
    warnings: list[str]
    error: str | None = None

    def to_dict(self) -> dict[str, object]:
        return {
            "ok": self.ok,
            "strategy": self.strategy,
            "symbol": self.symbol,
            "bar_count": self.bar_count,
            "trades": self.trades,
            "total_return": self.total_return,
            "buy_hold_return": self.buy_hold_return,
            "max_drawdown": self.max_drawdown,
            "equity_curve": self.equity_curve,
            "trade_log": self.trade_log,
            "warnings": self.warnings,
            "error": self.error,
            "disclaimer": "研究/模拟结论，非投资建议。",
        }


def ma_cross_backtest(
    bars: list[dict[str, Any]],
    *,
    symbol: str,
    fast: int = 5,
    slow: int = 20,
) -> BacktestResult:
    """Simple long-only MA crossover on close prices (simulation only)."""
    warnings = ["simulation_only", "not_for_investment_decisions"]
    if fast < 1 or slow <= fast:
        return BacktestResult(
            False,
            "ma_cross",
            symbol,
            0,
            0,
            None,
            None,
            None,
            [],
            [],
            warnings,
            "fast 必须 < slow，且均为正整数。",
        )
    if len(bars) < slow + 2:
        return BacktestResult(
            False,
            "ma_cross",
            symbol,
            len(bars),
            0,
            None,
            None,
            None,
            [],
            [],
            warnings,
            f"K线不足：需要至少 {slow + 2} 根，当前 {len(bars)}。",
        )

    closes = [float(b["close"]) for b in bars]
    timestamps = [str(b.get("timestamp_utc") or "") for b in bars]

    def ma(i: int, n: int) -> float:
        return sum(closes[i - n + 1 : i + 1]) / n

    cash = 1.0
    position = 0.0
    entry = 0.0
    trades = 0
    trade_log: list[dict[str, Any]] = []
    equity_curve: list[dict[str, Any]] = []
    peak = 1.0
    max_dd = 0.0

    for i in range(slow - 1, len(closes)):
        f = ma(i, fast)
        s = ma(i, slow)
        price = closes[i]
        # cross up
        if i > slow - 1:
            f_prev = ma(i - 1, fast)
            s_prev = ma(i - 1, slow)
            if position == 0 and f_prev <= s_prev and f > s:
                position = cash / price
                cash = 0.0
                entry = price
                trades += 1
                trade_log.append(
                    {
                        "side": "buy",
                        "index": i,
                        "timestamp_utc": timestamps[i],
                        "price": price,
                        "fast_ma": f,
                        "slow_ma": s,
                    }
                )
            elif position > 0 and f_prev >= s_prev and f < s:
                cash = position * price
                ret = (price / entry) - 1.0 if entry else 0.0
                trade_log.append(
                    {
                        "side": "sell",
                        "index": i,
                        "timestamp_utc": timestamps[i],
                        "price": price,
                        "trade_return": ret,
                        "fast_ma": f,
                        "slow_ma": s,
                    }
                )
                position = 0.0
                entry = 0.0
                trades += 1

        equity = cash + position * price
        peak = max(peak, equity)
        dd = (equity / peak) - 1.0 if peak else 0.0
        max_dd = min(max_dd, dd)
        equity_curve.append(
            {
                "timestamp_utc": timestamps[i],
                "equity": equity,
                "close": price,
                "fast_ma": f,
                "slow_ma": s,
            }
        )

    # force mark-to-market at end
    last = closes[-1]
    final_equity = cash + position * last
    total_return = final_equity - 1.0
    buy_hold = (closes[-1] / closes[slow - 1]) - 1.0

    return BacktestResult(
        True,
        f"ma_cross_{fast}_{slow}",
        symbol,
        len(bars),
        trades,
        total_return,
        buy_hold,
        max_dd,
        equity_curve[-200:],  # cap payload size
        trade_log[-50:],
        warnings,
        None,
    )
