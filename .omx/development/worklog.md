# Worklog

## 2026-10-07 - Delivery remediation execution

- Rechecked R5 isolation, per-card FX assertions, and failure text.
- Confined test state roots, pinned fixture only with isolation, and rejected an occupied 8765.
- Two fresh UI smokes passed after the fixes. C# 599, Python 449 with one skip, gated process tests 5/5.
- DPI, pointer, Tab, screen reader, and novice tasks remain open. No commit or release.

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

## 2026-09-04 - N0 账本完整性与数据可信护栏（小白安全化计划第一阶段）

来源：`.omx/development/novice-finance-product-plan.md` §5（N0.1~N0.4）。

- **N0.1 冲正代替物理删除**：
  - `LedgerTransaction.Reversal(original, reason)` 工厂：镜像原流水经济内容、新 id、
    `ReversesTransactionId` 指回原流水；冲正记录不可再冲正。
  - `PortfolioStore` schema v5 → v6：`transactions` 增加 `reverses_transaction_id` 列与索引；
    迁移前先把库文件复制为 `.bak`（回滚锚点）；`ValidateReversals` 在写入时校验
    目标存在、未被冲正过、目标本身不是冲正——UI、导入器、未来工具都无法绕过。
  - `PositionCalculator.Replay` 跳过「原 + 冲正」整对：现金、持仓、成本、已实现收益、
    外部流全部回到入账前，无重算漂移。超卖校验不变（先冲正，再正常重放）。
  - `RemoveTransaction` API 删除；账本页「删除选中」改为「冲正选中」+ 确认对话框；
    流水表新增「状态」列（已入账 / 冲正 / 已冲正）。
  - CSV 导入预览：已冲正的行重新导入时报错并解释恢复方式（录一条同内容新流水），
    防止把已取消的事实「复活」。
- **N0.2 估值不完整不输出“正常”**：
  - `RiskCoverage`（Complete / Partial / InsufficientData）+ `PricedFractionByCost`（按成本口径，
    因为缺的正是市值——已定价市值/合计会恒等于 100%，是误导）。
  - 全部未定价：只返回一条 Warning「未知」，摘要明确「不输出任何正常结论」。
  - 部分定价：摘要以「估值不完整：仅基于已定价部分」开头；目标配置偏离（金额口径）被抑制，
    因为除以不完整总额会伪造精度；总览页提醒区新增红字覆盖率条。
  - 汇率缺失导致成本不可折算的持仓按成本 0 计入覆盖比例（宁可低估已知比例）。
- **N0.3 后台提醒能力如实标注**：
  - 新 `NotifyLogReader`：读 `notify.log` 最后一行 → `BackgroundCheckStatus`
    （最近运行时间 + 结果摘要；超长行截断防注入）。
  - 设置页新增「后台检查范围」声明卡（与 HeadlessNotifier 实际行为逐字一致）、
    「最近一次后台检查」行；「同时推送价格与集中度」勾选文案改为明确
    「软件打开时也推送…后台始终只查打新时限」；注册按钮文案带范围说明。
- **N0.4 文档口径**：README 阶段段落去掉硬编码 v0.10.0，指向 `VERSION`/`PHASE` 单一来源；
  current.md 状态修正为 v0.13.3（原写 v0.13.1 已过时）。
- 验证：构建 0 警告；C# 373 → 382 全过（新增 `TransactionReversalTests` 8 例 +
  风险覆盖 4 例 + `NotifyLogReader` 5 例；原 `RemovesASingleTransaction` 改写为冲正语义）；
  Python 316 全过；UI 冒烟（六页导航 + 设置窗口）通过。

## 2026-09-05 - N1 小白模式与任务型首页（小白安全化计划第二阶段，第一批）

来源：`.omx/development/novice-finance-product-plan.md` §6。

- **接手现场**：工作区有前一轮 Agent 留下的半成品——`SymbolResolver` + 测试已写好但未接 UI；
  `PortfolioSettings` 的小白档案字段（UiMode/OnboardingCompleted/Goal/HorizonYears/
  EmergencyFundAmount/MaxDrawdownTolerance/WatchMarkets）已完整；
  `PortfolioSettingsTests` 的新测试被追加到类闭合括号**之后**导致全仓构建失败，
  `SymbolResolverTests` 有一处断言参数写反。先修基线（补 Dispose 与闭合括号、
  修正 `Assert.Contains` 参数序），399 全过后才动新功能。
- **新建 Desktop 测试项目** `tests/Caishenfolio.Desktop.Tests`（net8.0-windows + WPF + xunit，
  入 slnx）——此前只有 Host 测试项目，桌面层的纯逻辑（术语表、待办推导）从此可测。
- **N1 术语白话解释**：`TermGlossary`（计划 §6 列的 xirr/twr/drawdown/percentile/carry/
  adjustment/slippage 七个 + unpriced/reversal/drift 三个产品语义）。每条 = 是什么 + 诚实的
  局限警示（如「历史最大回撤 30% 只是历史，未来可能更深」）；未知词条返回 null 不编造。
  首页 KPI 与记一笔表单就地挂 tooltip。
- **N1 记一笔免内部代码**：`LedgerView` 标的框接入 `SymbolResolver`——
  输入 `600000`/`浦发银行`/`AAPL` 即可，实时显示「✓ 已识别：SSE:600000（按代码规则识别：…）」；
  有歧义时列出候选并**点击选用**；记账时未解析/有歧义直接拒绝（fail-closed，
  账本领域层强制 `EXCHANGE:CODE`，UI 层负责把人话翻译过去，绝不让用户背内部代码）。
- **N1 首次引导**：`OnboardingWindow`——本位币、目标、资金期限、应急金、可承受回撤、
  关注市场六问，全部可留空；「跳过」也标记已完成（OnboardingCompleted 的语义就是
  绝不二次打扰）。保存走既有 `ApplySettings`（校验→持久化→采用）。
- **N1 小白首页**：`NoviceHomeView` + `NoviceTaskList`（纯函数，可单测）——
  按计划四问组织：① 我有多少钱+哪些数字不确定（KPI + 完整性状态）；② 我的钱在哪
  （品种/市场/货币配置条）；③ 今天做什么（待办卡：建账户→录持仓→缺价格（红色紧急）→提醒）；
  ④ 如果想做点什么（研究入口，全部带「研究模拟/历史回测」徽标，明说不动真实账本）。
  你的档案卡显示引导采集的结果，可随时重开。
- **N1 模式切换**：小白模式（默认）＝首页落地、隐藏研究组导航（首页徽标入口可达全部功能）；
  专业模式＝原全部导航。选择持久化到 `ui.mode`，左栏底部一键互切。
- **N1 视觉区分**：主窗口顶栏新增页面上下文徽标——真实账本（绿）/研究模拟（蓝）/
  历史回测（紫）/系统（灰），随 ShowPage 切换；首页模拟卡蓝边框呼应。
- **N1 文案诚实化**：「AI 建议网格参数」→「规则估算网格参数」×4 处（GridView/GridWindow 的
  xaml + cs）+ tooltip 注明「基于分位数与 ATR 规则估算，不是大模型，不构成投资建议」。
  （实现本就是启发式，结果框原本就写「非大模型」，只是按钮名在吹牛。）
- **冒烟脚本升级**：`ui_smoke.ps1` 学会处理首次引导——检测「欢迎使用 OMNIX」窗口则
  UIA 点击「跳过，直接进入」再等主窗口标题（顺带验证了引导窗口 BAML 真的能加载）；
  导航走查加入「首页」。注意：本次冒烟在本机真实状态库上把 onboarding 标记为已完成，
  之后手工启动不会再弹引导（首页「填写/修改档案」可重开）。
- 事故 2 起（详见 error-ledger）：edit 工具改 .ps1 会**丢 BOM**（PS 5.1 中文乱码+解析错乱，
  error-ledger 已有两同类前科，这次是第三个入口）；修 .ps1 时一次 edit 误吞了
  `function Invoke-DialogCheck {` 声明行。
