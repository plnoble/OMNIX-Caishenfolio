# Agent Handoff

## Current Objective

P4.3.1 chart click pick for plan levels (v0.8.2).

## What Changed

- Chart modes: `PickPlanBuy` / `PickPlanSell` / `PickFillBuy` / `PickFillSell`
- Toolbar: **点选买点** / **点选卖点** — single click adds plan line + distance label
- PricePlan window: 图上点选买/卖、图上取买/卖成交价
- Hover guide line while picking; multi-pick until switch to 十字光标

## How to use

1. Load K-line
2. Click **点选买点** or **点选卖点**
3. Click on chart price area → green/red plan line appears with distance to last close
4. For fills: open **计划/成交** → 图上取买价/卖价 → enter qty → 登记成交

## Next (optional)

- Sync grid ledger next levels into plan lines
- Click marker to edit/delete
- Installer still deferred
