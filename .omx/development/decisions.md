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

## 2026-07-31 - 重构定位：理财账本与研究双主线

- Choice: 产品重心改为「资产账本 + 研究工作台」双主线，第一批覆盖 A股/港股/美股/日股 与 股票/ETF/场外基金/债券/可转债/汇率。
- Choice: 保留 WPF + Python 双层；**账本领域下沉 C# Host**（SQLite + decimal + 汇率折算），Python 专注行情适配与研究分析。
- Rationale: 符合 AGENTS.md「Host 拥有状态与审计权威」；Python 侧无需持有长期状态即可扩展数据源。
- Rejected: 领域逻辑全放 Python（违反权威边界）；换栈为本地 Web（P3-P5 的桌面 UI 全部作废）。
- 记账来源: 手工录入 + 通用 CSV 导入（券商专用格式解析留到后续）。

## 2026-07-31 - R0 市场语义：地区与品种分离

- Choice: 用 `MarketRegion` + `AssetClass` 取代原 `Market` 枚举。
- Rationale: 原枚举把 `Etf` 当成市场，导致「美股 ETF」无法表达；账本按地区与品种两个维度做资产配置聚合，必须正交。
- Consequence: 旧持久化字符串（"ashare"/"etf"/"fund"）保留别名解析，不破坏已存的自选与计划数据。
- Choice: 金额一律 `Money`(decimal + 货币)，跨币种运算抛错而非静默相加。
- Rationale: 质量门禁要求钱不得用二进制浮点；多币种组合下静默相加是最危险的一类错误。

## 2026-07-31 - 阶段号单一来源

- Choice: `PRODUCT_PHASE` 定义在 `caishenfolio_core/__init__.py`，C# `ProductInfo.Phase` 与之对齐；测试断言常量而非字面量。
- Rationale: 阶段号此前在三处硬编码并已漂移（P5/P4/旧产品名），使 6 个测试长期红灯、门禁失效。