- 验证：构建 0 警告；C# **420** 全过（Host 399 + Desktop 21：TermGlossary 14 例 +
  NoviceTaskList 8 例）；Python 316 全过（1 skip 为既有网络跳过）；
  **UI 冒烟通过**（引导跳过 + 七页导航 + 设置窗口）。
- N1 未完项（见 handoff）：拼音检索（SymbolResolver 已支持中文名匹配，拼音需在
  Python 搜索侧扩展）、高级参数折叠/向导化、TWR 展示、流动性需求采集。

## 2026-09-05 - N2 换汇记账与兑换路径决策实验室（小白安全化计划第三阶段）

来源：`.omx/development/novice-finance-product-plan.md` §7（N2.1~N2.3；N2.4 提醒并入 N3 批次）。

- **N2.1 手工换汇记账向导**：
  - 账本页「记一笔」新增「换汇」类型：换出币种+金额、换入币种+金额（回单两边如实填写）、
    手续费（换出币种）、日期与备注。换入字段仅换汇类型启用。
  - 实时提示按两边金额算出的成交汇率（「1 CNY = 24.5 JPY，仅展示，不用于入账」）；
    到账金额必须手填，禁止用汇率反推（domain 层 `FxExchange` 本就按回单两边金额入账）。
  - 流水表换汇行显示「100 CNY → 2,450 JPY」两边金额，不再只显示单边。
  - 限制（有意）：换汇在单账户多币种内入账（domain 语义）；跨账户换汇留待后续批次。
- **N2.2/2.3 多路径比较（Python 分析模块 `research/fx_paths.py`）**：
  - 五条路径：已持有→目标 / 新本位币直换 / 绕行已持有币种（两段点差）/ 暂不兑换（估值）/
    分批（默认各半）。全部 Decimal 计算，边界统一 HALF_UP 舍入到 6 位并文档化。
  - 每条路径输出：起始资产与机会成本、毛到账、成本明细、净到账、按基准金额归一的
    可比值、可追溯公式字符串（「毛额 = 2450 ÷ 159.97 = 15.315372 USD」）。
  - 头条数字：已持有路径相对优势（毛额/净额）、盈亏平衡历史成本价、隐含交叉汇率
    （带公式与来源）。报价统一「1 USD = 6.726 CNY」方向声明 + 来源 + 日期；
    供应方向相反的报价自动反转并在 provenance 记录 inverted_from。
  - 诚实语义：成本未填 = 「gross_only（仅毛额比较）」而非按免费算；缺关键报价 =
    「missing_quote（fail-closed）」，绝不估算；行情为参考价非可成交报价写入 disclaimer。
  - **固定验收样例逐字复现**（计划 §7）：C=6.7260、J=159.97、E=24.5 →
    直换毛额 14.867678 USD、旧日元路径 15.315372 USD、相对优势 3.011%、
    隐含 CNY/JPY 23.783824——四组数字与计划手算一致，各有测试钉死。
  - 费用反转有测试：毛额 +3.011% 的优势在已持有路径 5% 成本下净额转负。
- **服务端与客户端**：
  - `POST /research/fx-paths`（app.py `research_fx_paths`）：报价取自行情源 fail-closed，
    已持有仓位与历史成本由调用方传入（C# 账本持有该事实）；缺参 400、缺报价 422。
  - `AnalyticsCoreClient.PostFxPathsAsync` + 8 个 DTO（`FxPathRequestCost` 的 null 字段
    = 未知而非零，与 Python 侧语义对齐）。
- **C# 侧 `FxExchangeHistory`**：从流水读真实换汇历史（冲正对排除、方向敏感、
  implied rate 仅展示）；汇率页「按账本填入」自动带出已持有外币现金余额 +
  按最近一次换汇成交价折算当前持有量的成本。
- **汇率页新增「兑换路径比较」面板**：输入比较金额/目标货币/已持有（可预填）/
  三条路径的合计成本%；输出优势卡（毛/净）、盈亏平衡卡、报价溯源行、五张路径卡
  （状态徽标：净额已计入成本 / 成本输入缺失 / 缺报价 / 非兑换路径）。
- 页面徽标修正：汇率/估值页此前误标「真实账本」，实为只读研究 → 改为「研究模拟」。
- N2.4 换汇提醒（净到账目标/优势阈值/区间）：并入 N3 批次（提醒基础设施在那期建）。
- 事故（详见 error-ledger）：`PathCost` 固定费为 None（无固定费）被误判为「币种无法折算」；
  载荷 `_pct` 字段百分数单位约定与 fx_carry 的分数约定不一致，测试抓出后确立全仓规则。
- 验证：构建 0 警告；C# **427** 全过（Host 399→406：`FxExchangeHistoryTests` 7 例；
  Desktop 21）；Python 316→**342** 全过（`test_fx_paths` 21 例 + `test_fx_paths_route` 5 例）；
  UI 冒烟通过（七页导航含改造后的汇率页）。

## 2026-09-05 - N3 可信后台提醒（小白安全化计划第四阶段，第一批）

来源：`.omx/development/novice-finance-product-plan.md` §8。N2.4 换汇提醒顺延至 N3 第二批。

- **提醒去重/冷却/重入状态机（`AlertGatekeeper` + `AlertNotificationState`）**：
  - 事件身份 = kind+symbol+title（消息里含实时价格，入键会导致每次都是"新事件"）；
  - 一条规则覆盖两类语义：新事件/条件重新进入（消失后再现）→ 立即发；条件持续触发且
    上次**送达**在冷却期内 → 静默。状态存 `state/alert-state.json`（30 天未见自动剪枝，
    损坏按空态读——丢的只是一次去重，绝不会丢提醒）；
  - 送达与观察分离：只有真正送达（任一渠道成功）才推进 lastNotified——投递失败下次
    重试，不会被自己的冷却吞掉；`WasInRange` 每次观察都更新（重入检测独立于投递结果）。
- **HeadlessNotifier 两层架构（计划 §8）**：
  - 层 1（永远跑）：打新时限——只读账本，无网络依赖；
  - 层 2（可选，`BackgroundExtendedChecks` 开关）：注入式扩展检查，跑完整
    `PortfolioWorkspace.RefreshAsync`——**同一求值器、同一阈值、同一交叉核对设置**，
    前后台提醒是同一句话；层 2 任何异常捕获为 limitation（"扩展检查失败：…打新时限
    提醒不受影响"），绝不阻塞层 1；
  - 层 2 结果与层 1 按事件身份合并去重（扩展求值也会重算打新提醒，同事实不重复出现）。
- **`BackgroundExtendedCheck` 运行器（Host）**：先探活 8765 复用已开应用的核心；
  没有则临时启动（venv 复用→broker 启动→等健康≤60s）并在结束后**只停自己启动的**。
  门控集成测试（`CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS=1`）真实跑通全链路（4 秒），
  常规测试与 CI 自动跳过。
- **外发消息卫生（计划 §8：不含凭据/路径/敏感备注）**：`NotificationDispatcher` 出站前
  剔除「（备注：…）」后缀（计划价位的用户备注是最可能藏敏感信息的地方）+
  `SensitiveValueRedactor` 新增盘符路径正则（`C:\Users\…` → `[路径已隐去]`）。
- **价格陈旧度改交易日口径**：周五的报价在周一检查 = 1 个交易日旧（原 3 天）；
  节假日未建模（无交易所日历），文案明示「交易日」口径。前台后台同时生效。
- **设置如实化**：`NotifyLogReader` 范围声明随 `BackgroundExtendedChecks` 动态变化
  （开=「打新+价格/集中度…失败不影响」；关=原「只覆盖打新时限」）；设置页新增
  「后台扩展检查」开关（勾选瞬间声明卡就变）与冷却时间（1–720 小时，默认 20≈每日
  任务每天每事最多一次）；注册按钮文案与实际范围联动。
- **App.xaml.cs 接线**：`--notify` 按设置组装扩展检查（核心根上溯发现 + 凭据注入 +
  状态库），手工验证 exit 0、无窗口、日志为新格式「已检查：打新时限（未开启扩展检查…）」。
