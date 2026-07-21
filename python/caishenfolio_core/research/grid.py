from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from caishenfolio_core.research.backtest import CostModel, cost_model_from_dict


DISCLAIMER = "研究/模拟结论，非投资建议。网格建议基于历史波动统计，不保证未来有效。"


@dataclass
class GridPlan:
    lower: float
    upper: float
    grid_count: int  # number of intervals → levels = count+1
    order_cash: float  # cash per buy fill (unit-nav style or absolute; we use absolute cash per grid)
    mode: str = "arithmetic"  # arithmetic only in v0

    def levels(self) -> list[float]:
        if self.grid_count < 2:
            raise ValueError("grid_count 至少为 2。")
        if self.upper <= self.lower:
            raise ValueError("upper 必须大于 lower。")
        step = (self.upper - self.lower) / self.grid_count
        return [self.lower + i * step for i in range(self.grid_count + 1)]

    def to_dict(self) -> dict[str, object]:
        lv = self.levels()
        return {
            "lower": self.lower,
            "upper": self.upper,
            "grid_count": self.grid_count,
            "order_cash": self.order_cash,
            "mode": self.mode,
            "step": (self.upper - self.lower) / self.grid_count,
            "levels": lv,
            "level_count": len(lv),
        }


def suggest_grid_from_bars(
    bars: list[dict[str, Any]],
    *,
    symbol: str,
    lookback: int | None = None,
    grid_count: int | None = None,
    order_cash: float = 1000.0,
) -> dict[str, object]:
    """
    Heuristic "AI-like" grid setup from historical bars:
    - band: ~10th–90th percentile of closes (trim extremes)
    - spacing: related to ATR; grid_count from band/ATR
    """
    if not bars:
        return {"ok": False, "error": "无K线数据，无法建议网格。", "disclaimer": DISCLAIMER}

    use = bars[-lookback:] if lookback and lookback > 0 else bars
    closes = [float(b["close"]) for b in use]
    highs = [float(b.get("high", b["close"])) for b in use]
    lows = [float(b.get("low", b["close"])) for b in use]
    if len(closes) < 10:
        return {"ok": False, "error": "K线过少（建议≥10），无法稳健建议。", "disclaimer": DISCLAIMER}

    sorted_c = sorted(closes)
    n = len(sorted_c)

    def pct(p: float) -> float:
        idx = min(n - 1, max(0, int(round((n - 1) * p))))
        return sorted_c[idx]

    lower = pct(0.10)
    upper = pct(0.90)
    if upper <= lower:
        lower = min(closes)
        upper = max(closes)
    # slight pad
    pad = (upper - lower) * 0.02
    lower = max(0.01, lower - pad)
    upper = upper + pad

    # ATR-like
    trs: list[float] = []
    for i in range(1, len(use)):
        tr = max(
            highs[i] - lows[i],
            abs(highs[i] - closes[i - 1]),
            abs(lows[i] - closes[i - 1]),
        )
        trs.append(tr)
    atr = sum(trs) / len(trs) if trs else (upper - lower) / 10
    atr = max(atr, (upper - lower) / 50)

    if grid_count is None:
        # aim ~0.8–1.2 ATR per cell, clamp 4–30
        raw = int(round((upper - lower) / max(atr, 1e-9)))
        grid_count = max(4, min(30, raw))
    else:
        grid_count = max(2, min(50, int(grid_count)))

    plan = GridPlan(lower=lower, upper=upper, grid_count=grid_count, order_cash=float(order_cash))
    last = closes[-1]
    levels = plan.levels()
    # next buy = highest level strictly below last; next sell = lowest strictly above last
    below = [lv for lv in levels if lv < last]
    above = [lv for lv in levels if lv > last]
    rationale = [
        f"样本 {len(use)} 根K线，收盘价约 {min(closes):.4f}–{max(closes):.4f}。",
        f"建议区间取约 10%–90% 分位并略放宽：[{lower:.4f}, {upper:.4f}]，降低极端点干扰。",
        f"ATR≈{atr:.4f}，网格数={grid_count}，格距≈{(upper - lower) / grid_count:.4f}（约 {((upper - lower) / grid_count) / atr:.2f}×ATR）。",
        f"最新价≈{last:.4f}；若现价在区间内，宜在下方网格分批买、上方网格挂卖。",
        "震荡市更合适；单边趋势需缩小仓位或暂停网格。",
        DISCLAIMER,
    ]
    return {
        "ok": True,
        "symbol": symbol,
        "plan": plan.to_dict(),
        "stats": {
            "bar_count": len(use),
            "last_close": last,
            "min_close": min(closes),
            "max_close": max(closes),
            "atr": atr,
            "pct10": pct(0.10),
            "pct90": pct(0.90),
        },
        "next_buy_level": max(below) if below else None,
        "next_sell_level": min(above) if above else None,
        "rationale": rationale,
        "disclaimer": DISCLAIMER,
    }


