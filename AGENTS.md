# Caishenfolio Agent Guide

## Product

- Name: **OMNIX-Caishenfolio**（品牌 OMNIX）
- Path: `D:\Agent\Project\OMNIX-Caishenfolio`
- Type: Windows local **personal multi-market multi-asset wealth workbench**
- Scope: 资产账本（账户/持仓/流水/估值/收益/配置）+ 研究（行情/计划/网格/回测/对比/报告）
- Markets: A股 / 港股 / 美股 / 日股；Assets: 股票 / ETF / 场外基金 / 债券 / 可转债 / 汇率 / 现金
- Out of scope: live broker order placement and exchange execution

## Working Agreement

1. Read this file, `.omx/development/current.md`, and `.omx/development/handoff.md` before changing code.
2. Existing code in `D:\Agent\Project\金融工作台` is **reference only**, not the implementation baseline.
3. Prefer small, verifiable steps. Update worklog, decisions, error-ledger, and handoff.
4. Do not overwrite user changes you did not make.
5. Never introduce live trading APIs or store long-lived exchange credentials in repo files.

## Architecture Rules

- **C# Host owns authority**: path roots, tool permissions, credential injection policy, process launch, audit ownership.
- **Python Core is controlled execution**: analytics, research, agents, market adapters; no direct desktop privilege.
- IPC is REST + WebSocket on **loopback by default**.
- All AI/long-running work must map to auditable Task / Artifact / Audit records (from P1 onward; contracts start in P0 docs).
- Unknown tools, paths, providers, agents, and external widgets are **deny-by-default**.

## Data Semantics Rules

- Internal symbol form: `EXCHANGE:SYMBOL` (e.g. `SSE:600000`, `HKEX:00700`, `NASDAQ:AAPL`, `TSE:7203`, `FUND:110022`, `FX:USDCNY`).
- Venue → region / currency / timezone comes from `ExchangeRegistry` (C#) and `data/markets.py` (Python). Do not re-derive ad hoc.
- **Market region and asset class are orthogonal.** Never put an instrument type into the region enum.
- Money is `Money` (decimal + currency). Never `double`, never cross-currency arithmetic without explicit FX conversion.
- OHLCV results must carry provider, adjustment policy, provenance, and quality warnings.
- Provider failure is **fail-closed**: return error/warning, never fabricate market data.
- Do not mix adjustment policies inside one analysis window without explicit conversion.

## Security Rules

- Path roots for import / artifact / run / state are separate and allowlisted on both C# and Python sides.
- Reject UNC paths, path traversal, and unresolved absolute paths outside roots.
- Redact credentials, tokens, secrets, and local filesystem paths from diagnostics and logs.
- Shell / file-write / generated-code capabilities are disabled unless explicitly enabled by policy.
- Research output must include the research/simulation disclaimer.

## Development Protocol

Follow `agent-dev-protocol` under `.claude/skills/agent-dev-protocol/SKILL.md`.

## Current Phase

**R0 — 数据语义重构（市场/品种分离、Money、交易所注册表）**

重构路线：R0 数据语义 → R1 账本内核 → R2 估值与收益 → R3 行情通道 → R4 桌面双主线 UI → R5 CSV 导入与报表。

Completed earlier:

- **P5 — 侧栏导航工作台（行情/计划/网格/回测/对比/系统）**

- **P4.3.x — 计划买/卖横线、图上点选、真实成交台账**
- **P4.2 — 网格策略（建议/回测/人工台账）**
- **P4 / P4.1 — 对比/MA回测/报告/Parquet/权益曲线**
- **P3.x — 行情 UI、缓存、画线、品牌 OMNIX**

Completed earlier:

- **P2 — Market UI + SQLite Task Mirror + Research Command v0**
- **P1 — Task/Artifact/Audit + Fixture Market Data + Core Health**
- **P0 — Foundation + Security + Data Semantics**