- 验证：构建 0 警告；C# **450** 全过（Host 429：新增 `AlertGatekeeperTests` 21 例 +
  两层隔离/合并/范围/重试 9 例 + 消息卫生 3 例 + 设置往返 2 例 + 门控集成 1 例；
  陈旧度测试改交易日口径并加周末用例）；Python 342 全过；UI 冒烟通过（含设置窗口
  新控件）；`--notify` 手工验证通过；门控集成测试通过且无遗留 Python 进程。
- N3 第二批未完项：N2.4 用户自定义换汇提醒规则（净到账目标/优势阈值/区间）、
  交易所节假日日历、市场时区精确语义。

### N2.4 换汇提醒规则（N3 第一批收尾，同日）

- **`FxAlertRule`（存 `NotificationSettings.FxAlertRules`，随加密 JSON 持久化）**：
  两类规则——`RateBand`（货币对进入区间，上下限任一可空）与 `HeldAdvantageAbove`
  （已持有外币路径相对新资金直换的毛额优势 ≥ 阈值）。
- **`FxAlertRuleEvaluator`（纯函数）**：报价与优势作为注入的查询函数传入，边界与
  阈值全部可单测；取不到报价/算不出优势 → note（"未评估"）而非猜测；文案只陈述
  "你设定的事实被触发"并自带否定声明（"不代表现在是换汇的好时机"/"软件不判断
  换汇时点"）——计划 §8 明令禁止的「最佳换汇时机已到」措辞有专门测试守护。
- **接线（`BackgroundExtendedCheck`）**：工作区刷新后按规则预取数据——区间规则按
  货币对调 `GetFxRateAsync`（去重缓存）；优势规则从账本取持有量 + 换汇历史成本，
  调 `PostFxPathsAsync`（与汇率页同一数学，零重复；优势是比值，比较金额可任取），
  读 `held_advantage_gross_pct`。单个货币对取不到只降级为 note，不抛异常。
- **设置页规则编辑器**：类型联动显隐的紧凑表单（区间：货币对/上下限；优势：
  基准/已持有/目标三货币 + 阈值%）+ 规则表格（启用列可勾选、删除选中）；
  保存走既有 `NotificationSettingsStore`（DPAPI 加密）。
- 新增 `AlertKind.FxBand/FxAdvantage`——自动继承去重/冷却/重入语义与消息脱敏管道。
- 验证：C# **461** 全过（新增 `FxAlertRuleEvaluatorTests` 12 例：区间内外/单边带/
  缺报价 note/阈值达与未达/算不出 note/禁用/措辞守护/设置往返）；UI 冒烟通过
  （设置窗口含规则编辑器）；门控集成测试重跑通过（FX 预取路径无回归）且无遗留进程。

## 2026-09-05 - N1/N2/N3 第二批收尾（N0~N3 全部达成计划要求）

来源：`.omx/development/novice-finance-product-plan.md` §6/§7/§8 残留项清零。

- **N1 拼音检索**：`market/pinyin_index.py`——pypinyin 可用即用（已入 market extras），
  离线种子表内置人工核对的首字母/全拼，转换器可注入；`fuzzy_search_a_share` 对
  ASCII 字母查询增加拼音匹配档（starts → contains → 拼音 → 代码，中文查询行为不变）。
  新增 `test_pinyin_search.py` 7 例（pfyh→浦发银行、pingan→平安、非 ASCII 拒绝等）。
- **N1 TWR 展示**：`WorkspaceSnapshot.Twr` + `TryTwr`——**估值点已含当日外部流**，
  同日落入点值的流水必须从 flows 剔除，否则期初现金把平账记成 -50%（首版即犯，
  测试逼出）；单点历史诚实返回 null（总览显示 —）。总览页新增 TWR KPI + TermGlossary
  就地解释（twr 词条含"它不是你实际拿到手的收益率"警示）。
- **N1 高级参数折叠**：回测页成本/涨跌停六项收进 Expander（默认收起，快慢线两参数
  即可跑第一次回测），各项带 tooltip 白话。
- **N1 流动性需求**：`PortfolioSettings.LiquidityNeed`（anytime/3m/1y/long，未知值
  校验拒绝、旧行读空不崩）+ 引导窗口第④问（七问全集齐）+ 首页档案卡显示。
- **N2 逐对差值**：`fx_paths.py` 每条路径新增 `diff_vs_best`（best_path/basis/abs/
  abs_in_target/pct）——**全比较统一单一基线**：所有可比路径都有净额才用净额，
  否则全部退毛额（绝不允许净额减毛额）；`best_path` 同口径。汇率页路径卡显示
  "距最优 -0.447694 USD（-2.92%，毛额口径）"。
- **N2/N3 提醒规则补全**（N2.4 五类中剩余两类）：`NetArrivalAbove`（净到账 ≥ 目标
  金额，净额口径缺失时 note"未能计算净到账"不猜）与 `BreakevenCrossed`（越过盈亏
  平衡：条件 = 优势 ≥0，重入语义天然覆盖穿越方向翻转）。求值器重构为统一
  `FxPathFacts` 注入（一次预取 advantage/net/cost-rate/breakeven，四类规则共用），
  新增 `AlertKind.FxNetArrival/FxBreakeven`。设置页规则类型扩为四种（阈值标签随
  类型切换：优势% / 净到账金额 / 无）。措辞守护测试覆盖全部四类。
- **N3 市场日历与时区**：`MarketCalendar`（CN/HK/US/JP 固定日期节假日 + 周末；
  农历/浮动节假日**明示不建模**，文案"约 N 个交易日"）+ `MarketClock`（交易所时区
  的"今天"——北京时间凌晨美股报价不算隔夜陈旧）。陈旧度按标的所属市场日历计算，
  asOf 参数（测试/回填）优先。
- **N3 §8 失败模式专项测试**（`BackgroundFailureModeTests`，按计划点名）：
  限流（429 → 层 1 不受影响）、休眠漏跑（冷却过期后下次唤醒照常提醒）、
  重复唤醒（冷却期内静默不重复）。
- 事故 2 起（详见 error-ledger）：**差值首版基线混用**（split 毛额 15.09 直接与
  direct 净额 14.87 比较——"承诺绝不发生的事"第一版就发生了，探针测试逼出）；
  **split 段重犯 None 多义错**（N2 记过的同一守卫，path 3 修了 split 没修——
  "复制分支逻辑逐条件核对"的规则再次被验证）。
- 验证：构建 0 警告；C# **480** 全过（Host 459 + Desktop 21）；Python **351** 全过
  （+9：拼音 7 + 差值 2）；UI 冒烟通过（设置窗口四类规则 + 回测折叠 + 总览 7 KPI）；
  门控集成测试通过、无遗留进程。
- N0~N3 至此全部达成计划要求。下一站：Gate B 式集中审查（计划 §15）或 N4 证据卡。

## 2026-09-05 - N4 股票决策证据卡（小白安全化计划第五阶段）

来源：`.omx/development/novice-finance-product-plan.md` §9。

- **Python `research/evidence_card.py`（纯组装，无模型、无 I/O、无时钟）**：
  - 计划 §9 的九段结构逐段实现：数据质量（来源/时间/缺失/冲突/复权）、当前估值
    （只列可验证指标+口径注记）、基本面（按披露期呈现，缺失不补写）、市场行为
    （趋势/波动/回撤/活跃度，公式写进 basis）、历史检验（相近价位条件分布 +
    caveat「不能当作操作依据；未含交易成本」）、组合影响（交易前后占比，全部由
    用户账本数字计算）、正/反/未知证据（**每条带 basis 溯源**）、失效条件、结论状态。
  - **结论是证据状态而非操作指令**：风险超限 > 证据不足 > 证据冲突 > 偏正/偏反，
    顺序即策略——用户自设上限能一票否决；关键数据缺失时卡片明确停在「证据不足」
    （计划验收标准"可以明确停止"）。
  - **中性事实不计入倾向**：趋势/波动/条件分布是"两头都切的数字"，只进 sections
    不进 evidence 计数——否则一张只有中性事实的卡会假装有方向。
  - 跨币种候选金额**拒绝折算**：无汇率时占比为 None（不是 0）并说明原因。
  - 措辞禁令测试化：`BANNED_PHRASES` 子串级扫描卡片全部字符串——首版即抓到两处
    违规（"这不等于**该买**"含"该买"；"不构成**买入依据**"含"买入依据"——否定句
    里的禁语子串同样违规，说明子串扫描而非句子分类是对的）。
