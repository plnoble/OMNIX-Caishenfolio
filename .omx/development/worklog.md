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

## 2026-07-31 - R4 桌面双主线导航

- 左栏分组：「我的资产」（总览 / 持仓 / 账本）与「研究」（行情 / 计划 / 网格 / 回测 / 对比）+ 系统；启动默认落总览。
- `PortfolioWorkspace`（Host，UI 无关门面）：刷新估值、记账、导入、导出、迁入旧成交台账——
  放在 Host 而非桌面层，使整条理财工作流不开窗口即可单测。
- 新增 `Desktop/Wealth`：`PortfolioViewModel`（INotifyPropertyChanged + ObservableCollection）与三个视图。
  总览含 5 个 KPI 与四维配置条；持仓表按盈亏着色、未定价行灰显并标「缺价格」；
  账本含账户管理、按类型联动的记账表单、CSV 预览导入、流水删除与导出。
- 核心未就绪时账本照常打开（现金仍精确，持仓标记缺价格）；核心起来后自动挂上取价源并重估。
- 事故：`Setter Property="Resources"` 编译期无错但启动即崩（详见 error-ledger）；改为应用级隐式样式。
- 验证：`dotnet build` 0 错 0 警；C# 183 全过；Python 82 全过；**启动冒烟通过**（窗口标题 v0.10.0）。
- 未做：旧研究页 1225 行 code-behind 未 MVVM 化（改动风险高于收益，已列入 handoff 的 Next）。

## 2026-07-31 - V1~V4 版本治理与产品化

- **版本号收敛为单一来源**：根目录 `VERSION` / `PHASE` 单行纯文本，MSBuild / C# / Python / PowerShell 共用。
  修复前实际存在四个源且已全部不一致：`Directory.Build.props` 0.2.0、`ProductInfo` 0.10.0、
  Python `__version__` 0.10.0、`pyproject.toml` 0.4.0；Python `PRODUCT_PHASE` 也还停在 R0。
- `ProductInfo.Version/.Phase` 改为读程序集属性（`InformationalVersion` + `AssemblyMetadata`），代码里不再出现版本字面量。
- `ProductVersionTests` 断言四处一致 + 版本必须是三段 semver，漂移即红灯。
- `scripts/version.ps1`：查看 / `-Bump patch|minor|major` / `-SetVersion`；同步 Python 常量与 pyproject。
  两个编码坑：PS 5.1 读 .ps1 需 UTF-8 BOM 否则中文乱码且解析错乱；而 `Set-Content -Encoding utf8` 会**写入** BOM，
  会污染 VERSION 与 .py，故写文件改用 `UTF8Encoding($false)`。
- **MSI 打包**：移植归档工程的 WiX 方案为 `packaging/windows/Omnix.{Installer.wixproj,Product.wxs}`，
  新 GUID、OMNIX 命名、`ProductVersion=$(OmnixVersion)`。实测产出 1.13MB MSI，包内 ProductVersion=0.10.0。
  `MajorUpgrade` 支持原地升级；用户数据在 LocalAppData，升级/卸载都不动。
- **更新检查**：`UpdateChecker` + `IReleaseFeed`（GitHub Releases 只读）。数值比较而非字符串比较
  （`0.9.0 < 0.10.0`）；网络失败/无 Release/无法解析的 tag 全部 fail-closed 且不阻塞使用；
  不自动下载安装，只打开发布页。系统页新增「检查更新」。
- **CI**：`.github/workflows/ci.yml`（push/PR：构建 + 双侧测试 + 产出 MSI 制品，Python 强制 fixture 源）；
  `release.yml`（打 tag：先校验 tag 与 VERSION 一致，再测试、构建 MSI、算 SHA256、建**草稿** Release）。
- 新增 `docs/RELEASE.md`。
- 验证：C# 183 → 206 全过；Python 82 全过；MSI 构建成功并核对包内版本号。

## 2026-07-31 - A1~A4 归档资产移植

