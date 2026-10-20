# Current Development

## Latest scope — 2026-10-19 外汇核心化 / Android 架构审查

- 用户将产品定位调整为“外汇资产管理和换汇决策为核心，普通资产记账为辅助”；本轮明确只审查，不实施大改。
- 已交付：`D:/Agent/Worktrees/OMNIX-Caishenfolio/reviews/fx-android-architecture-review-2026-10-18.md（仓库外：含真实交易金额，公开仓库不放）`，覆盖现状、模型、差距、复用/重构/退出项、新模型、确定性引擎、Android 迁移及分阶段计划。
- 核心结论：保留回单/冲正/导入契约，新增 Lot 与消耗分配、目标货币和时间情景；Android 本机 Kotlin 领域内核，PC 可继续保留普通资产与研究能力。
- 新发现：绕行固定费未知仍产生净额；历史成本未知使 held 路径被省略；提醒无稳定规则身份，且 UNKNOWN 观察/备注传递不完整。全部记录，未改业务代码。
- 本轮验证：Python 外汇 64 项通过（系统 3.13.7，隔离 fixture，非 managed 全门禁）；Host/Host.Tests 编译完成，vstest→testhost 90 秒超时，0 项执行。证据 `.local/fx-android-review-20261018/`。无 UI/真实行情/Android 验收。
- 后续实施顺序建议：A0 语义/迁移契约 → A1 Lot 与 FX 内核 → A2 行情及单档提醒 → A3 场景 MVP；然后多路径/临界汇率/分档/额度，最后保险/银行报价/AI 解释。本轮没有启动实现或新的并行开发。

## Historical Windows delivery status（保留历史记录）

- Project: **OMNIX-Caishenfolio**
- Next execution brief: **TR1 已启动并分派三个 Agent**，见 `agent-stage-trial-readiness-2026-10-08.md`：A 换汇结果卡可访问语义、B 数据失败/恢复契约、C 安装布局隔离冒烟。统筹负责集中验证和小问题直接修复，不重做 FA1/R7，不替真人验收。
- Status: **本批工程整改及机器门禁通过，完整产品人验尚未完成。FA1-R01/R02/R03 已修；构建 0/0、C# 616、Python 448+1skip、独立真进程 5/5、负例 13/13、固定冷启动 5/5、两次完整隔离 UI 各 18/18。292 文件与最终 UI 清单零漂移，产品源码/MSI 未改。** 旧失败日志不可恢复，不把最新样本通过夸大成全部历史超时已归因。已提交（2026-10-14，两个提交：N0~N5 本体 + 本次整理）；**未发布、未安装**。
- 本次（2026-10-14）实测门禁：构建 0/0、C# **621**（Host 570 + Desktop 51）、Python **448 通过 + 1 skip**（skip 为联网真实行情，有意门控）、UI 冒烟 18/18、安装布局冒烟通过。
  状态行此前写 616、验证段写 587，均为手写漂移，已按实测订正。