- **路由 `POST /research/evidence-card`**：Task/Artifact/Audit 映射（artifact 即卡片
  JSON 全文，claim 可审计）；四通道独立降级——某通道失败只出现在数据质量段的
  missing 列表，不影响其余段落（计划质量门"任一来源失败不被伪造数据掩盖"）。
- **C# 侧**：`AnalyticsCoreClient.PostEvidenceCardAsync`（组合参数从快照组装）；
  `EvidenceCardView` 新独立视图——输入区（标的免内部代码走 SymbolResolver、
  候选金额/期限/可承受损失）、结论横幅（状态色）、九段卡片、证据表格
  （立场/事实/依据三列）、回撤 vs 可承受损失对照提示。
- **导航**：左栏研究组新增「证据卡」，**两种模式都显示**（计划 §9 是小白核心能力）；
  冒烟走查扩为八页。
- 验证：C# **480** 全过；Python **371** 全过（新增 `test_evidence_card` 15 例 +
  `test_evidence_card_route` 5 例：Task/Artifact/Audit 映射、风险超限优先级、
  缺标的 400、坏标的 → 证据不足、全串溯源+禁语扫描）；UI 冒烟（八页导航）通过；
  端到端探针（fixture 全通道）结论/六段/计数/审计全部符合预期。
- 事故 1 起（error-ledger）：ui_smoke.ps1 第三次丢 BOM（edit 工具），按既有预防
  规则即时恢复——规则已从"教训"变成"条件反射"，但仍说明该入口需要工具级修复。

## 2026-09-05 - N5 仓位、收益归因与压力分析·第一批（小白安全化计划第六阶段）

来源：`.omx/development/novice-finance-product-plan.md` §10。开工前审计确认：N0~N4 已有实证
（构建+测试+冒烟全绿），N5 在代码树中完全缺失，N6/N7 部分覆盖——本轮补 N5 第一批。

- **`PortfolioContributionAnalyzer`（Host 纯函数）**：每个持仓的盈亏贡献 + 汇率/涨跌拆分。
  - 关键发现：现有估值把**成本也按今日汇率折算**，因此总览的浮动盈亏在构造上就只含
    涨跌部分；汇率贡献 = 成本(原币) ×（今日汇率 − 成本日汇率）被藏在成本折算里。
  - 三数模型：浮动盈亏（总览口径）= 涨跌部分；汇率部分单独列出；
    「相对实际投入」= 两者之和（恒等式，非估计）。
  - 成本日 = 该 (账户,标的) 最早买入/期初流水日；汇率取**不晚于成本日**的最新快照
    （FxConverter 只喂历史汇率，后续汇率不得回溯）；缺快照 → 拆分如实标「未知」+ 警告，
    绝不插值。
- **`TradeWhatIfAnalyzer`（操作前模拟）**：输入标的+金额（新标的需计价货币），
  输出买卖前后单一持仓/货币/现金占比变化，按用户自设阈值给出**提交前警告**
  （计划验收："候选买入导致超限时，在提交前明显警告"）；现金不足如实提示；
  未定价持仓直接拒绝模拟（不按 0 估计）；绝不写入账本。
- **`PortfolioStressAnalyzer`（压力情景）**：用户自填冲击（股票跌%、USD/JPY 变动%、
  最大持仓额外跌%），对已定价持仓与分币种现金重定价。假设清单逐条列出（可复算）；
  未定价持仓排除并计数；债券/可转债/场外基金**有意不套用股票跌幅**并在假设里说明——
  猜 beta 恰是本应用拒绝编造的那类数字；结果一律标「假设分析，不是预测」。
- **`RebalanceComparisonAnalyzer`**：回到目标配置的两条路——卖出超配 vs 新增资金补低配，
  金额按目标比例推算并明示非可成交报价；诚实注记：只新增资金只能稀释超配，
  精确回目标需配合卖出；估值不完整时拒绝计算（金额除以不完整总额=伪造精度）。
- **应急金语义（RiskAnalyzer）**：闲置现金检查先扣除用户标记的应急金
  （`Settings.EmergencyFundAmount`，引导窗第⑤问采集）；超限警告文案点名应急金金额。
  应急金不再触发普通闲置现金提醒（计划验收项）。
- **UI：新「风险与模拟」页（RiskLabView，NavRiskLab）**：贡献/拆分表、买入模拟表单、
  压力情景表单、再平衡比较，四块全部只读推演 + 免责声明；冒烟走查扩为九页。
- 验证：构建 0 警告；C# **496** 全过（新增 N5 测试 16 例：拆分恒等式、缺汇率 fail-closed、
  超限预警、现金不足、未定价拒绝、冲击精算、缺口如实、不完整拒绝、应急金两态）；
  Python **371** 全过；UI 冒烟（九页导航 + 设置窗口）通过。

## 2026-09-05 - 修复批次：回撤归因（N5 第二批核心）、CSV 幂等/换汇往返、压力现金口径

- **压力分析器现金基准一致化**：原先对现金按「最新汇率」重折算，与估值快照用的汇率可能
  不同步，导致"当前 vs 压后"基线漂移。改为从估值自身推导：货币切片市值 − 该币种持仓市值
  = 该币种现金（构造上精确）；切片缺失 → 警告并排除。`FxConverter` 参数随之移除。
- **schema v6 → v7：`valuation_position_snapshots`**：每次 `SaveValuationSnapshot` 同事务
  重写当日持仓级快照（数量/原币市值/本位币市值/成本/未定价标记）。未定价行市值存 NULL——
  归因必须看到缺口而不是 0。当日重刷新为更正而非追加。
- **`DrawdownAttributionAnalyzer`（回撤归因）**：最大回撤窗口内逐持仓归因。
  恒等式 Δ(base) = Δ(local)×r_trough + local_peak×(r_trough−r_peak)——三数要么都有要么都无；
  端点汇率缺失 → 汇率部分「未知」+警告；端点未定价 → 拒绝给份额（装 0 会伪造一次暴跌）；
  窗口内开/平仓按 0 计入端点，现金残差行吸收出入金；份额符号约定 负=拖累。
  UI：风险页新增「归因最大回撤」；功能上线前的历史只有总额 → 如实说明无法归因。
- **CSV 导出/导入三个数据完整性修复**：
  1. 内容哈希幂等打通：手工流水的 GUID id 与导入重建的内容哈希 id 不同源，导出→再导入
     会整本翻倍。Preview 现在同时对存量流水计算内容哈希并比对（`DeterministicId` 改为
     **比例尺无关**：SQLite 读回的 1000.0 与重建的 1000 必须同哈希）。
  2. 换汇往返：模板表头补「对方货币/对方金额」两列（导入器本就认识，导出器此前丢对腿），
     模板补换汇示例行。
  3. 冲正对拒绝复活：导出的冲正镜像行打「冲正·」类型标记，导入时明确拒绝；
     已冲正原行按内容哈希识别并拒绝——导出的账本再也无法通过导入"复活"已取消的事实。
- 新发现的陷阱（error-ledger）：SQLite `GetDecimal` 读回的 decimal 带写入侧残留比例尺
  （'1000' 读成 1000.0m），任何对「重读行 vs 重建行」做内容比较的代码都必须先归一化，
  否则相等的内容判为不等。
- 验证：构建 0 警告；C# **504** 全过（+回撤归因 5、CSV 往返 3）；Python **371** 全过；
  UI 冒烟（九页导航 + 设置窗口）通过。

## 2026-09-05 - 六项遗留全清：Gate B/C 审查、跨账户换汇、利率情景、卖出卡、样本外进卡、教育路径、11 步脚本

按用户指令把审计清单的 6 项遗留全部处理。完整审查报告见 quality-gates.md（F1~F9）。

- **Gate B/C 集中审查（第 1 项）**：按 §15 标准逐条对照代码与测试，产出 9 项发现
  （0 个 P0、2 个 P1、5 个 P2、2 个 P3），P1/P2 当轮全部修复（其中 F2/F3/F5 在上一轮
  修复批次已先行处理，本轮补 F1 费用反转测试）。审查报告含审查后全量验证证据。
