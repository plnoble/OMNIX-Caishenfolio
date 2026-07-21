# Agent Handoff

## Current Objective

P5 workspace shell (v0.9.0): Scheme A full-phase left navigation UI.

## What Changed

- MainWindow: left rail (行情/计划/网格/回测/对比/系统)
- Top bar: symbol / interval / dates / load / core status only
- Market page: large chart (`*`), watch collapsible, bars table collapsed by default
- Plan page: embedded `PricePlanView`
- Grid page: embedded `GridView`
- Backtest page: inline MA params (no separate settings window required)
- Compare / System pages for secondary workflows
- Version **0.9.0** / phase **P5**

## How to navigate

1. **行情** — watchlist + big K-line + pick buy/sell
2. **计划** — planned levels & real fills
3. **网格** — suggest / backtest / ledger
4. **回测** — MA cross
5. **对比** — multi-symbol overlay window
6. **系统** — core, credentials, export, cache

## Next (optional)

- Embed compare chart fully in-page (no popup)
- Icon-only nav with tooltips polish
- Remember last page / watch collapse state
- Installer still deferred
