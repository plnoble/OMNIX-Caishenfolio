from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass
class CostModel:
    """Simple A-share-ish cost model for research simulation (not broker-accurate)."""

    commission_rate: float = 0.0003  # 佣金比例（买卖）
    commission_min: float = 0.0  # 单位净值回测下通常用比例即可；可为 0
    stamp_duty_rate: float = 0.0005  # 印花税（仅卖出，A股股票常见口径简化）
    slippage_rate: float = 0.0005  # 滑点：买上浮、卖下浮
    limit_up_pct: float = 0.10  # 涨停幅度（相对前收）
    limit_down_pct: float = 0.10  # 跌停幅度
    enforce_limit: bool = True  # 涨停不买、跌停不卖

    def to_dict(self) -> dict[str, object]:
        return {
            "commission_rate": self.commission_rate,
            "commission_min": self.commission_min,
            "stamp_duty_rate": self.stamp_duty_rate,
            "slippage_rate": self.slippage_rate,
            "limit_up_pct": self.limit_up_pct,
            "limit_down_pct": self.limit_down_pct,
            "enforce_limit": self.enforce_limit,
        }


def cost_model_from_dict(raw: dict[str, Any] | None) -> CostModel:
    if not raw:
        return CostModel()
    return CostModel(
        commission_rate=float(raw.get("commission_rate", 0.0003)),
        commission_min=float(raw.get("commission_min", 0.0)),
        stamp_duty_rate=float(raw.get("stamp_duty_rate", 0.0005)),
        slippage_rate=float(raw.get("slippage_rate", 0.0005)),
        limit_up_pct=float(raw.get("limit_up_pct", 0.10)),
        limit_down_pct=float(raw.get("limit_down_pct", 0.10)),
        enforce_limit=bool(raw.get("enforce_limit", True)),
    )


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
    cost_model: dict[str, object] = field(default_factory=dict)
    skipped_signals: int = 0

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
            "cost_model": self.cost_model,
            "skipped_signals": self.skipped_signals,
            "disclaimer": "研究/模拟结论，非投资建议。",
        }


def _is_limit_up(close: float, prev_close: float, pct: float) -> bool:
    if prev_close <= 0:
        return False
    # 允许微小浮点误差
    return close >= prev_close * (1.0 + pct) - 1e-9


def _is_limit_down(close: float, prev_close: float, pct: float) -> bool:
    if prev_close <= 0:
        return False
    return close <= prev_close * (1.0 - pct) + 1e-9


def ma_cross_backtest(
    bars: list[dict[str, Any]],
    *,
    symbol: str,
    fast: int = 5,
    slow: int = 20,
    costs: CostModel | None = None,
) -> BacktestResult:
    """Long-only MA crossover with optional fees/slippage/limit rules."""
    costs = costs or CostModel()
    warnings = [
        "simulation_only",
        "not_for_investment_decisions",
        "cost_model_simplified",
        "limit_rules_use_close_vs_prev_close",
    ]
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
            costs.to_dict(),
            0,
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
            costs.to_dict(),
            0,
        )

    closes = [float(b["close"]) for b in bars]
    timestamps = [str(b.get("timestamp_utc") or "") for b in bars]

    def ma(i: int, n: int) -> float:
        return sum(closes[i - n + 1 : i + 1]) / n

    cash = 1.0
    position = 0.0
    entry = 0.0
    trades = 0
    skipped = 0
    trade_log: list[dict[str, Any]] = []
    equity_curve: list[dict[str, Any]] = []
    peak = 1.0
    max_dd = 0.0
    total_fees = 0.0

    for i in range(slow - 1, len(closes)):
        f = ma(i, fast)
        s = ma(i, slow)
        price = closes[i]
        prev = closes[i - 1] if i > 0 else price
        limit_up = _is_limit_up(price, prev, costs.limit_up_pct)
        limit_down = _is_limit_down(price, prev, costs.limit_down_pct)

        if i > slow - 1:
            f_prev = ma(i - 1, fast)
            s_prev = ma(i - 1, slow)
            # buy signal
            if position == 0 and f_prev <= s_prev and f > s:
                if costs.enforce_limit and limit_up:
                    skipped += 1
                    trade_log.append(
                        {
                            "side": "skip_buy_limit_up",
                            "index": i,
                            "timestamp_utc": timestamps[i],
                            "price": price,
                            "prev_close": prev,
                        }
                    )
                else:
                    buy_px = price * (1.0 + costs.slippage_rate)
                    # commission on notional
                    notional = cash
                    fee = max(costs.commission_min, notional * costs.commission_rate)
                    if fee >= cash:
                        skipped += 1
                    else:
                        spendable = cash - fee
                        position = spendable / buy_px
                        cash = 0.0
                        entry = buy_px
                        trades += 1
                        total_fees += fee
                        trade_log.append(
                            {
                                "side": "buy",
                                "index": i,
                                "timestamp_utc": timestamps[i],
                                "signal_price": price,
                                "fill_price": buy_px,
                                "fee": fee,
                                "slippage_rate": costs.slippage_rate,
                                "fast_ma": f,
                                "slow_ma": s,
                            }
                        )
            # sell signal
            elif position > 0 and f_prev >= s_prev and f < s:
                if costs.enforce_limit and limit_down:
                    skipped += 1
                    trade_log.append(
                        {
                            "side": "skip_sell_limit_down",
                            "index": i,
                            "timestamp_utc": timestamps[i],
                            "price": price,
                            "prev_close": prev,
                        }
                    )
                else:
                    sell_px = price * (1.0 - costs.slippage_rate)
                    gross = position * sell_px
                    commission = max(costs.commission_min, gross * costs.commission_rate)
                    stamp = gross * costs.stamp_duty_rate
                    fee = commission + stamp
                    cash = max(0.0, gross - fee)
                    ret = (sell_px / entry) - 1.0 if entry else 0.0
                    trade_log.append(
                        {
                            "side": "sell",
                            "index": i,
                            "timestamp_utc": timestamps[i],
                            "signal_price": price,
                            "fill_price": sell_px,
                            "fee": fee,
                            "commission": commission,
                            "stamp_duty": stamp,
                            "trade_return_grossish": ret,
                            "fast_ma": f,
                            "slow_ma": s,
                        }
                    )
                    total_fees += fee
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
                "limit_up": limit_up,
                "limit_down": limit_down,
            }
        )

    last = closes[-1]
    final_equity = cash + position * last
    total_return = final_equity - 1.0
    buy_hold = (closes[-1] / closes[slow - 1]) - 1.0
    warnings.append(f"total_fees_on_unit_nav≈{total_fees:.6f}")

    return BacktestResult(
        True,
        f"ma_cross_{fast}_{slow}",
        symbol,
        len(bars),
        trades,
        total_return,
        buy_hold,
        max_dd,
        equity_curve[-200:],
        trade_log[-80:],
        warnings,
        None,
        costs.to_dict(),
        skipped,
    )