- **N2 跨账户换汇（第 6 项）**：schema v7→v8 新增 `counter_account_id`（空=同账户，
  旧行语义不变）；`PositionCalculator` 把到账腿记入对方账户；`FxExchange` 工厂加
  `counterAccountId` 参数；账本页换汇新增对方账户下拉（默认同一账户）。冲正自动兼容
  （配对跳过不区分单双账户）。3 例测试：分账户余额、冲正还原、旧行读作同账户。
- **N5 利率情景（第 4 项）**：`StressScenario.FixedIncomeMovePercent` 作用于债券/可转债/
  场外基金；实现为**用户的直接假设**而非久期模型（久期属 §10 后续能力，数据门槛未到），
  假设清单明示"未建模久期"；压力面板加输入框。2 例测试。
- **N4 卖出方向证据卡（第 3 项之一）**：`assemble_card(side="sell")`——同一组事实回答
  相反的问题：正/反证据按方向翻转；卖出影响（占比下降、现金上升、卖出超过持仓占比时
  明示）；§9 质量门要求的税费/机会成本两行必列；禁语扫描在 sell 卡上同样通过；路由与
  Task metadata 记录 side。5 例测试。
- **N4 样本外/参数敏感性进卡（第 3 项之二）**：历史检验新增两项可复算检验——
  前后半切分稳定性（训练/检验式）与可比窗口 10/20/30 敏感性；方向不一致时文案直接说
  "该规律未经得起时间上的样本外检验"/"警惕过拟合"，立场保持 neutral（信息不进倾向计数）。
  2 例测试。
- **N6 教育路径（第 5 项）**：原创三层内容 `FinanceLessons`（小白 6 条 / 进阶 6 条 /
  高阶 5 条，每条=概念白话 + 软件内对应位置），嵌入小白首页折叠区——内容出现在任务
  发生处而非独立资料库；无外部素材，版权干净。
- **§12 十一步小白端到端脚本（第 2 项的可自动化部分）**：
  `NoviceEndToEndAcceptanceTests` 以真实服务执行步骤 1-3、6、8-11（建账/现金/换汇/提醒/
  模拟/压力/冲正/缺价未知），步骤 4/5/7 的 Python 侧由 fx_paths/evidence_card 测试覆盖、
  C# 侧桥接事实（换汇历史 → 路径比较的 held-cost 输入、免前缀搜索）在本测试内断言。
  真人执行部分仍需真实新手，Agent 不可替代——已注明。
- 验证：构建 0 警告；C# **510** 全过；Python **380** 全过；UI 冒烟通过。

## 2026-09-05 - code-review-2026-09-05 审核修复：P1×13 + P2×3 全清，17 反例矩阵全过

审核文档判定"本轮不通过"，本轮按其 §1 执行要求逐项修复。完整矩阵核对见 quality-gates.md。

- **P1-01 资金口径**：`FundingSource`（ExistingCash/ExternalContribution）贯通 C# 模拟器、
  Python 证据卡、桌面 UI；现有现金买入分母不变（反例 30 000/110 000=27.27%）、外部资金买入
  分母增加且现金不动；估值不完整时拒绝模拟（fail-closed）。
- **P1-02 再平衡分母**：新增资金方案改「能容纳现有资产的最小新总额」法（80/20→50/50 需 60
  而非 30）；卖出方案保持现总额口径；两者在文案与注记中明确区分。
- **P1-03 TWR 流后快照约定**：快照=当日全部流水重放后的值（文档化）；估值点当日流已含在
  点值内，从该段增益中剥离而非当作升值。修复审核反例（+50% → 0%）；纯函数 4 例 + 工作区
  集成 3 例（入金/提款/同日多流）。
- **P1-04 统一比较口径**：`comparison_basis`（net/gross）一次计算，顶层 `best_path` 与全部
  `diff_vs_best` 复用；删除 `_best_key` 的旧双口径逻辑。
- **P1-05 分批固定费**：每次实际执行的转换完整扣固定费（50/50 两笔全扣，0%/100% 跳过未
  执行腿）；`net_per_base_amount` 与净到账同一套费用公式。
- **P1-06 净到账费用模型**：`FxAlertRule` 增加 `SpreadPct/FixedFee`（设置 UI 净到账类型
  必填，至少一项）；无费用模型 → 明确拒绝评估（note 说明），绝不默认零费用；后台链路把
  规则费用模型随 `PostFxPathsAsync` 下发。
- **P1-07 外币移动加权成本**：新 `HeldCurrencyCostCalculator`——多批次购汇按实际投入混
  合（反例 200 非 180）、部分换出按均价释放（比例优先乘法，避免循环小数残差——error-ledger）、
  非换汇来源（利息）标「无成本基价」；替换后台检查的「最后一笔反推」。
- **P1-08 现金聚合**：压力测试按币种聚合一次计算；零冲击不变量测试（任意账户拆分下
  StressedTotal==BaselineTotal）。
- **P1-09 数据质量不投票**：交叉核对从 positive 证据降为 neutral 门禁；报价分歧只在超过
  调用方容差（`price_tolerance_pct` 贯通 UI→客户端→路由→卡）时才算冲突；缺估值+财务时
  结论停在 insufficient 而非 positive。
- **P1-10 maxLoss 门禁**：`max_loss_pct` 作为可审计输入贯通 UI→客户端→请求→结论门禁；
  历史回撤（注意负号约定 bug：存储为 -60.0）≥ 用户上限 → `risk_exceeded` + invalidation
  注记，顶部不再显示正面结论。
- **P1-11 真实数据日**：`as_of` = 各通道真实最后数据日的最大值（周末请求不再显示周末
  日期）；`channel_as_of` 通道级时间戳进数据质量段。
- **P1-12 跨账户 CSV 往返**：模板加「对方账户」列（别名解析）；导出写对方账户名；
  确定性 ID 纳入对方账户；往返后两账户两币种余额精确一致（测试断言）。
- **P1-13 裸代码不猜交易所**：字母 ticker 双读（NASDAQ+NYSE）、4 位数字双读（HKEX+TSE）
  且无账本表格命中时**拒绝解析**并列出全部解读；表格唯一命中（含代码部分匹配）是合法消歧。
  IBM/KO/BABA/7203/0700 不再静默落错市场。
- **P2-01**：`ValidateReversals` 批内目标查重（同批双冲正拒绝，整批不落）。
- **P2-02**：回撤归因按 symbol 跨账户聚合（金额守恒测试）；任一账户未定价 → 整标的未定价。
- **P2-03**：卖出卡贯通桌面端（方向/资金来源下拉、maxLoss 上送）；请求序列化测试断言
  side/funding/max_loss/tolerance 全链路可见，响应回读 side。
- 修复过程中新发现并记入 error-ledger：①回撤存储负号约定使 max-loss 门禁失效；
  ②`Account` 常量字段遮蔽 `Account.Create` 解析到 `string.Create`；③移动加权除法残差
  （比例优先乘法规避）。
- 验证：构建 0 警告；C# **541**（+31）；Python **391**（+11）；UI 冒烟通过；17/17 反例
  矩阵逐条有测试（映射表在 quality-gates.md）。

## 2026-09-06 - code-review-2026-09-06 复审修复：P1-R×10 + P2-R×3 全清，19 反例矩阵全过

复审文档（code-review-2026-09-06.md）判定"不通过"——上一轮修复引入或遗留的金融口径
缺陷仍会被小白看到。本轮逐项修复，完整矩阵核对在 quality-gates.md。

- **R01 跨币种暴露**：旧公式把"目标币种+金额−本位币现金减少额"混在一个分子里，
  CNY 现金买 USD 资产时 USD 暴露完全不增加。改为两侧分开更新（同币种净零、跨币种
  目标增加），本位币现金不足时明说"外币现金需先换汇（费用未计入）"而非静默可行。