def grid_backtest(
    bars: list[dict[str, Any]],
    *,
    symbol: str,
    lower: float,
    upper: float,
    grid_count: int,
    order_cash: float = 1000.0,
    costs: CostModel | dict[str, Any] | None = None,
    initial_cash: float | None = None,
) -> dict[str, object]:
    """
    Long-only arithmetic grid on daily closes (research simulation).
    Buy when close crosses down through a grid level; sell when crosses up through a level
    if inventory exists at the lower adjacent slot.
    """
    if isinstance(costs, dict) or costs is None:
        cost = cost_model_from_dict(costs if isinstance(costs, dict) else None)
    else:
        cost = costs

    warnings = [
        "simulation_only",
        "not_for_investment_decisions",
        "grid_long_only_close_cross",
        "cost_model_simplified",
    ]
    try:
        plan = GridPlan(lower=lower, upper=upper, grid_count=grid_count, order_cash=order_cash)
        levels = plan.levels()
    except ValueError as exc:
        return {"ok": False, "error": str(exc), "disclaimer": DISCLAIMER}

    if len(bars) < 3:
        return {"ok": False, "error": "K线不足。", "disclaimer": DISCLAIMER}

    # inventory at each grid index 0..n-2 meaning bought at level[i] waiting to sell at level[i+1]
    inv = [0.0] * grid_count  # shares
    cash = float(initial_cash) if initial_cash is not None else order_cash * grid_count
    start_cash = cash
    trades: list[dict[str, Any]] = []
    equity_curve: list[dict[str, Any]] = []
    skipped = 0
    peak = start_cash
    max_dd = 0.0

    closes = [float(b["close"]) for b in bars]
    ts = [str(b.get("timestamp_utc") or "") for b in bars]

    def mark_equity(i: int) -> float:
        price = closes[i]
        return cash + sum(inv) * price

    for i in range(1, len(closes)):
        prev, price = closes[i - 1], closes[i]
        # limit flags vs prev close
        limit_up = price >= prev * (1.0 + cost.limit_up_pct) - 1e-9
        limit_down = price <= prev * (1.0 - cost.limit_down_pct) + 1e-9

        # check each band between levels[j] and levels[j+1]
        for j in range(grid_count):
            lo, hi = levels[j], levels[j + 1]
            # buy: crossed down through lo
            if prev >= lo > price:
                if cost.enforce_limit and limit_up:
                    skipped += 1
                    continue
                if cash < order_cash * 0.5:
                    continue
                spend = min(order_cash, cash)
                buy_px = price * (1.0 + cost.slippage_rate)
                fee = max(cost.commission_min, spend * cost.commission_rate)
                if spend <= fee:
                    continue
                qty = (spend - fee) / buy_px
                cash -= spend
                inv[j] += qty
                trades.append(
                    {
                        "side": "buy",
                        "level": lo,
                        "grid_index": j,
                        "timestamp_utc": ts[i],
                        "signal_price": price,
                        "fill_price": buy_px,
                        "qty": qty,
                        "fee": fee,
                    }
                )
            # sell: crossed up through hi, if we hold from this grid
            if prev <= hi < price and inv[j] > 0:
                if cost.enforce_limit and limit_down:
                    skipped += 1
                    continue
                qty = inv[j]
                sell_px = price * (1.0 - cost.slippage_rate)
                gross = qty * sell_px
                commission = max(cost.commission_min, gross * cost.commission_rate)
                stamp = gross * cost.stamp_duty_rate
                fee = commission + stamp
                cash += max(0.0, gross - fee)
                inv[j] = 0.0
                trades.append(
                    {
                        "side": "sell",
                        "level": hi,
                        "grid_index": j,
                        "timestamp_utc": ts[i],
                        "signal_price": price,
                        "fill_price": sell_px,
                        "qty": qty,
                        "fee": fee,
                        "buy_level": lo,
                    }
                )

        eq = mark_equity(i)
        peak = max(peak, eq)
        dd = (eq / peak) - 1.0 if peak else 0.0
        max_dd = min(max_dd, dd)
        equity_curve.append(
            {
                "timestamp_utc": ts[i],
                "equity": eq,
                "close": price,
                "cash": cash,
                "shares": sum(inv),
            }
        )

    final_eq = mark_equity(len(closes) - 1)
    total_return = (final_eq / start_cash) - 1.0 if start_cash else None
    buy_hold = (closes[-1] / closes[0]) - 1.0 if closes[0] else None

    return {
        "ok": True,
        "strategy": "grid_long_arithmetic",
        "symbol": symbol,
        "plan": plan.to_dict(),
        "bar_count": len(bars),
        "trades": len(trades),
        "buy_count": sum(1 for t in trades if t["side"] == "buy"),
        "sell_count": sum(1 for t in trades if t["side"] == "sell"),
        "skipped_signals": skipped,
        "initial_cash": start_cash,
        "final_equity": final_eq,
        "total_return": total_return,
        "buy_hold_return": buy_hold,
        "max_drawdown": max_dd,
        "open_shares": sum(inv),
        "open_inventory": [
            {"grid_index": j, "buy_level": levels[j], "sell_level": levels[j + 1], "qty": inv[j]}
            for j in range(grid_count)
            if inv[j] > 0
        ],
        "trade_log": trades[-100:],
        "equity_curve": equity_curve[-200:],
        "cost_model": cost.to_dict(),
        "warnings": warnings,
        "disclaimer": DISCLAIMER,
    }


