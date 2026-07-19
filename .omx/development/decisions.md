# Decisions

## 2026-07-19 - Product identity and landing

- Choice: product name **Caishenfolio**; greenfield root `D:\Agent\Project\Caishenfolio`.
- Consequence: all active implementation happens here; `D:\Agent\Project\金融工作台` is reference-only.
- Rejected: continue feature development inside the legacy FinWorkbench tree.

## 2026-07-19 - Tech stack for rewrite

- Choice: WPF + .NET 8 Desktop, C# Host Core (security owner), Python Analytics Core, loopback REST/WS later, SQLite/DuckDB later.
- Rationale: Windows privilege/process control stays in C#; finance/agent ecosystem stays in Python.
- Rejected for v1: Electron/Tauri-first, pure-Python desktop, Avalonia (no multi-OS requirement).

## 2026-07-19 - First cut P0

- Choice: Foundation + Security + Data Semantics before research UI or market adapters.
- Consequence: P0 delivers path roots, capability deny-by-default, loopback bind policy, redaction, symbol/OHLCV/provider contracts, minimal shell, tests.

## 2026-07-19 - P1 HTTP stack: stdlib first

- Choice: use Python stdlib `ThreadingHTTPServer` for Analytics Core in P1 instead of requiring FastAPI immediately.
- Rationale: zero install friction for health/search/bars smoke; FastAPI remains planned optional extra.
- Rejected: hard-require fastapi/uvicorn before first Host↔Python loop works.

## 2026-07-19 - P2 durable task ownership

- Choice: Host owns durable SQLite task mirror under State root; Python Core keeps in-memory task store for the process lifetime.
- Rationale: Host is audit authority and path-root owner; Core can restart without claiming State root writes until a later dual-writer design.
- Consequence: Desktop mirrors research results into SQLite after Core returns; Core task ids are stored as `core_task_id` metadata.
- Rejected for P2: dual SQLite writers (Host + Python) without coordination protocol.

## 2026-07-19 - P2 research command shape

- Choice: first research command is `POST /research/symbol-snapshot` (fixture bars summary JSON artifact).
- Rationale: exercises full Task → Artifact → Audit path with existing fixture provider; includes research disclaimer.
- Rejected for P2: LLM agent research or multi-step orchestration.

## 2026-07-19 - P3 real market via AkShare

- Choice: first real provider is **AkShare** (optional extra `market`), default when Desktop starts Core.
- Rationale: no API keys in repo; covers A-share/HK/US/fund public endpoints; aligns with fail-closed (errors surface, never invent OHLCV).
- Consequence: users must `pip install akshare pandas` and have network to upstream; offline tests force `fixture`.
- Rejected for P3: paid commercial vendor keys in repo; silent fallback from real→fixture (would hide data truth).