- **R02/R03 单位与原因**：发现真 bug——UI 告诉用户"20 表示 20%"但服务端把 20 当小数
  与 0.6 比较，60% 回撤 20% 上限竟输出 positive。契约统一为 API 0~1（服务端拒绝越界）、
  UI 一次换算；`conclude` 改收结构化 `risk_reasons`，每种风险（持仓上限/最大损失）单独
  成句，多触发并列——不再一律说"突破单一持仓限制"误导用户少买而不是认识到波动超限。
- **R04 分批排名值**：上一轮我写的归一化把 `split_fraction` 又乘了一次且除以完整成本，
  排名值缩小约 200 倍（13.09 → 0.065）——分批路径永远不可能胜出。统一为
  「每 base_amount 总支出的目标币净额」：net ÷ (历史成本×frac + 新资金×(1−frac)) × amount。
- **R05 两段固定费**：仅填固定费时净额=毛额（费没扣）。所有分支实际扣费；币种不可折算
  保持 gross_only。
- **R06 缓存键**：`FxPathFacts` 按 (三币种, 点差, 固定费) 缓存——旧键不含费用，第一条
  无成本规则的 facts 会污染第二条有成本规则。真进程集成测试（venv+核心 57s）验证
  费用隔离 + 触发/静默两态。
- **R07 外币成本池重写**：只看换汇记录的旧池漏掉提现/消费/手续费/买卖。新实现按日聚合
  净变化后入池（同日转账净零不动池），`HeldCurrencyDeltas` 显式定义每类流水对持仓币种
  总量的 (Δ, 成本) 语义；按均价释放、未知来源入未定价池、余额对账双向处理。
- **R08 显式零目标**：`Validated` 丢弃 0 权重导致"股票 0% 现金 100%"保存后无法生成清仓
  方案。0 是陈述不是缺省；测试走完整 Validated→Save→Load→Compare 链。
- **R09 通道日期**：字符串 `max()` 让 "2026Q1" 赢过所有真实日期。严格 ISO 解析、报告期
  不参与顶部日期、通道旧超一年出数据质量警告。
- **R10 北交所**：430/830/870/920 系列裸代码被归为 FUND。C# 对齐 Python 的 BSE 前缀
  语义（43/83/87/88/92）。
- **P2-R01**：schema v9 过滤唯一索引（`WHERE reverses_transaction_id <> ''`），双 Store
  实例测试。**P2-R02**：桌面发送真实地区/币种权重+标的元数据，服务端计算交易后值，
  未知明说。**P2-R03**：15 处 SQLite 连接 `contextlib.closing`，`-W error::ResourceWarning`
  全量通过。
- 旧测试更新两处：`ZeroWeightsAreDropped…`（断言的正是 R08 缺陷，改语义）、
  `TimeWeightedReturnIsUnaffected…`（上一轮已改）。一处测试自身算术错误
  （1000×$10 误当 10 000 CNY）用纸算修正。
- 验证：构建 0 警告；C# **571**（+30）；Python **406**（+15，warnings-as-errors）；
  UI 冒烟通过；后台集成 2/2（真进程）。

## 2026-09-06 - remediation-plan-2026-09-06 执行：WP1~WP6 完成（迭代 A→D 自动化部分）

整改计划判定"暂不放行"（4 项金融语义 P1）。按迭代 A（失败反例）→ B（内核）→ C（展示）→
D（验证）执行。完整矩阵与诚实遗留声明在 quality-gates.md。

- **WP1 权重守恒**：上一轮把候选权重直接加进地区/币种切片——合计 110%/109%，旧测试把
  错误固化成期望（计划 §4.1 点名）。重写为共享守恒助手：存量现金买卖家对冲（现金地区−w、
  标的地区+w）、外部资金先缩放再加、卖出地区转 cash 且币种切片不变（不静默换汇）；
  不守恒输入/负权重 fail-closed。顺手发现：测试夹具缺 cash 切片时旧实现悄悄算出"现金不足"，
  现在如实说明。
- **WP2 费用三态**：`None=未知、0=显式零`贯通全部四条路径+后台提醒+设置 UI。**不变量
  测试逼出一个计划未点名的真缺陷**：点差已知、固定费未知时旧代码把固定费当零输出 ok——
  修复后 fx_paths 45 例全绿，需要显式零的旧测试逐个补 `fixed_fee="0"`（契约变化照实改
  断言并注明原因，非放松）。
- **WP3 逐笔重放**：删除按日净额聚合（同日提现+无关入账曾被抵消成"转账"）。跨账户转账
  按计划 §6.2 落保守短期方案；显式 CashTransfer 列入遗留。
- **WP4 多频道日期**：`market_as_of`（严格 ISO）+ 顶部「最新行情截至」+ 桌面逐项渲染
  K线/报价/估值/财务期；任何落后即警告（删 365 天一刀切）；报告期不进日期排序。
- **WP5 测试真实性**：`PercentInputParser` 生产类消除测试镜像；「分批最优」假测试改名
  并收紧（断言 best_path=最便宜单一路径）；三组性质不变量测试。**更正了 quality-gates
  里不实的"人工验证四种视觉状态"表述**——实际未做真人验收，已改为如实声明。
- **WP6 依赖**：真 venv 版本核对无告警（审核环境的问题）；pyproject 锁定 requests 链；
  未屏蔽任何警告类。
- 验证：构建 0 警告；C# **579**；Python **425**（`-W error` 零警告）；真进程后台链路 2/2；
  UI 冒烟通过。§13 真人验收与显式 CashTransfer 如实列为遗留。

## 2026-09-06 - remediation-recheck 修复：P1×4 + P2×5 全清（生产链测试补齐）

复审指出的模式问题是本轮主题：**直调内核的测试掩盖了生产接线的断点**。四项 P1 全部
是"内核正确、链路断开"——funding_currency 没进请求字典、卖出超持仓时现金用了请求额
而非可卖额、汇率页根本无法输入固定费、预填还在用已废弃的最后一笔汇率。完整矩阵在
quality-gates.md。

- **P1-01**：客户端参数+序列化、桌面传值、真进程集成测试三件套。链路测试先踩了两个
  坑（响应有 `card` 包装层；broker 无凭据时默认 provider=auto → 需在宿主环境设 fixture），
  均已在测试内解决并记录。
- **P1-02**：卖出重写为"先算实际可卖权重，全部后状态用同一个数"；守恒校验从买入分支
  提取共用；地区切片<持仓占比 fail-closed。测试厘清了两个"现金"口径（现金余额 vs 地区
  cash 切片）的差异=非权益非现金切片，写成不变量。
- **P1-03**：汇率页三条路径各补固定费+币种输入；非法文本提交前报错；真链路验证
  0 点差+1 USD → 净额 15.3333（fixture USD/JPY=150：毛额 16.3333−1）与仅固定费 → None。
- **P1-04**：预填提取 `LedgerHeldCostPrefill` 纯函数；200≠180、提现缩放、未知入账整体
  留空+说明、纯外部入账四反例。
- **P2**：before/after DTO+渲染（BeforeAfterLines，仅动切片显示前→后）；日期格式异常
  警告（provenance 保留）；季度序号陈旧度（发现 ISO 日期喂 YYYYQn 解析器必 None 的缺陷）；
  pypinyin 注入协议（None=无法罗马化）双环境测试；门禁记录三处更正（过时测试名、WP4
  过度声明、行 11 证据）。
- 全量验证全部用 managed venv（复审 P2-04 要求）：C# 586 / Python 434（-W error）/
  门控链路 4/4（真进程）/ UI 冒烟 / git diff --check。

## 2026-09-07 - remediation-recheck-2 修复：F-01~F-04 全清（含冒烟布局断言）

复审二轮判定未通过：上一轮的 4 项 P1 中 2 项"部分通过"的尾巴（布局重叠、卖出不变量
缺口）升级为本轮 P1。完整映射在 quality-gates.md。

- **F-01**：我上一轮给汇率页加固定费面板时，直接复制了比例面板的 `Grid.Row="2"`——
  两个面板同格叠放，后声明者覆盖前者的绘制与命中。构建与导航冒烟都发现不了。
  修复（拆行）之外，本轮的关键交付是**冒烟脚本的程序化布局断言**：UIAutomation 按
  AutomationId 定位 6 个成本输入框、断言宽高>0 且两两不相交。"页面能加载"从此不再
  冒充"布局可用"。