- **A1 Python 运行时自动供给**：`PythonRuntimeProvisioner`（移植自归档 FinWorkbench）。
  用 uv 在 State root 下建独立 venv，按 `pyproject.toml` 哈希打标记判断依赖是否过期——
  依赖只在清单变化时重装，不是每次启动都装。**安装失败不写标记**，半成品环境下次会被识别为过期而不是就绪。
  无 uv 时降级到系统 Python，两者都没有时明确报错而不是抛异常。修掉了原先「往 PATH 上随便哪个 python 装包」的问题。
- **A2 UI 启动冒烟**：`scripts/ui_smoke.ps1`。启动应用、等窗口标题、检查 stderr 有无未处理异常、
  校验标题里的版本与 VERSION 一致、再等 5 秒确认没有延迟崩溃，写 JSON 报告后关闭。
  把上次踩的 XAML 坑变成可执行门禁（BAML 运行时才加载，构建期看不出）。
- **A3 组合风险指标**：`PortfolioRiskAnalyzer`。按 Money/decimal 重写，不抄旧代码（旧版用 float 存钱）。
  单一持仓/品种/市场/货币/现金五类集中度上限（阈值可配）、历史最大回撤（含峰谷日期）、
  相对用户自设目标配置的偏离金额。**只陈述「你设的阈值被突破了多少」，不含任何买卖建议**，有测试守住这条线。
  新增 schema v3 `valuation_snapshots`：每次刷新记一个点（按日期 upsert），没有这条曲线就算不出回撤。
- **A4 价格提醒**：`PortfolioAlertEvaluator`。复用研究侧已有的计划买/卖价位——图上画的线，
  在持有该标的时会在理财侧变成提醒。另含价格过旧、未定价持仓、集中度提醒。警告排在提示前面。
- 总览页新增「历史最大回撤」KPI 与右侧提醒栏。
- 验证：C# 213 → 236 全过；`dotnet build` 成功；**UI 冒烟通过**（覆盖新增的提醒面板 XAML）。

## 2026-07-31 - S1/S2 偏好设置

- `PortfolioSettings` + schema v4 `settings` 键值表：本位币、五类集中度阈值、按品种的目标配置。
  用键值而非列——偏好会长（新阈值、新品种），为每个偏好做一次 schema 迁移不值。
- 保存前校验：阈值必须在 (0,1]；目标配置要么全空要么合计正好 100%（容差 0.0001）；
  未知货币/品种直接拒绝；0 权重的品种丢弃而不是卡住合计；接受旧品种名（`fund` → `mutual_fund`）。
  保存目标时先整体删除 `target.` 前缀行再写入——从目标里删掉一个品种是真的删掉，不会留下陈旧权重。
- `PortfolioWorkspace` 构造时读取偏好；`ApplySettings` 校验→持久化→采用，校验失败不落库。
  本位币变更会开一条新的估值曲线（快照按「日期+货币」为主键，天然分开）。
- 新增 `PortfolioSettingsWindow`：本位币、五个阈值、八类品种的目标占比（带实时合计校验与红/绿提示），
  并把已实现但一直没有入口的 `ImportLegacyFills` 接了出来（选账户导入旧成交台账，可重复点击）。
- 修掉一个真实噪音：现金桶同时出现在「品种」和「市场」两个维度，同一笔闲置现金会报两次告警。
  现改为只在「品种」维度评估现金。
- **冒烟脚本升级**：加了 UI Automation 一步——点开「设置」按钮并确认「理财偏好设置」窗口真的加载。
  对话框的 BAML 只在显示时解析，主界面跑通不代表它能开；这一步实测通过。
- 验证：C# 236 → 249 全过；`dotnet build` 0 错；UI 冒烟通过（含设置窗口加载）。

## 2026-07-31 - 启动时静默更新检查

- 原来只有系统页手动按钮；现在启动后在后台静默查一次，排在账本刷新与核心启动**之后**，
  GitHub 慢或不通都不会拖慢启动，失败完全无声。
- 只有 `UpdateAvailable` 且用户没忽略过该版本，才在主窗口顶部出提示条（去下载/忽略此版本/关闭）。
  已最新、本地更新、检查失败一律不打扰，不弹模态框。