def next_grid_actions(
    *,
    plan: GridPlan,
    last_price: float,
    open_lots: list[dict[str, Any]],
) -> dict[str, object]:
    """Given plan + open lots (each with buy_level/qty), suggest next buy/sell prices."""
    levels = plan.levels()
    below = [lv for lv in levels if lv < last_price]
    above = [lv for lv in levels if lv > last_price]
    next_buy = max(below) if below else None
    # sells: for each open lot, target is next higher level above its buy_level
    sell_targets = []
    for lot in open_lots:
        buy_lv = float(lot.get("buy_level") or lot.get("price") or 0)
        qty = float(lot.get("qty") or 0)
        if qty <= 0:
            continue
        higher = [lv for lv in levels if lv > buy_lv + 1e-12]
        target = min(higher) if higher else plan.upper
        sell_targets.append(
            {
                "buy_level": buy_lv,
                "qty": qty,
                "suggest_sell": target,
                "unrealized_ref": (last_price / buy_lv - 1.0) if buy_lv else None,
            }
        )
    return {
        "last_price": last_price,
        "next_buy_level": next_buy,
        "next_sell_candidates": sell_targets,
        "in_band": plan.lower <= last_price <= plan.upper,
        "note": "建议价位为网格理论档位，实盘需考虑流动性与规则；非投资建议。",
        "disclaimer": DISCLAIMER,
    }
