# Worklog

## 2026-07-19 - Protocol init

- Initialized agent-development-protocol under `D:\Agent\Project\Caishenfolio`.

## 2026-07-19 - P0 scaffold and implementation

- Created solution `Caishenfolio.slnx` with Host, Desktop, Host.Tests.
- Implemented Host security: PathRootPolicy, ToolPermissionPolicy, LoopbackBindPolicy, SensitiveValueRedactor.
- Implemented Host data semantics: SymbolId, Market/AssetClass/Adjustment, OhlcvBar, ProviderResult.
- Implemented Python `caishenfolio_core` mirrors for security + data + health payload.
- Added docs: ARCHITECTURE, SECURITY_MODEL, DATA_SEMANTICS; product AGENTS.md + README.
- Verification: `dotnet build` ok; C# tests 25 passed; Python unittest 9 passed; compileall ok.

## 2026-07-19 - Protocol re-init + P1

- Re-read agent-development-protocol README/AGENTS; init skip-existing on legacy workspace and Caishenfolio.
- Implemented P1 Task/Artifact/Audit stores (Host + Python).
- Implemented fixture market data provider (search + historical bars, fail-closed).
- Added stdlib loopback HTTP server (`python -m caishenfolio_core.server`) with /health, /symbols/search, /market/bars, /tasks, audit.
- Desktop: Start / Check Health / Stop Core on 127.0.0.1:8765.
- Verification: `scripts/verify_p1.ps1` pass; C# 32 tests; Python 13 tests.

## 2026-07-19 - P2 market UI + SQLite mirror + research v0

- Extended AnalyticsCoreClient: search, bars, research snapshot typed DTOs.
- Python: `POST /research/symbol-snapshot` creates research Task + Artifact + Audit; fail-closed on unknown symbols.
- Host: `SqliteTaskStore` + `TaskMirrorService` under State root (`%LocalAppData%\Caishenfolio\state\tasks.db`).
- Desktop: symbol search list, bars DataGrid, Run Research Snapshot with Host mirror status.
- Product phase/version: P2 / 0.3.0.
- Verification: `scripts/verify_p2.ps1` pass; C# 39 tests; Python 17 tests.

## 2026-07-19 - P3 中文 UI + 真实行情 akshare

- Desktop 按钮/面板/状态提示全中文化。
- 新增 `AkshareMarketDataProvider` 与 `create_market_provider`；默认 `akshare`，测试可切 `fixture`。
- Health 暴露行情源就绪与是否合成数据。
- 真实行情失败 fail-closed（本环境代理阻断时验证：ok=false, data=None）。
- Product phase/version: P3 / 0.4.0；`scripts/verify_p3.ps1` pass。

## 2026-07-19 - P3.1 代理容错 + 诊断

- 网络策略：代理失败自动直连重试；`CAISHENFOLIO_HTTP_TRUST_ENV=0` 强制忽略系统代理。
- A 股多真实上游回退；`/market/diagnostics`；Desktop 启动自动健康检查与错误可读化。
- 版本 0.4.1；verify_p3 通过（关闭占用 Desktop 进程后）。