- 本次优化：①`ui_smoke.ps1` 旧报告改为**归档**而非拒绝运行（默认命令自 9/8 起实际已失效）；②安装包完成改名 Caishenfolio（`UpgradeCode` 与快捷方式注册表 KeyPath 刻意保持不变，原地升级不受影响）；③删除仓库根目录遗留的 0 字节 `%s` 文件。
- **协作模式（2026-10-19 确立）**：用户=决策者（花钱/公开/发布/动真实数据均需其拍板）；
  统筹=定计划、写任务书、独立验收、裁决、合并（不推送）；开发 agent **1 个**，照任务书开发并自调 Codex；
  Codex 独立审代码细节，**调用无需逐次授权**（额度共用，先看用量）。
  统筹自改门槛：≤20 行、无设计取舍的小问题自己改并注明提交；其余写任务书交 agent。
  worktree/审核目录/临时数据统一放 `D:\Agent\Worktrees\OMNIX-Caishenfolio\`。
  **当前未分配开发主线**，等用户指定。
- 账本 schema **当前为 v9**（`PortfolioStore.SchemaVersion`）。下文 v5/v6 是各功能落地当时的版本，非当前值。
- **FX-01 已修（统筹自改，2026-10-19）**：绕行路径在「点差已知、固定费未知」时曾返回
  `status=ok` 和确定净额（把未知固定费当零）。单段路径本就拒绝，两段分支漏了同一道守卫。
  已补守卫并移除把未知费显示为 `0.0` 的字段；用原始失败输入复现确认，新增 4 条回归。
  审查报告其余三条（FX-02 已知金额缺成本被整体置空、FX-03 换汇不受余额约束、FX-04 提醒缺
  UNKNOWN 态）**尚未修**，属新 FX 领域范围。
- **平台决定（2026-10-19 用户拍板）**：**PC 为主记账端**（用户将有一台常开电脑）。
  手机定位为「看 + 收提醒」的辅助端，不做双写。常开 PC 已解决"关机收不到提醒"，
  且常驻检查快于 Android WorkManager 的 15 分钟下限，因此 **Android 全量迁移降级后置**，
  先验证现有 webhook 推送是否够用。
- **用户反馈（同日）**：现有 PC 记账"很不顺手"。冒烟只验"能填能存"，验不了易用性；
  具体痛点待用户指认后立项。
- **主记账模型确认（2026-10-19 用户拍板）**：改为**账户级现金流**——投入/转出/转到自己的另一个
  账户/当天资产金额，由现金流+期末市值算资金加权收益率（`ReturnMetrics.Xirr` 已具备该签名）。
  股票买卖路径**保留且必须继续可用**，但不再是主入口。参考形态为有知有行账本。
  任务书：`docs/work-packages/WP-01-账户级现金流记账.md`，基准 `d0775f0`，待开工。
- **统筹查清的五条下游规则（写进 WP-01，不得凭印象推翻）**：①「当天资产金额」是市值断言不是
  现金流水；②账户须有明细/余额二选一的估值模式，混用会静默重复计数；③账户间转移在账户层是
  外部流、组合层是内部流；④`FxExchange` 不计外部流是对的；⑤**硬伤**：`TryXirr` 在外部流币种≠
  本位币时直接返回 null（`PortfolioWorkspace.cs:274`），多币种下收益率会整个消失，必须按流发生
  当日汇率折算，缺历史汇率则标「不完整」而非静默 null。
- **WP-01 已验收合并（2026-10-20，`6f98aa3`，未推送）**：账户级现金流记账落地——新增
  「当天资产金额」「账户间转移」两类流水、账户估值模式、schema v10、多币种资金加权收益率、
  新增记录对话框。门禁由统筹在独立 worktree 重跑：Debug/Release 均 0 警告、C# **653**、
  Python **453**（1 skip）、UI 冒烟与安装布局冒烟均过；合并后 `src/`+`tests/` 树与验过的
  `c378c41` 完全一致。
  两条严重问题由统筹用**修复前产品代码**复现确认：缺发生日汇率时净投入变 0 导致凭空
  「+48,500 收益」；余额账户报数后再转账导致合计静默重复计数且自称完整。
  Codex 本轮未运行（版本+额度），接受替代独立审查，记为欠账。详见 `docs/work-packages/WP-01-审核报告.md`。
- **待用户决定（推送前必须定）**：真实交易金额仍在若干**本地未推送**提交的历史里
  （`7d37614` 等）；`origin/main` 停在 `a3afe7c`。推送前决定改写还是接受。
- 应用内更新已启用：下载 → SHA-256 校验 → ECDSA P-256 验签 → 安装；任一层不过即删除文件
- 覆盖: A股 / 港股 / 美股 / 日股；股票 / ETF / 场外基金 / 债券 / 可转债 / 外汇 / 现金

## 已交付

- **R0~R5**：数据语义 → 账本内核 → 估值与收益 → 行情通道 → 桌面双主线 → CSV 导入导出
- **V1~V4**：版本号单一来源（`VERSION` / `PHASE` + 漂移守护测试）、WiX MSI 打包、
  应用内更新检查（GitHub Releases）、CI / Release 工作流
- **A1~A4**（移植自归档 FinWorkbench）：Python 运行时自动供给（uv/venv + 依赖哈希）、
  UI 启动冒烟脚本、组合风险指标（集中度/回撤/配置偏离）、价格提醒
- **S1~S2**：偏好设置持久化（schema v5 `settings`）与设置窗口
- **F1~F5**：技术指标库、诚实回测（成本/样本外/回撤/连亏）、估值分位与基本面、
  汇率利差看板、打新记录
- **G1~G4**：解读层（分档 + 白话解释 + 历史条件收益）、打新日历、界面全接、回测报告页
- **H1~H4**：
  - H1 类型化数据源错误（`market/errors.py`）+ 仅对可重试错误退避重试
  - H2 第二数据源：baostock（A股历史）、天天基金 pingzhongdata（场外基金净值，纯标准库）
  - H3 真实政策利率：纽约联储 EFFR / 欧央行 MRO / 香港金管局 / LPR / 日本央行，
    缓存一天，取数失败回落内置值并标记 `stale`（界面标红）
  - H4 外部通知：企业微信/飞书/钉钉/Telegram/Discord/Slack/自定义 webhook + SMTP 邮件；
    `--notify` 无界面模式 + Windows 计划任务注册（仅用户点击时执行）；
    凭据经 DPAPI 加密后才写入账本
- **N0**（小白安全化第一步，`novice-finance-product-plan.md` §5）：
  - N0.1 冲正代替物理删除：`Reversal` 工厂 + schema v6（`reverses_transaction_id` 列、
    迁移前自动备份 `.bak`）+ `PositionCalculator.Replay` 跳过冲正对 + 账本页「冲正选中」；
    `PortfolioStore.RemoveTransaction` 已删除；CSV 预览拒绝再导入已冲正行
  - N0.2 估值不完整不输出“正常”：`RiskCoverage`（Complete/Partial/InsufficientData）；
    全部未定价时只返回一条 Warning「未知」并抑制集中度与偏离输出；
    部分定价时摘要改为「仅基于已定价部分」并按成本口径给出覆盖比例；
    总览页提醒区新增覆盖率红字条
  - N0.3 提醒能力如实标注：设置页新增「后台检查范围」声明卡与最近一次检查结果
    （`NotifyLogReader` 读取 notify.log 最后一行）；「价格与集中度」勾选文案明确
    只在软件打开时生效
  - N0.4 文档口径：README 阶段段落改为指向 `VERSION`/`PHASE` 单一来源，去掉过时版本号
- **N1 第一批**（小白模式与任务首页，`novice-finance-product-plan.md` §6）：
  - 记一笔免内部代码：`SymbolResolver` 接入账本页，裸代码/名称/ticker 实时解析
    （✓ 已识别提示 + 歧义点击消歧），未解析/歧义拒绝记账（fail-closed）
  - 首次引导 `OnboardingWindow`：本位币/目标/期限/应急金/回撤容忍/关注市场六问，
    全可留空，跳过也标记完成（`ui.mode` + `novice.*` 设置键，`PortfolioSettings` 已扩展）
  - 小白首页 `NoviceHomeView` + `NoviceTaskList`（纯函数可测）：四问结构
    （多少钱+哪些不确定 / 钱在哪 / 今天做什么 / 做点什么会怎样），
    缺价格为红色紧急待办，你的档案卡可重开引导
  - 小白/专业模式切换：小白默认（首页落地、隐藏研究导航），专业完整导航；
    持久化 `ui.mode`，左栏底部互切
  - 真实账本（绿）/研究模拟（蓝）/历史回测（紫）顶栏徽标随页面切换
  - `TermGlossary`：10 个术语的白话解释+局限警示，首页 KPI/表单就地 tooltip
  - 「AI 建议网格参数」→「规则估算网格参数」（4 处 + 免责 tooltip）
  - 新测试项目 `tests/Caishenfolio.Desktop.Tests`（桌面层纯逻辑可测）
  - 冒烟脚本学会跳过首次引导（顺带验证引导窗口 BAML）；导航走查加入「首页」
- **N2**（换汇记账与路径比较，`novice-finance-product-plan.md` §7）：
  - N2.1 换汇向导：账本页「记一笔」新增「换汇」——回单两边金额入账，
    成交汇率实时展示但仅显示；流水表显示「100 CNY → 2,450 JPY」双边金额
  - N2.2/2.3 Python `research/fx_paths.py`：五路径比较（已持有/直换/绕行/持有/分批），
    Decimal 计算 + 可追溯公式；固定样例（6.7260/159.97/24.5→3.011%）测试钉死；
    成本未知=gross_only、缺报价=fail-closed
  - `POST /research/fx-paths` 路由 + `AnalyticsCoreClient.PostFxPathsAsync` + 8 DTO
  - `FxExchangeHistory`（Host）：读真实换汇成本（冲正对排除、方向敏感）
  - 汇率页「兑换路径比较」面板：按账本预填（现金余额+历史成本折算）、
    优势卡（毛/净）、盈亏平衡、报价溯源、五路径卡
  - 页面徽标修正：汇率/估值页 → 研究模拟（原误标真实账本）
  - N2.4 换汇提醒并入 N3 批次
- **N3 第一批**（可信后台提醒，`novice-finance-product-plan.md` §8，含 N2.4）：
  - `AlertGatekeeper` + `AlertNotificationState`（state/alert-state.json）：去重/冷却/
    重入；送达与观察分离——投递失败不推进冷却，下个周期自动重试
  - `HeadlessNotifier` 两层架构：层 1 打新时限永远跑（只读账本）；层 2
    `BackgroundExtendedChecks` 开关下跑完整 `PortfolioWorkspace.RefreshAsync`
    （同一求值器/阈值/交叉核对），失败捕获为 limitation 绝不阻塞层 1
  - `BackgroundExtendedCheck` 运行器：探活复用已开核心，否则临时启动并在结束后
    只停自己启动的；门控集成测试（env 变量）真实验证全链路
  - **N2.4 换汇提醒规则（四类全）**：汇率区间 / 优势阈值 / 净到账目标 / 盈亏平衡
    穿越——`FxPathFacts` 统一事实注入，措辞只陈述"你设定的事实被触发"，
    禁用语测试覆盖全部四类
  - 外发消息卫生：剔除「（备注：…）」用户备注 + 盘符路径脱敏（SensitiveValueRedactor）
  - **市场日历与时区**：固定节假日（CN/HK/US/JP）+ 交易所时区"今天"；农历/浮动
    节假日明示不建模（文案"约 N 个交易日"）
  - 价格陈旧度按标的所属市场日历计算；§8 失败模式专项测试（限流/休眠漏跑/重复唤醒）
  - 设置页：扩展检查开关（声明卡即时联动）+ 冷却时间（1–720 小时，默认 20）+
    四类规则编辑器；`--notify` 手工验证 exit 0 / 无窗口 / 新日志格式「已检查：打新时限」
- **N1/N2/N3 第二批收尾（同日）**：
  - N1 拼音检索（pypinyin + 离线种子拼音，`fuzzy_search_a_share` 拼音匹配档）
  - N1 TWR KPI（总览页 + 术语解释；同日流剔除语义有测试钉死）
  - N1 回测页高级参数折叠（Expander）
  - N1 引导采集流动性需求（七问全集；`LiquidityNeed` 校验/往返/旧行兼容）
  - N2 路径差值 `diff_vs_best`（全比较统一单一基线：全体净额或全体毛额）
  - 首页档案卡含流动性；汇率页路径卡显示"距最优"
- **N4 股票决策证据卡**（`novice-finance-product-plan.md` §9）：
  - Python `research/evidence_card.py`：九段结构（数据质量/估值/基本面/市场行为/
    历史检验/组合影响/正反未知证据/失效条件/结论状态）；纯组装无模型；每条证据
    带 basis 溯源；中性事实不进倾向计数；跨币种拒折算（None 非零）
  - 结论顺序即策略：风险超限 > 证据不足 > 冲突 > 倾向；关键数据缺失时明确停止
  - 禁语子串级扫描（BANNED_PHRASES）抓否定句里的违规；Task/Artifact/Audit 映射，
    四通道独立降级
  - `POST /research/evidence-card` 路由 + `PostEvidenceCardAsync` +
    `EvidenceCardView`（结论横幅/九段卡片/证据三列表格/回撤对照提示）；
    标的输入免内部代码（SymbolResolver）；左栏双模式可见；冒烟八页导航
- **N5 仓位、收益归因与压力分析·第一批**（`novice-finance-product-plan.md` §10）：
  - `PortfolioContributionAnalyzer`：持仓盈亏贡献 + 汇率/涨跌精确拆分
    （三数模型：总览浮动盈亏=涨跌部分；汇率部分=成本×(今日汇率−成本日汇率)；
    相对实际投入=两者之和；缺成本日汇率 → 如实标未知）
  - `TradeWhatIfAnalyzer`：操作前模拟（前后占比 + 自设阈值超限预警 + 现金不足提示；
    未定价拒绝；不写入账本）
  - `PortfolioStressAnalyzer`：可编辑冲击（股票/USD/JPY/最大持仓），假设清单逐条可复算，
    未定价排除并计数；债券/可转债/基金有意不套用股票跌幅（假设中说明）
  - `RebalanceComparisonAnalyzer`：卖出 vs 新增资金两案并陈，缺口如实，
    估值不完整拒绝计算
  - 应急金语义：闲置现金检查扣除 `EmergencyFundAmount`，文案点名应急金
  - UI：新「风险与模拟」页（贡献表 + 模拟/压力/再平衡表单）；冒烟九页导航

## 验证

```powershell
dotnet build Caishenfolio.slnx; if ($?) { dotnet test Caishenfolio.slnx }   # 621 通过（Host 570 + Desktop 51）/ 0 警告
$env:PYTHONPATH="$PWD\python"; $env:CAISHENFOLIO_MARKET_PROVIDER="fixture"
# 正式门禁用应用 managed venv（复审 P2-04），不要用 PATH 上的系统 Python：
$py = "$env:LOCALAPPDATA\Caishenfolio\state\.venv\Scripts\python.exe"
& $py -W error -m unittest discover -s tests/python -p "test_*.py"   # 449 项运行：448 通过、1 skip
scripts\ui_smoke.ps1                             # XAML 改动必跑；含引导跳过+九页导航+设置窗口
# 真实进程复审门禁（需 managed venv；后台2+资金币种1+固定费1+外部资金1）：
$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS="1"
dotnet test tests\Caishenfolio.Host.Tests --no-build --no-restore --filter 'FullyQualifiedName~BackgroundExtendedCheckIntegrationTests|FullyQualifiedName~EvidenceCardFundingCurrencyChainTests|FullyQualifiedName~FxPathCostChainTests' # 5/5
dotnet build packaging\windows\Omnix.Installer.wixproj -c Release           # 出 MSI
```

后台检查手工验证：`Caishenfolio.Desktop.exe --notify` 应无窗口退出（code 0），
并在 `%LOCALAPPDATA%\Caishenfolio\logs\notify.log` 追加一行（新格式含「已检查：…」
与冷却/失败信息）；设置窗口底部「最近一次后台检查」应显示该行的内容。
开启「后台扩展检查」后再跑一次，日志应显示「已检查：打新时限、价格与集中度」；
拔网线跑则应显示「扩展检查失败：…打新时限提醒不受影响」。

## 未做（N 系列后续）

- **真人验收**：§6 七项任务 + §12 十一步脚本需真实新手无口头指导执行（自动化部分已落地为 NoviceEndToEndAcceptanceTests）
- N6 细化：教育路径已有三层骨架与内容，可继续按用户反馈扩充；N7 无障碍专项（键盘/对比度/读屏）未做
- 旧研究页 MVVM 化、通知多渠道 UI、净值曲线图表（历史遗留，非 Gate 项）

- **Gate B/C 集中审查**（计划 §15：N4~N5 后审查数据追踪、样本外、过拟合、组合前后对比）
- N5 第二批：回撤归因（需逐日持仓快照，当前 `valuation_snapshots` 只有总额）、利率情景、
  行业集中度/相关性/ETF 重叠（依赖数据质量，计划§10 后续能力）；相关 UI 深化
- N6 场景化教育（现有 TermGlossary/引导/就地解释为部分覆盖）、N7 全面测试与发布加固
- N1 验收的"新手真人测试"（计划 §6 七项任务需真人无指导完成，自动化只覆盖机械导航）
- ~~N2 补充：跨账户换汇、换汇 CSV 导入~~ **已完成**（统筹 2026-10-19 独立核实：`LedgerTransaction.CounterAccountId` 含同账户校验；`TransactionCsvImporter` 认 换汇/结汇/购汇/fx）
- N4 补充：样本外/参数敏感性回测进卡（当前历史检验为条件分布，滚动样本外待接
  既有 out_of_sample_report）；卖出方向证据卡（计划质量门"卖出分析必须列税费"）
- 日历：农历/浮动节假日未建模（固定节假日已入表，长假可能多报陈旧度，方向安全）
- 旧研究页（1225 行 code-behind）未 MVVM 化；净值曲线无图表
- 通知设置界面目前只支持一个 webhook 渠道（数据模型已支持多个）

- GitHub: https://github.com/plnoble/OMNIX-Caishenfolio