- `UpdatePreferenceStore`（State root 下 `update.json`）记「已忽略的版本」与上次检查时间。
  忽略是**按版本**的，更高版本仍会提示；手动点「检查更新」无视忽略照常显示。
  文件损坏时回退默认值而不是让应用起不来。
- 主窗口网格新增一行放提示条，页面宿主与底部状态栏行号相应下移。
- 澄清了一条容易踩的链路（已写入 docs/RELEASE.md）：GitHub `/releases/latest` **按设计排除草稿**，
  而我们的发布工作流刻意建草稿，所以必须人工点发布客户端才看得到；推 main 则完全不产生可见更新。
- 验证：C# 249 → 255 全过；构建 0 警告；UI 冒烟通过（提示条 XAML 随主窗口加载已被覆盖）。

## 2026-07-31 - v0.11.0 安装包体验修复 + 首次发版

- 用户反馈实测：装完没有安装位置可选、桌面没有快捷方式。两条都属实，是包的缺陷：
  - **根本没引 WiX UI 扩展**，`msiexec` 只跑默认基础界面（仅进度条），没有目录选择页。
    现引入 `WixToolset.UI.wixext` + `WixUI_InstallDir`，`WIXUI_INSTALLDIR=INSTALLFOLDER`。
  - 只建了开始菜单快捷方式；补 `DesktopShortcutComponent`（`DesktopFolder`）。
  - `ARPINSTALLLOCATION` 未设置，控制面板「安装位置」为空；已补 `SetProperty ... After=CostFinalize`。
- 跳过许可协议页（Welcome→InstallDir 用 Order=2 覆盖内置 Order=1）：本项目没有许可文本，
  空白协议页只是多一次点击。
- 引入 UI 扩展后暴露编码问题：MSI 数据库代码页被钉在 1252，装不下中文产品名与快捷方式描述。
  改 `Codepage="936"` `Language="2052"`，并把向导本身本地化为 zh-CN。
- **版本必须升到 0.11.0**：已安装的是 0.10.0，`MajorUpgrade` 默认不允许同版本覆盖，
  修好的包若仍是 0.10.0 用户得先手动卸载。
- 验证：MSI 表级核对（`InstallDirDlg` 存在、`WIXUI_INSTALLDIR`、两个快捷方式、ProductLanguage 2052、
  WelcomeDlg Next→InstallDirDlg Order 2）；C# 255 + Python 82 全过；构建 0 警告；UI 冒烟通过。
- 未做：向导的可视化点击走查没有自动化成功（msiexec UI 在独立进程，按标题跨进程查找会误抓到
  标题含同名字符串的其他窗口）。表级证据充分，实际观感由安装时确认。

## 2026-07-31 - 交叉验证取价（借鉴 ai-berkshire）

- 读了 xbtlin/ai-berkshire（MIT，14.8k star）。它是 Claude Code 的提示词/技能集合，不是应用：
  无账本、无数据接入层、无持久化。与本项目几乎正交，唯一值得借的是 `financial_rigor.py` 的
  **cross_validate**（同一数据点多源比对 + 容差报警）。
  decimal 精度我们 R0 已有；本福特查财报、三情景估值不适用；「强制结论」与我们不给建议的底线相悖，不采纳。
  README 自报的收益率（+69.29% / +66.38% / 1.46 亿）无法验证，不作为依据。
- 修补的真实缺陷：`CompositeMarketDataProvider._first_success` **只用第一个应答的源，从不交叉核对**。
  对研究工作台无妨，对理财软件意味着一个错价会静默算错净资产。
- `latest_quote(symbol, cross_check, tolerance_pct)`：询问所有可用源 → 按多数币种筛出可比报价 →
  取**中位数**（对单个离群源稳健）→ 记录每个源的报价与价差。
  价差 = (max-min)/median；超容差加 `price_disagreement:{pct}` 警告。
  币种不一致的源不混入中位数，单独报 `currency_disagreement`。单源可用时照常返回并标 `single_source`。
  全部失败仍 fail-closed。默认路径（cross_check=false）行为完全不变。