- **F-02**：卖出校验的两个真缺口——(A) 地区切片与**钳制后卖出量**比较（卖 5% 时只要求
  切片≥5%，持仓还剩 15% 却装在 5% 地区里也放行）；(B) 货币权重完全没校验（120% 直传
  UI）。修法是抽 `_validated_distribution` 共用校验器（买入卖出同源）+ 切片与**完整
  持仓**比较 + 输出端二次不变量。复审两个生产函数反例（us=5%/single=15%；120% 直传）
  均转为测试。
- **F-03**：三个静默路径——`fromisoformat` 接受紧凑格式（词典序 max 选错主日期）、
  仅异常财报期时整个扫描被跳过。严格正则+round-trip、解析后排序、任一通道有数据即
  扫描。顺带钉死闰日边界（2024-02-29 过/2025-02-29 拒）。
- **F-04**：tooltip 与实现脱节（"最近一次换汇"vs 移动加权）；费用完整性提示写了就被
  "正在取报价"覆盖。改持久化字段+结果渲染后写回。
- 验证（DoD 逐条）：构建 0/0；C# 586；managed venv `-W error` 441；真进程 4/4；
  冒烟含新布局断言；Release MSI 0/0。

## 2026-09-07 - 第三轮独立复审

- 新增报告 `code-review-2026-09-07-remediation-recheck-3.md`；产品代码未修改。
- 原布局/卖出反例通过；发现 P1 外部资金单仓占比及对应测试期望错误、P2 无合法日期时标题回退非法值。
- 独立执行 .NET 586、Python 441（1 skip）、真进程 4/4、UI 布局冒烟、Release MSI，全过；`git diff --check` 无空白错误。
- 额外 computer-use 图形检查运行时初始化失败；不据此宣称多 DPI、键盘或桌面净额完整验收。
- 已更新 current/handoff，下一步由整改 Agent 按报告两个问题和 UI 验收要求修复。

## 2026-09-07 - remediation-recheck-3 修复：R3-01 + R3-02 全清

- **R3-01 外部资金稀释**：买入公式 `旧占比 + 新金额/新总额` 的错误在于旧占比没换分母——
  外部资金扩大总资产后，旧持仓（20% × 100 000）应先除以新总额再叠加，正确值
  30 000/110 000 = 27.2727%，旧式 29.09% 会误触发 28% 上限并出现"单仓 29% > 地区 27%"
  的矛盾。复审指出旧测试本身把错误公式固化成期望——一并纠正（36.3636% 而非 39.09%）。
  修复含跨维度一致性断言（标的 after ≤ 地区/货币 after）与真链路验证。
- **R3-02 日期回退**：上轮严格校验后，回退分支又把未校验的 bars 日期/请求日期塞回
  标题——"最新行情截至 2026Q1"。删除回退：无有效市场日期 → 标题空 + 桌面"行情日期未知"；
  原始值保留在 channel_as_of 并带格式警告。
- 门禁全过：C# 587 / managed venv 449（-W error）/ 真进程 5/5（新增外部资金链路）/
  冒烟含布局断言 / MSI 0/0。
- 复审 §4 的口径要求已执行：current/handoff 顶部不再写"DoD 全过"，改为
  "自动门禁通过，完整 UI DoD 未完成"并列明未测项。

## 2026-09-07 - 第四轮独立复审完成

- 新增 `code-review-2026-09-07-remediation-recheck-4.md`，R3-01（P1）、R3-02（P2）通过关闭，本范围内无新增阻塞问题。
- 独立核验代码、测试预期与生产结果；重跑 .NET587、managed Python449项含1skip、真进程5/5、UI冒烟及MSI，全部通过。
- 纠正 current/Latest Review 仍保留第三轮“待修”的状态滞后，保留历史；完整UI待验收清单不变。
- 产品代码未修改；下一步按报告进行人工焦点/点击、桌面净额、DPI/窗口与新手任务验收。

## 2026-09-07 - recheck-4 后续：§5 UI 验收前两项程序化入冒烟

第四轮复审已通过代码修复，§5 四项 UI 验收留人工。本轮把可自动化的两项做进冒烟脚本
（之后每次冒烟自动复查），后两项如实保留人工：

- **焦点/可写/非屏外检查**：6 个成本框逐一 ValuePattern 写入、SetFocus 后
  HasKeyboardFocus 断言、IsOffscreen 断言。
- **表单→净额端到端**：真实页面操作——费币种选 USD（Expand+Select）、全 0 费用比较
  （断言净==毛且「净额已计入成本」）、0 点差+1 USD 固定费（断言净=毛−1，数值解析比较）。
  这是复审 §5#2 要求的"桌面输入后观察净额"页面级证据，链路测试之外的第一手 UI 证据。
- 调试踩了 4 个 UIA/断言坑（属性名、PowerShell 方法链、ComboBox 折叠选择、
  provider 相关的期望值），全部记入 error-ledger 并沉淀 skill-candidates 模板。
- 验证：扩展冒烟 12 步全绿；回归 C# 587 / Python 449（-W error）全过。
- **仍未测（如实）**：DPI 三档、窄窗口、Tab 顺序完整遍历、真人新手——保留人工。

## 2026-09-08 - 第五轮 UI 补充独立复审

- 产出 `code-review-2026-09-08-ui-acceptance-recheck-5.md`，第四轮代码通过不变；本轮 UI 验收暂不通过。
- 构建 0/0；C# 587；managed Python -W error 449 项（448 pass / 1 skip）。
- 两次真实冒烟：布局/焦点/九页导航通过，场景 A 请求 HTTP 422；B 和尾部设置窗口未执行到。
- 保留窗口诊断确认实际 provider=cache+upstream、synthetic=false；源码确认桌面凭据覆盖继承 fixture；未取得 422 响应体，不误判具体原因。
- 发现持有币种未选择、首条文本断言覆盖不足；失败字符串插值错误；登记 R5-01/02/03。
- 只新增审核报告、同步协议记录；未改产品代码、未改用户凭据/账本；本轮启动应用已关闭。

## 2026-09-08 - recheck-5 UI 验收整改：R5-01/02/03 全清，连续两次冒烟全绿

第五轮复审判定 UI 补充验收暂不通过——表单端到端在复审环境连续 422，根因是 **fixture 从未
在真实桌面链路生效**（用户凭据 ApplyToEnvironment 覆盖了继承的环境变量；冒烟一直在读用户
真实状态跑 cache+upstream）。完整证据映射在 quality-gates.md。

- **R5-01 隔离启动**：新增 Host 授予的测试隔离机制——桌面认 `CAISHENFOLIO_TEST_STATE_ROOT`
  （整棵状态树进隔离根），broker 在凭据注入后应用 `CAISHENFOLIO_TEST_FORCE_PROVIDER`
  （仅测试变量可压过凭据，不放宽生产优先级）。冒烟预置隔离账本（全表 schema v9 + 一笔
  CNY→JPY 回单）、启动后先过 provider gate（HTTP /health 断言 fixture+synthetic，错源当场
  失败并报 provider 名）。负例脚本验证 auto 核心被 gate 拒绝。
- **R5-02 逐卡断言**：持有币种走真实 UI 路径（按账本填入 → 断言 2100/100 与 JPY 选中）；
  `Get-CardText` 从 Label 元素上溯卡片容器收集（Label/金额是不同 UIA 元素）；持仓卡与新
  资金卡各自断言两场景；绕行留空断言「成本输入缺失」；等"已比较 N 条路径"标记翻转后才
  取当前渲染。
- **R5-03**：失败文本先赋值再读（修内插输出属性路径的 bug）；焦点检查口径收紧为
  "UIA 可写/可聚焦/非屏外（鼠标命中与 Tab 遍历未测）"。
- 实现教训四条记 error-ledger（预置库须全表、-f 拼 SQL 的 % 坑、UTF-8 无 BOM、
  UIA 卡片文本须按容器收集）。
- 验证：连续两次完整冒烟（run-1/run-2 JSON 均 passed 14 步 failure=null）；构建 0/0；
  C# 587；managed venv 449（-W error）；门控真进程 5/5；MSI 0/0。
