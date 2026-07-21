# Agent Handoff

## Current Objective

P4.2 grid strategy research (v0.8.0): suggest / backtest / manual ledger. No broker auto-orders.

## What Changed

- Python: `research/grid.py` (GridPlan, suggest_grid_from_bars, grid_backtest, next_grid_actions)
- Python: `research/grid_ledger.py` (SQLite plans/fills, FIFO PnL, next actions)
- API: `/research/grid-suggest`, `/research/grid-backtest`, `/research/grid/plans`, fills, snapshot, deactivate
- Desktop: **网格策略** window (建议与回测 + 成交台账)
- Host client methods for grid endpoints
- Tests: `tests/python/test_grid_research.py`
- Version **0.8.0** / phase **P4.2**

## How to use

1. Start Core, load a symbol + date range
2. Click **网格策略**
3. **AI 建议网格参数** → review rationale → **运行网格回测**
4. **保存为台账方案** → switch to 成交台账 → register buy/sell → refresh PnL / next levels

## User actions needed

None required beyond existing Core + market data setup.

## Next (optional)

- Geometric grid / short grid / multi-leg
- Draw grid levels on candle chart
- Real LLM explain for grid rationale (currently heuristic “AI-like”)
- Installer packaging still deferred
