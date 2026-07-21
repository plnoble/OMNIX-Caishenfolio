# Agent Handoff

## Current Objective

P4.3 planned buy/sell levels on candle chart + actual fill journal (v0.8.1).

## What Changed

- Host: `PricePlanStore` → `%LocalAppData%\Caishenfolio\state\price_plan.json`
  - Planned levels (buy/sell, multi), actual fills, FIFO snapshot
- Desktop chart: green dashed plan-buy / red plan-sell lines with distance labels; fill lines; last price guide
- UI: toolbar **计划/成交** → `PricePlanWindow`
- Clears freehand drawings does **not** remove plan/fill lines

## How to use

1. Load bars for a symbol
2. Click **计划/成交**
3. Add plan buy/sell prices → lines appear on K-line with % distance to last close
4. Register real fills (price/qty/fee) for PnL tracking

## Next (optional)

- Click chart to capture price into plan form
- Sync grid ledger next levels into plan lines
- Installer still deferred
