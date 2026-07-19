# Caishenfolio Agent Guide

## Product

- Name: **OMNIX-Caishenfolio**（品牌 OMNIX）
- Path: `D:\Agent\Project\Caishenfolio`
- Type: Windows local financial research & simulation workbench
- Scope: research / simulation / backtest / export / report
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

- Internal symbol form: `EXCHANGE:SYMBOL` (e.g. `SSE:600000`, `HKEX:00700`, `NASDAQ:AAPL`).
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

**P3 — 中文 Desktop + 真实行情（AkShare，fail-closed）**

Completed earlier:

- **P2 — Market UI + SQLite Task Mirror + Research Command v0**
- **P1 — Task/Artifact/Audit + Fixture Market Data + Core Health**
- **P0 — Foundation + Security + Data Semantics**