- 贯通到桌面：`Quote.to_dict` 提升出 `source_count/spread_pct/sources` 三个字段 →
  `MarketQuoteDto` → `PriceQuote` → 新增 `AlertKind.PriceDisagreement`，在提醒栏显示各源报价。
- 设置窗口新增「交叉核对价格」开关（默认开）与「价格容差」（默认 2%），存进 settings 表。
- 验证：Python 82 → 93 全过；C# 255 → 264 全过；构建 0 警告；UI 冒烟与安装布局冒烟均通过。

## 2026-07-31 - 零依赖 A 股行情源 + 交叉核对指名离群源

- 上次对 ai-berkshire 的评估不扎实：只读了 README 摘要就下结论，说 `tools/` 只有一个工具（实际 8 个），
  而且**借 cross_validate 时没读原文**。这次拉了完整文件树重评。
- 读原文后补上一处：它算**每个源相对中位数的偏离**并标出超容差者，我原来只算整体价差。
  现在每个源带自己的偏离（`akshare=10(-9.1%)`），`outliers` 单列，提醒里直接点名该不信哪个源。
- 新增 `TencentQuoteProvider`（借鉴 `tools/ashare_data.py` 的取数方式）：纯标准库打 `qt.gtimg.cn`，
  GBK 解码，只做最新价。价值有二：A 股原本基本只有 akshare 一个报价源，交叉核对形同虚设；
  且首次安装后 venv 未就绪时取不到任何价格，这个源装好即可用。
  加入 auto 链且排在需要 key 的源之前。
- 只借取数方式，**不借它的代码→交易所映射**：它把 `1` 开头全判给深交所，正是我们修过的坑
  （`110xxx`/`113xxx` 是上交所可转债）。
- 测试抓到一个危险 bug：交易所未命中前缀表时会回落到 CN 分类器，
  `HKEX:00700` 会变成 `sz000700`——拿深交所某股的价格当腾讯控股。已改为仅在交易所为空时才用分类器。
- 停牌/退市报 0 价按无有效报价 fail-closed，不当成 0 元。
- 验证：Python 95 → 110 全过；C# 265 全过。

## 2026-07-31 - F1~F5 金融分析能力

- **纠正了一条我划错的线**：此前把「选股筛选、财报、PE/PB、估值分位」一并当作投资建议拒绝，这是错的。
  它们是事实计算。真正不能做的只有一条：以推荐口吻断言「现在该买/该卖」。
  以下全部按「陈述事实与历史位置，不给指令」实现，并有测试守住措辞。
- **F1 指标库**：SMA/EMA/MACD/Wilder RSI/布林/Wilder ATR/百分位。两条硬规则：
  历史不足返回 None 而非 0（0 会变成可交易信号与图上一条线）；序列长度与输入一致，调用方无法错位对齐。
- **F2 诚实回测**：现有回测已含手续费/印花税/滑点/涨跌停/回撤。补上胜率、盈亏比、最长连亏、
  回撤持续期、波动率、单位回撤收益、扣成本后相对买入持有的超额；以及**按时间顺序**（绝不打乱，
  打乱等于让规则看见未来）切分的样本外检验。证据不足时明说：样本外亏损、衰减过半、跑输什么都不做、
  交易笔数太少。数据太短返回 None 而不是拿 20 根K线编个数字；通过的规则仍附「单次回测不构成证据」。
- **F3 估值分位**：`valuation_history` / `financial_summary` 通道 + `/market/valuation`、`/market/financials`。
  PE/PB/股息率的当前值、分位、区间与中位数。样本不足 60 个标为不可靠而非照算；PE 为负明确指出无可比意义；
  载荷直说「分位不代表便宜或应当买入」——低估值可能是基本面在恶化，这个计算看不出来。
- **F4 汇率利差看板**：各货币对的政策利率差、当前汇率在自身历史中的分位、各币种敞口与权重。
  明确声明利差只描述长期倾向、不预测方向、不构成换汇时点判断（2022-2024 日元就是反例）。
  缺利率数据返回 None 而非 0；无法折算的余额没有权重而不是权重为 0（后者读起来像「这笔钱不值钱」）。
