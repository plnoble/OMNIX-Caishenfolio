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

## 2026-07-31 - R0 数据语义重构（理财软件重构起点）

- 目标转向：从单标的研究工作台重构为「个人多市场多资产理财软件」（账本 + 研究双主线）。
- 拆分 `Market` 枚举：新增 `MarketRegion`(cn/hk/us/jp/global) 与扩展后的 `AssetClass`
  (equity/etf/index/mutual_fund/bond/convertible_bond/fx/cash/commodity/reit)；删除混杂的 `Data/Market.cs`。
- 新增 `Money` + `Currencies`：decimal 金额、货币标签、跨币种运算直接抛错、按币种最小单位取整（JPY 0 位）。
- 新增 `ExchangeRegistry`（C#）/ `data/markets.py`（Python）：交易所→地区/货币/时区/Yahoo 后缀，
  含新增的 TSE（日股）、FUND（场外基金）、FX（汇率）、CNIB（银行间债券）伪交易所。
- `SymbolId` 增加 `Normalized()`（SH:600000 → SSE:600000）与 FX 对拆解。
- 新增 `classify_cn_code` / `cn_exchange_for_code`：按交易所区分同码不同品种（SSE 000001 指数 vs SZSE 000001 平安银行），
  可转债 110***/113***（SSE）与 159***（SZSE ETF）不再混淆；替换 provider 与 symbol_index 中两处重复启发式。
- 收敛既有 6 个 Python 测试失败：product/phase 在三处硬编码且已漂移（app.py 报 P5、health_payload 覆盖成 P4、
  http_server 仍写死旧产品名）。改为单一来源 `PRODUCT_PHASE`，测试断言常量。
- 版本/阶段：0.10.0 / R0（C# `ProductInfo` 与 Python `PRODUCT_PHASE` 对齐）。
- 验证：`dotnet build` 成功；C# 测试 42 → 83 全过；Python 测试 47（6 红）→ 60 全过。

## 2026-07-31 - R1 账本内核

- 新增 `Host/Portfolio` 领域：`Account`（多币种，余额按「账户+货币」分开）、`Instrument`（由交易所推导地区/货币）、
  `LedgerTransaction`（13 种流水，工厂方法内建校验）、`Position`/`CashBalance`/`ExternalFlow`。
- `PositionCalculator`：移动加权平均成本法；买入费税进成本；卖出结转已实现盈亏；送股只加份额不加成本；
  拆股按比例缩放；分红/票息计入收益而非冲减成本；超卖 fail-closed 并提示补期初持仓。
- `OpeningPosition` / `OpeningCash`：支持从既有持仓建账，不必回补多年历史。
- 换汇改为记录回单两边金额（`CounterAmount`）：1/7.2 在 decimal 下无精确表示，按汇率反推会在余额里留残差。
- `PortfolioStore`：SQLite（State root，`PRAGMA user_version` 版本化迁移，v1），decimal 以 TEXT 存储避免 REAL 精度损失；
  批量插入原子化；账户/标的/流水 CRUD 与过滤；`LoadState()` 回放。
- `LegacyFillImporter`：把 P4.3 的 JSON 成交台账（double 金额、无货币）迁入账本，货币按交易所推导，
  用确定性 id 保证重复导入幂等，坏行跳过并给出警告而不是中断整批。
- 验证：C# 测试 83 → 116 全过；Python 60 全过；`dotnet build` 成功。

## 2026-07-31 - R2 估值与收益

- `FxRate` / `FxConverter`：直接汇率 → 逆汇率 → 经枢轴货币（默认 USD）三角换算；缺汇率报错不猜。
  折算结果按目标货币最小单位取整——JPY→USD→CNY 的乘积在 decimal 下无精确表示，不取整会把
  第 22 位的噪声带进总额和导出。
- `ValuationEngine`：本位币估值，输出每笔持仓的本币/本位币市值与浮盈、成本、已实现、分红，
  以及按品种/地区/货币/账户的资产配置。**无价格或无汇率的持仓不按 0 计入**，而是标记未定价 + 警告，
  `IsComplete=false`——数据源故障应表现为「估值不完整」，不是「组合缩水」。
- 币种敞口维度下现金保留原币种，其余维度现金作为独立切片。
- `ReturnMetrics`：XIRR（牛顿法 + 二分兜底，与 Excel 文档示例对齐到 1e-6）、Modified Dietz、
  链式时间加权收益、年化。无解时返回 null，不编造数字；跨币种现金流直接拒绝。
- `PortfolioStore` 迁移至 v2：新增 `fx_rates` 快照表，`CreateFxConverter(asOf)` 不会用未来汇率估过去。
- 验证：C# 测试 116 → 151 全过。

## 2026-07-31 - R3 行情通道扩展

- Provider 从「只有 OHLCV」扩展出三条通道：`latest_quote`（估值用最新价）、`nav_series`（场外基金净值）、
  `fx_rate`（货币对）。`market/base.py` 用能力协议声明，composite 按能力路由并逐一汇报失败。
- 基金净值不再伪装成 K 线：新增 `NavPoint`（无 open/high/low/volume）。此前每天要编造三个字段；
  图表仍可用（`_bars_cn_fund` 由净值通道派生并保留 `fund_nav_not_ohlcv` 警告）。
- 日股：Yahoo ticker 映射改由交易所注册表提供（`TSE:7203 → 7203.T`、`FX:USDCNY → USDCNY=X`、
  `HKEX:00700 → 0700.HK`），yfinance 的货币也改为按交易所推导，不再写死 USD/HKD 二选一。
- 债券：A 股路由按 `classify_cn_code` 分流，可转债走 `bond_zh_hs_cov_daily`、其他交易所债券走 `bond_zh_hs_daily`。
- 汇率：yfinance 走 `USDCNY=X`；akshare 走 `fx_spot_quote`（缺接口时明确提示改用 yfinance）。
- 新增路由 `/market/quote`、`/market/nav`、`/market/fx`，C# `AnalyticsCoreClient` 增加对应类型化方法。
- `PortfolioPricingService`：按持仓收集报价与汇率，缺失转为警告（估值随之标记不完整而非按 0 计），
  直连汇率缺失时回退到枢轴腿以便三角换算，取到的汇率写入快照表供离线估值。
- 验证：Python 测试 60 → 82 全过；C# 测试 151 → 157 全过。

## 2026-07-31 - R5 CSV 导入与报表导出

- `DelimitedText`：RFC4180 解析/生成，处理引号内的分隔符与换行、双引号转义、UTF-8 BOM、逗号/制表符自动识别。
- `TransactionCsvImporter`：中英文列名与交易类型别名（买入/申购/定投/buy…）、多种日期格式、
  千分位与负号（方向由类型决定，不看正负号）、缺货币时按交易所推断、只有金额时反推单价。
- 先预览后提交：全部行校验完才写库；有错行时默认拒绝提交，必须显式选择跳过——避免半截导入。
- 行 id 由该行经济内容哈希得出，重复导入同一份对账单是幂等的（库内与文件内重复都会标出）。
- `RecordedAt` 取交易日而非导入时刻，保证回放顺序与导入时间无关。
- `PortfolioReportExporter`：持仓/配置/流水三张 CSV。未定价持仓导出空单元格而不是 0，
  避免表格 SUM 把「取不到价」算成「值 0」；流水导出可被导入器原样读回（已测往返一致）。
- 验证：C# 测试 157 → 175 全过。