- 仍开放（人工）：DPI 三档、窄窗口、鼠标命中、Tab 遍历、真人新手。

## 2026-10-07 - 工作区开发状态盘点

- 新增 `development-status-2026-10-07.md`：功能主干已成形，处于第五轮整改待复审与真人/发布收尾阶段。
- 核对开发方 2026-09-08 两份 14 步冒烟通过报告及相关代码；不等同本日重跑或正式复审。
- 今天独立执行构建 0/0、C# 587、managed Python -W error 449 项（448 pass / 1 skip）、diff --check 通过。
- 未运行 UI、门控真进程、MSI、实盘或真实提醒；未改业务代码，未提交、发布或清理文件。
- 记录大量未提交变更及过期“未做”清单；校准 current / handoff 的最新状态，保留既有历史证据。

## 2026-10-07 - 开发 Agent 收尾任务书

- 新增 `agent-delivery-plan-2026-10-07.md`，将用户指定的四项收尾工作拆为 WP0～WP4。
- 明确现有功能不可重复开发、修改归属、测试状态/进程隔离、费用防串线负例、真实 DPI 与真人验收边界。
- 给出正式回归命令、交付文件清单、Git/发布授权边界与完成标准；未进行业务代码修改、测试重跑、提交或发布。

## 2026-10-07 - 第六轮交付独立复审

- 新增 `code-review-2026-10-07-delivery-recheck-6.md`：1 P1 + 4 P2，当前交付暂不通过；第四轮两项保持关闭。
- 复验构建 0 错误/2 警告、C# 599、managed Python 449 项（1 skip）、真进程 5/5；负例脚本报告 3/3。
- 无写入探针确认正式根被接受、标题契约失败；未显示 WPF 控件探针确认入金被隐藏旧数量错误阻断。
- 核对旧 UI/DPI 报告及用户读屏说明；不将 09:24 前证据迁移到后续界面版本。
- 仅新增审核文档及 .local/recheck-6 诊断材料，未改产品实现、未操作正式账本、未提交或发布。

## 2026-10-07 - 第七轮整改独立复审

- 新增 `code-review-2026-10-07-remediation-recheck-7.md`，原隔离 P1 核心、标题契约和复制负例问题关闭；剩余 3 P2，明确修复顺序及验收清单。
- 构建 0/0；C# 602（567+35）；managed Python -W error 449 项（448 pass/1 skip）；真进程 5/5；生产负例 3/3；diff --check exit 0。
- `.local/recheck-7` 保存无窗口/无账本/内存 HTTP 探针、原始输出、负例与关键源码指纹：确认禁用旧金额、失败/乱序区间错配及盘符相对路径漏检。
- 完整 UI、DPI、窄屏、MSI、真人未执行；不继承旧 UI 报告。只读账本元数据与旧 9 月 6 日声明不同，未推断原因。
- 仅增加诊断与审核交接文档，不修改产品实现，不提交、发布或删除用户文件。

## 2026-10-07 - 第八轮整改独立复审

- 新增 `code-review-2026-10-07-remediation-recheck-8.md`；关闭 R7-01/R7-03 功能缺陷，仅保留 R7-02（P2）的响应归属及完整说明问题。
- 构建 0/0，C# 605（570+35），managed Python 448 pass/1 skip，门控真进程 5/5，生产负例 3/3，diff --check exit 0。
- 原样重跑 R7 控件探针确认修复与剩余乱序反例；日志、源码指纹与账本主文件前后指纹保存到 `.local/recheck-8`。
- 8765 为预存 cache+upstream 核心；一项后台空账本测试复用，未停该进程。完整 UI/显示矩阵/真人/MSI 未执行，不冒称 fixture 自启证据。
- 账本主文件指纹一致；无产品实现修改、提交或发布。下一步限定为剩余修复、正式回归和冻结候选验收。

## 2026-10-07 - 最终候选验收执行任务书

- 按用户新的阶段式协作方式，生成 `agent-stage-final-acceptance-2026-10-07.md`，执行 Agent 一次负责长期回归、冻结/自动门禁、显示人验协调及 MSI 交付。
- 静态核对开发方新修复声明基本对应实现；未重跑构建/测试，不正式关闭 R7-02。
- 明确脏工作区完整基线、非本轮进程不可停止、旧探针预期需转为正确断言、证据必须绑定最终候选。
- 完成后以 final-candidate-delivery.md 集中交回；本轮仅任务书和协议记录，无产品/分支/发布操作。

## 2026-10-07 - FA1 最终候选验收执行（fa1-20261007-1410）

- A 包：FxCarryView 加载核心提为可 await 的 `LoadCarryAsync`（薄封装，逻辑不变），
  `BuildTransaction` 转 internal；新增 `WpfTestHost`（单 STA 线程持有 App+Dispatcher）与
  10 个正式回归（7 请求归属 + 3 类型边界），Desktop 35→45。
- B 包：门禁全过（构建 0/0、C# 615、Python 448+1skip、真进程 5/5 自启、负例 3/3、diff --check）；
  冒烟脚本新增两个关键步骤并重跑两次 18/18；重试史与孤儿核心清理记录在
  `.local/acceptance-final/fa1-20261007-1410/gates-log.md`。
- C 包：MSI 0/0 并核对版本/内容/指纹；200% 当前缩放探针（真实鼠标命中 6/6 + Tab）；其余显示/
  真人项如实 blocked/not_run 并汇总为集中清单；正式账本前后指纹一致。
- 交付：`final-candidate-delivery.md`（索引）+ `acceptance/final-candidate-human-verification-2026-10-07.md`。

## 2026-10-07 - FA1 集中终审与直接收尾

- 原 287 项源码及 MSI 指纹吻合；R7 正式回归经独立运行通过，关闭 R7-02。
- 发现并直接修复 WpfTestHost P1：普通单测仍会启动正式桌面。改普通 Application + 真实资源提取，新增泵启动队列后的隔离断言；仅三个测试侧文件变化，产品不变。
- 最终构建 0/0、C# 616（570+46）、真进程 5/5、负例 3/3；`.local/review-fa1-20261007` 留全部迭代日志及最终 overlay。
- 首次独立测试的两个残留核心核对归属后停止；最终相关端口/核心为空，账本主文件仍匹配原交付，不声称正式目录未访问。
- 新增 `final-candidate-review-2026-10-07.md`；完整 FA1 未通过，剩余引导等待/窗口归属、日志留存与启动稳定性、人验。纠正交付“纯环境抖动”与技能候选“不 Run 即无启动”的错误口径。
- 未提交/推送/发布/安装；不把本轮已修的小问题再分发给执行 Agent。

## 2026-10-08 - FA1 启动与验收工具收尾完成

- 统筹直接修 ui_smoke / negative：本次 PID + Window 类型的引导等待/关闭确认；每次独立证据目录，CreateNew 拒覆盖，源码/二进制指纹和起止/退出码；种子脚本归本次临时根。
- 生产函数负例 3→13；构建 0/0、C# 616、managed Python 448+1skip、固定冷启动 5/5、两次完整 UI 各 18/18，7 份最终 manifest 相同。10 月 8 日续跑真进程 5/5，292 文件复算零漂移。
- 全部 8 次启动留档：初版窗口/标题文本重复匹配失败，修复后固定 5+2 全过；未删失败记录，未把历史超时统称环境原因。
- 正式主账本指纹一致，无测试核心/端口残留，MSI 指纹不变；未提交/发布/安装。新增 `final-candidate-repair-2026-10-08.md`，剩余人工/环境验收，不再分发本批小修。

## 2026-10-08 - 启动 TR1 并分派执行

- 用户要求统筹继续安排下一阶段，已生成 `agent-stage-trial-readiness-2026-10-08.md` 并启动三个子 Agent，而非只给口头建议。
- A 负责换汇结果卡可访问性；B 负责故障注入/恢复契约；C 负责安装布局测试隔离。文件边界不重叠，构建/UI 由统筹串行调度。
- 安装布局旧脚本只有标题检查、未配置隔离账本，不能直接运行；交给 C 先加固再验证。真人、系统显示、安装及外发授权保留原集中清单。