- **F5 打新记录**：schema v5 `ipo_subscriptions`。申购→摇号→缴款→上市→卖出全流程，
  中签后缴款会**镜像成账本买入流水**（确定性 id，重复保存是更新不是重复建仓），删除记录会一并删掉持仓。
  统计口径包含**没中签的那些**——只记得赢的那几次会把中签率算成 100%。未摇号的不计入分母。
- 验证：Python 156 → 185 全过；C# 265 → 280 全过；构建 0 警告；UI 冒烟与安装布局冒烟均通过。

## 2026-07-31 - G1 解读层 + G3 界面接入（第一批）

- **纠正**：用户要的不是「买/卖」两个字，是看懂。解读层提供比「建议买入」更有用的东西。
- `research/interpretation.py`：
  - 分档：历史低位/偏低/中性/偏高/历史高位（只命名位置，不含指令，有测试守着措辞）
  - 每个指标的「这是什么 / 怎么看 / 注意」，其中「注意」是新手最需要而通常被省略的部分
    （PE 条目直说「盈利下滑时 PE 被动变高，暴雷前 PE 常常很低，低 PE 不等于安全」）
  - **条件收益分布**：历史上处于同一分位带时，N 天后的胜率/中位数/最好/**最差**/样本量。
    这是刻意用来替代「建议买入」标签的——中位数不带最差就是推销。样本不足直接说不具统计意义。
  - 分位用**扩张窗口**计算：只用当时已有的数据，避免前视偏差。用全样本排名会让回测看起来
    远好于实盘可达到的水平。
- `/market/valuation` 现在同时返回 readings（分档 + 解释 + 条件分布），价格按日期对齐取自 K 线通道。
- **界面新增三页**（左栏新分组「看清楚」）：
  - **估值**：分位条 + 分档色带 + 白话解释 + 「从类似位置出发历史上后来发生了什么」
  - **打新**：申购/中签/缴款/卖出全流程录入，KPI 含中签率（已开奖口径）与真实累计收益
  - **汇率**：利差、每 100 万一年利差的**具体金额**、汇率历史分位与一年区间；
    顶部解释换汇盈亏由利差与汇率变动两部分组成，并说明四天波动就能吃掉一年利差
- 冒烟脚本新增导航走查：依次点开六个页面，确认切换时的 reload 路径不抛异常（仅加载窗口覆盖不到）。
  实测通过。
- 验证：Python 207 全过；C# 280 全过；构建 0 警告；UI 冒烟（含导航走查与设置窗口）通过。

## 2026-07-31 - G2/G4 打新提醒、回测判定面板、涨跌停按板块修正

- **回测结果窗新增「样本外检验」面板**：绿/红徽章给出「样本外仍成立 / 未通过」，
  下面列胜率、盈亏比、最长连亏、年化、波动率、回撤持续期、扣成本后相对买入持有的超额，
  以及逐条 findings 与切分日期。这是回测里唯一能说「这规则不行」的部分。
- **打新提醒**：中签未缴款＝警告级（逾期作废且影响后续申购资格）；上市前 7 天内、当天、
  已上市未登记卖出各有对应提醒。已完结与未开奖的记录不打扰。
- **修了一个真 bug（借鉴 daily_stock_analysis 的板块判定）**：回测把涨跌停硬编码成 ±10%。
  实际是分板块的——创业板/科创板 ±20%、北交所 ±30%、ST ±5%（但创业板/科创板的 ST 仍是 ±20%）；
  而**港股/美股/日股根本没有个股涨跌停**。原实现两头错：对创业板凭空造出从未发生的涨停日，
  对美股则跳过了完全可交易的信号。现按标的判定，非 CN 市场直接关闭该约束。
  可转债走临时停牌机制而非固定涨跌幅，也归为无固定上限。
- 只借板块判定思路，未抄代码（该项目 MIT，但这几行规则是公开事实）。
- 验证：Python 207 → 220 全过；C# 280 → 289 全过；构建 0 警告；UI 冒烟（六页导航 + 设置窗口）通过。
