# Caishenfolio Architecture (P0)

## Goal

Local Windows workbench for financial research and simulation. No live trading.

## Layers

1. **Desktop (WPF)** — window, first-run/status surfaces, future panels and WebView2 hosts.
2. **Host Core (C#)** — security owner: path roots, capability policy, redaction, loopback bind policy, process broker (later), desktop-side models.
3. **Analytics Core (Python)** — market data contracts, research/quant/agent execution, FastAPI surface.
4. **Storage** — SQLite for state/tasks/audit (later); DuckDB/Parquet for timeseries (later).
5. **IPC** — Host calls Python via REST + WebSocket; Python never holds long-lived desktop credentials.

## Authority Boundary

- Host decides what may run, which roots are allowed, and which capabilities are enabled.
- Python re-validates path roots and parameter schemas before side effects.
- Default network bind for managed Python Core: loopback only (`127.0.0.1` / `::1`).

## P0 Deliverables

- Product identity and repo skeleton
- Shared security primitives (C# + Python)
- Shared data semantics primitives (symbol, bar, adjustment, provider result)
- Minimal desktop shell showing product status
- Automated tests for security and data semantics

## Non-goals for P0

- Full research workflows, watchlist UI, packaging MSI, live market adapters, orchestrator runtime

## P2 Deliverables

- Desktop symbol search + bars preview panel (fixture provider via Core HTTP)
- Host durable task mirror (`SqliteTaskStore` under State root)
- Research command v0: `POST /research/symbol-snapshot` → Task + Artifact + Audit
- Host `TaskMirrorService` copies Core research outcomes into SQLite
- Product phase `P2` / version `0.3.0`

## Authority note (P2)

- Host owns durable audit/task SQLite under State root.
- Python Analytics Core still uses in-memory task store for the process; Core task id is mirrored as metadata `core_task_id`.

## P3 Deliverables

- Desktop UI and status strings in Chinese
- Real market provider `akshare` (optional dep); env `CAISHENFOLIO_MARKET_PROVIDER=akshare|fixture`
- Fail-closed on provider/network/parse errors; provenance marks `synthetic=false` for real bars
- Health exposes `market_provider`, `market_provider_ready`, `market_data_synthetic`
- Product phase `P3` / version `0.4.0`

