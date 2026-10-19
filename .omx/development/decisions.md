# Decisions

## 2026-10-07 - 隔离数据源用命令行钉住

- Choice: 隔离模式把 `fixture` 加到核心命令行 `--provider`，同时保留环境变量。
- Rationale: 实测桌面进程查询到的环境是 fixture，Python 实际读到的是 auto；命令行不依赖这次环境块传递。
- Consequence: 正式模式不带该参数，生产凭据优先级不变。非 fixture 的强制值会被拒绝。

## 2026-10-07 - 本批交付收尾范围

- Choice: 执行任务书只覆盖第五轮整改复核、UI/真人验收、文档校准、Git 与发布候选准备；CashTransfer 等新能力继续保留后续 backlog。
- Rationale: 用户要求完成状态评估指出的收尾事项，已有功能及未提交成果优先验证和固化，不将其扩大为新一轮功能开发。
- Consequence: `agent-delivery-plan-2026-10-07.md` 明确工作包、实际证据与人验边界；开发自测不得代签独立复审或自动发布。

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

## 2026-09-04 - N0.1 冲正的实现方式：重放时跳过整对，而非反向记账

- Choice: 冲正行是原行的完整镜像（`with` 复制经济字段），`PositionCalculator.Replay` 直接跳过「原 + 冲正」两行。
- Rejected: 把冲正作为一条真实的反向交易入账（卖出冲正=再买入）。后者会污染已实现收益、
  费税统计和 XIRR 外部流——修正错误不应产生新的"交易"；也会在重算平均成本时引入除法残差。
- Rationale: 冲正的语义是"这对事实从未发生"，不是"发生了一笔相反的交易"。跳过整对让
  现金/成本/收益精确回到入账前（decimal 无残差），审计链只剩两行原始记录 + 原因备注。
- Consequence: 重放需先扫一遍收集冲正 id 集合（O(n)，可接受）；外部流同步恢复，
  XIRR 不会把错误入账期间的现金流计入。
- Choice: 冲正唯一性由 `PortfolioStore.ValidateReversals` 在写入事务内校验（目标存在、
  未被冲正、目标非冲正行），不依赖 UI 自觉。
- Rationale: 计划 §5 要求"不放宽账本约束"；入口校验让 CSV 导入、未来脚本、IPC 调用
  都无法构造出双重冲正或悬空引用。
- Choice: schema v6 迁移前自动把库文件复制为 `.bak`。
- Rationale: 计划明确要求"回滚前备份"。SQLite `ALTER TABLE ADD COLUMN` 虽是安全操作，
  但用户账本是不可再生的数据，备份成本几乎为零。

## 2026-09-04 - N0.2 覆盖比例按成本口径而非已定价市值占比

- Choice: `PricedFractionByCost = 已定价合计 / (已定价合计 + 未定价持仓成本)`；成本本身因缺汇率
  不可折算的持仓按 0 计入。
- Rejected: 已定价市值 / 总值——总值本身就是已定价合计，该式恒等于 100%，毫无信息量。
- Rationale: 缺失的正是市值，任何"缺失部分占市值比例"都需要一个市值假设。成本是账本里
  唯一确定知道的口径，明示"按成本口径约 x%"不会伪装成精确值。
- Consequence: 该比例只在摘要里作粗略提示（"约 62%"），不参与任何规则判定；
  规则判定只看 CoverageState 的三档。

## 2026-09-04 - N0.2 部分定价时抑制目标配置偏离

- Choice: `CoverageState.Partial` 时 `Drift` 返回空，摘要说明"总额不完整，本次未计算"。
- Rationale: 偏离金额 = (目标-当前)×总值；总值不完整时每一条偏离金额都在伪造精度，
  与"未定价不得按 0 计入"同一原则。集中度是比例口径、分母同为部分值，仍输出但强制
  冠以"仅基于已定价部分"。

## 2026-09-05 - N1 免内部代码：UI 层解析，领域层继续强制规范形式

- Choice: `LedgerView` 在记账路径上用 `SymbolResolver` 把用户输入（裸代码/名称/ticker）
  翻译成 `EXCHANGE:CODE`；领域层 `LedgerTransaction.New` 保持强制 `SymbolId.TryParse` 不变。
- Rejected: 放宽领域层接受任意字符串（会把"什么算合法标的"的权威从交易所注册表
  下放到每个调用方的自觉）；在 SymbolResolver 里静默选第一个候选（歧义时猜错
  = 钱记到错误的标的上，属 P0 级错误）。
- Rationale: 解析是"翻译人话"，属 UI 职责；校验是"守住账本"，属领域职责。分层后
  CSV 导入、未来 IPC 入口依旧被领域层硬约束兜底。
- Consequence: 歧义输入在 UI 层拒绝记账并给出可点击的候选；未识别输入同样拒绝
  （fail-closed），提示改用代码或名称重试。

## 2026-09-05 - N1 首页待办逻辑做成纯函数而非 ViewModel 状态

- Choice: `NoviceTaskList.Derive(...)` 接收 7 个标量（是否引导过、账户数、持仓数、
  未定价数、提醒数、完整性、全未知），返回不可变任务卡列表；视图只做渲染。
- Rejected: 把推导写进 code-behind（不可单测，桌面层此前零测试）；为首页单独引入
  MVVM 框架级 VM（现有 Wealth 页都是共享 `PortfolioViewModel` + code-behind 模式，
  单独翻新风格会制造两套并存的 UI 范式）。
- Rationale: 计划 §6 要求"新视图不把逻辑堆入 MainWindow"；推导逻辑是产品语义
  （缺价格必须是红色紧急、空账本必须有第一步），值得用 8 个测试钉死。
- Consequence: 新建 `Caishenfolio.Desktop.Tests` 测试项目（net8.0-windows + WPF），
  桌面层纯逻辑从此可测，为后续 N1 剩余项（拼音检索 VM 等）铺路。

## 2026-09-05 - N1 小白模式隐藏研究导航而非删除功能

- Choice: 小白模式下左栏隐藏「研究」组（行情/计划/网格/回测/对比），但首页"模拟入口"
  卡片可达同一批页面；专业模式完整恢复；选择持久化到 `ui.mode`。
- Rationale: 计划要求小白首页只回答四个问题、菜单做减法；同时专业模式"不删除老用户
  能力"。徽标（研究模拟/历史回测）在进入页面前就标明性质，比藏在角落的免责声明有效。
- Rejected: 删除研究页入口（违反"不删除老用户能力"）；小白模式也显示全部导航
  （回到"菜单多、术语多"的老问题）。

## 2026-09-05 - N1 首次引导：跳过也算完成，字段全可留空

- Choice: `OnboardingCompleted` 在「开始使用」和「跳过」两种路径下都置位；六问全部可空，
  不填就保持默认（CNY 本位币等）。
- Rationale: 设置字段文档语义即"绝不二次打扰"；强迫填写与"降低门槛"的目标相反。
  档案卡常驻首页并随时可重开，跳过没有损失。
- Consequence: 冒烟脚本必须学会跳过引导（否则首次运行卡在模态框）——脚本新增
  `Invoke-DismissOnboarding`，顺带验证引导窗口 BAML 真的能加载。

## 2026-09-05 - N2.1 换汇向导先做单账户，不动领域层

- Choice: 换汇向导在「单账户多币种」内入账（domain `FxExchange` 的既有语义：
  CashAmount+Fee 从账户 fromCurrency 扣、CounterAmount 加到同账户 toCurrency）；
  计划里"换出账户/换入账户"分列两个账户的能力留待后续批次。
- Rejected: 为跨账户换汇扩展 `LedgerTransaction`（CounterAccountId 列 + schema v7 +
  Replay 改动）——`PortfolioStore` 是热路径（计划 §13 Agent A 独占），为一个次要场景
  动账本内核，风险大于收益；且最常见的换汇场景本就发生在同一机构账户内。
- Rationale: N2 验收样例（CNY→JPY 回单 + 路径比较）单账户完全覆盖；
  "小步可验证"优先。跨账户需求真实出现时再做领域扩展。

## 2026-09-05 - N2.2 路径比较的数学放 Python，事实从 C# 账本来

- Choice: 比较/公式/盈亏平衡的计算在 Python `research/fx_paths.py`（计划 §13 Agent B
  的所有权区）；已持有仓位、历史成本、现金余额从 C# 账本读出后作为请求参数传入。
- Rationale: 报价来自行情源（Python 侧能力），历史成本来自账本（C# 侧权威）——
  各自持有事实的一侧负责取数，分析归 Python，符合 Host/Python 的架构分工。
- Consequence: C# `FxExchangeHistory` 只做"读账本"（含冲正对排除），不含任何比较逻辑；
  Python 侧不知道账本存在，接口用显式参数保持两侧解耦。

## 2026-09-05 - N2 成本语义：未知 ≠ 免费，缺报价 = 整体 fail-closed

- Choice: `PathCost` 全字段可空，空 = **未知**（路径标 gross_only，净额为 None），
  要表达"免费"须显式 `PathCost.zero()`；任一**必需**报价取不到时整个比较
  返回 ok=false（HTTP 422），不做部分估算。
- Rejected: 成本留空按 0 算（会把"不知道"伪装成"免费"，净额比较变成假精度）；
  缺一个报价仍输出其余路径（界面会显得"已经算出来了"，用户未必注意到缺的那条）。
- Rationale: 计划 §7 明文要求「数据不足」「成本输入缺失」状态与 fail-closed；
  换汇决策里把未知当已知的代价是真金白银。

## 2026-09-05 - N2 百分数字段命名约定：以 _pct 结尾即百分数单位

- Choice: 载荷里 `held_advantage_gross_pct` 等以 `_pct` 结尾的字段直接存百分数值
  （3.011 表示 +3.011%），与 fx_carry 的 `carry`（分数 0.015）约定不同但自洽。
- Rationale: 首版路由测试乘了两次 100 暴露出歧义——与其让每个调用方猜单位，
  确立一条全仓规则：**字段名以 _pct 结尾 = 百分数单位，不带 _pct = 分数**。
- Consequence: C# 侧格式化直接拼 "%"，不做单位换算；新增字段必须遵守此命名。

## 2026-09-05 - N3 去重状态：送达与观察分离，冷却只从真实送达起算

- Choice: `AlertNotificationState` 每条记录两个字段族——`WasInRange/LastSeenAt`（每次
  观察都更新，驱动重入检测）与 `LastNotifiedAt`（只在任一渠道真实送达后推进）。
- Rejected: 观察即推进 lastNotified——投递失败的提醒会被自己的冷却吞掉，下次运行
  静默跳过，等于一次网络抖动吃掉一个打新缴款提醒（本项目最不能接受的失败模式）。
- Rationale: 重入是"世界的事实"（条件消失又出现），送达是"动作的事实"（消息真的
  出去了）；两种事实的生命周期不同，必须分开记账。
- Consequence: 全渠道失败的运行不推进冷却，下个周期自动重试；观察侧永远最新。

## 2026-09-05 - N3 事件身份 = kind+symbol+title，不含消息文本

- Choice: 去重键用 `Kind|Symbol|Title` 三元组；消息体（含实时价格）不入键。
- Rationale: 计划买入提醒的消息每天都带不同现价，入键会让每次运行都是"新事件"，
  去重永远不生效。改一个计划价位仍归同一事件——条件重置由重入规则覆盖
  （价位失效会先退出区间）。
- Consequence: 同标的多档价位提醒共享一个键（当前 UI 一次也只画一档，可接受）；
  若未来多档并存，键需扩展目标价位。

## 2026-09-05 - N3 层 2 复用完整 Workspace 而非单独拉规则

- Choice: 后台扩展检查直接构造 `PortfolioWorkspace(store){PricingSource=core}` 跑
  `RefreshAsync`，取 `snapshot.Alerts`——不重新组装求值器调用。
- Rationale: "前台与后台复用同一套规则和阈值"（计划 §8）不是靠约定遵守的，是靠
  **同一条代码路径**保证的：阈值、交叉核对、容差从同一 settings 表读，Workspace
  构造时已接线；单独拼装必然漂移。
- Consequence: 层 2 也会重算打新提醒（求值器内含）——HeadlessNotifier 按事件身份
  合并两层结果，同事实不重复出现。

## 2026-09-05 - N3 后台临时启动的核心"只停自己启动的"

- Choice: `BackgroundExtendedCheck` 先探活 8765；有活核心（应用开着）直接复用；
  没有才临时启动，结束只 Dispose 自己的 broker。
- Rejected: 无条件 kill 8765 上的进程——那可能是用户开着的正式核心。
- Rationale: 复用已有核心最便宜也最安全；临时核心的生命周期必须由启动方负责，
  否则计划任务每次运行都会在用户机器上留一个 Python 进程。
- Consequence: 应用开着的 9:00 前后触发任务时，后台与前台共用一个核心、
  并发写估值快照（SQLite 文件锁兜底，最坏是一次可重试的失败，层 1 不受影响）。

## 2026-09-05 - N2.4 优势规则复用 /research/fx-paths，不在 C# 重写公式

- Choice: 优势阈值规则的求值调 `PostFxPathsAsync` 读 `held_advantage_gross_pct`，
  与汇率页路径比较面板走同一个端点、同一份 Python 数学。
- Rejected: 在 C# 里重写 E×C/J-1 三行公式——公式简单，但两处实现必然在费用口径、
  舍入、缺报价语义上漂移；后台提醒和前台面板对同一事实说出两个数字是最坏结果。
- Rationale: 优势是比值（比较金额约掉），100 元的比较与全部持仓的比较给出同一
  百分比；端点已按固定样例（3.011%）钉死。
- Consequence: 后台多两次 HTTP 调用（可接受）；规则缺持仓成本时端点正常返回，
  求值器把它转成"未评估"note。

## 2026-09-05 - N2.4 区间规则提醒=事实陈述，措辞由测试锁死

- Choice: 规则触发的文案固定为"汇率/优势到达**你设定的**区间/阈值"+ 否定声明
  （"不代表现在是换汇的好时机"），并有专门测试断言「最佳换汇时机/建议换汇/该换了」
  永不出现。
- Rationale: 计划 §7 N2.4 明令禁止"最佳换汇时机已到"式文案；措辞规则只写在文档里
  会被后续迭代无声破坏，写成断言才是活的约束。
- Consequence: 新增规则类型时其文案必须通过同一个措辞测试（惯例见 handoff）。

## 2026-09-05 - N1 拼音检索：依赖可用则用，离线种子内置，逻辑注入可测

- Choice: pypinyin 入 market extras（真实安装必有）；fixture 模式下种子表带人工
  核对的拼音；匹配逻辑接受可注入转换器，纯逻辑离线全测。
- Rejected: 在仓库手写常用汉字拼音表（几千条、无法核对正确性=伪造覆盖）；
  把拼音做成硬依赖（fixture/CI 离线环境直接崩）。
- Rationale: "搜索支持拼音"（计划 §6）要真实交付，但离线确定性是测试基线；
  诚实优先：装了 pypinyin 的环境全量拼音，没装的环境明确只支持种子表，不假装。

## 2026-09-05 - N1 TWR：快照点已含当日流，同日流必须剔除

- Choice: `TryTwr` 从 flows 中剔除「日期 == 某个估值点日期」的外部流（首日及之前
  的也剔除）——快照是全量重放，点值已含当日入金，再计一次就是双算。
- Rationale: 期初现金建账当天：点值 100k 已含 100k 流，TimeWeighted 的约定
  （"流在估值点之后到达"）与我们的快照语义相反；首版即把平账算成 -50%。
- Consequence: 只有两个及以上估值点才有 TWR（首刷显示 —）；两个点之间的
  中期入金正常计入（它们不在任何点值里）。

## 2026-09-05 - N2 差值比较口径是集合属性：全体净额或全体毛额

- Choice: 逐对差值与 best_path 的计算先判「是否全部可比路径都有净额」，是则全
  用净额，否则全用毛额；每条路径的 diff 带 basis 标签。
- Rejected: 按路径各自"净额优先、毛额兜底"——缺净额的路径（没扣成本）会在 max()
  里白赚成本差，最优路径可能指向一条未计费的路径（首版实锤）。
- Rationale: 跨实体比较的可比性属于整组；"兜底"只能整组统一降级，逐实体兜底
  等于给缺数据的一方让利。

## 2026-09-05 - N3 日历建模范围：固定节假日入表，农历/浮动明示不建模

- Choice: MarketCalendar 只收录每年固定日期的休市日（元旦、CN 劳动节/国庆法定段、
  US 感恩节/圣诞/独立日、JP 新年段、HK 圣诞段）；农历/浮动节假日（春节、清明、
  复活节、日本替换休）**不建模**，文案用"约 N 个交易日"明示近似。
- Rejected: 手写三年全量节假日表——春节/清明/复活节日期依赖历法与官方公告，
  离线无法核实；写错就是把错误事实钉进代码。
- Rationale: 计划要求"节假日语义"，诚实做法是把确定知道的建进模型、不确定的
  明说不建——比一张可能错的完整表更可信。长假窗口可能多报陈旧度，方向安全
  （多提醒不会让人用旧价决策）。
- Consequence: 每年新增法定调休变化需手工维护表（2025–2027 已覆盖）。

## 2026-09-05 - N4 结论顺序即策略：风险超限 > 证据不足 > 冲突 > 倾向

- Choice: `conclude()` 的判定顺序固定为 risk_exceeded → insufficient → conflicting →
  positive/negative——用户自设的单一持仓上限能一票否决整张卡；关键数据缺失时
  卡片停在"证据不足"而不是继续给倾向。
- Rationale: 计划 §9 验收标准明文要求"数据不足时可以明确停止"。倾向性结论是
  最容易越权的输出，让它排在最后、且只在正反证据明确不对称时出现，
  是对"不替用户下买卖结论"最结构化的落实。
- Consequence: 一张证据齐全但触发上限的卡显示"风险超限"而非证据状态——
  这是特性：上限是用户自己填的主观边界，理应压过一切客观证据。

## 2026-09-05 - N4 中性事实不进证据计数

- Choice: 趋势、波动率、回撤、条件分布等"两头都切的数字"只进 sections 展示，
  不进 evidence_for/against 计数；只有带明确方向的事实（连续增长、多源价差）
  才算证据。
- Rationale: 60 日均价之上既可以说是"动量"也可以说是"贵了"，把它算进任何一方
  都是替用户解读；一张只含中性事实的卡必须得出"证据不足"，而不是假装有方向。
- Consequence: fixture 下的完整卡只有 1 条正面证据（交叉核对通过）——少而真，
  好过把每个指标都凑成"证据"。

## 2026-09-05 - N4 禁语按子串扫描，否定句里的禁语同样违规

- Choice: `BANNED_PHRASES` 对卡片全部字符串做**子串**扫描，不区分句子极性；
  否定式免责（"这不等于该买""不构成买入依据"）同样算违规，需改写成不含禁语
  子串的表述（"不是操作依据""不是收益预测"）。
- Rationale: 首版两处违规都是"好心免责"里夹带禁语——句子分类器会放过它们，
  子串扫描不会。提醒渠道、截图、转述都会剥离上下文，禁语单独出现就是违规。
- Consequence: 写否定免责时的固定句式：描述证据构成 + "不是操作依据/不是收益预测"。

## 2026-09-05 - N5 贡献拆分：承认"总览浮动盈亏=涨跌部分"这一既有事实而非重定义它

- Choice: 三数模型——总览浮动盈亏（成本按今日汇率折算，构造上=涨跌部分）、
  汇率部分（成本×(r_now−r_cost)）、相对实际投入（两者之和）。
- Rejected: 改估值引擎让成本按成本日汇率折算。那会动到总览 KPI、盈亏曲线、
  快照存储等一整条链，影响面远超 N5 范围，且"成本按今日汇率"本身也是一种
  自洽口径（分析师常用）。
- Rationale: 计划要的是"解释汇率变动与资产涨跌各自的贡献"，不是改成本口径。
  在贡献页把两个口径的关系讲清楚，总览保持不动，风险最小、信息增量为正。
- Consequence: 贡献页三列并存，文案明确"相对实际投入"与总览数字相差的正是汇率部分；
  成本日汇率缺失时后两列=未知（fail-closed）。

## 2026-09-05 - N5 压力分析：债券/可转债/场外基金不套用股票跌幅

- Choice: 股票跌幅只作用于 equity/etf；债券、可转债、场外基金保持原值，假设清单里写明。
- Rejected: 给非股票类猜一个 beta（如 0.3）一并冲击。
- Rationale: 债券对利率/信用非线性，场外基金底层未知（股票基金/债券基金/货币基金
  无法从账本区分）。编造弹性=把假设分析伪装成精确计算，违反产品底线。
- Consequence: 情景结果对重债组合偏乐观——但假设清单逐条可见，用户知道自己填了什么。
  后续若引入久期数据（计划 N5 后续能力）再补利率情景。

## 2026-09-05 - 回撤归因的端点语义：缺失即零、现金取残差、未定价不给份额

- Choice: 窗口端点缺失的持仓按 0 计入该端点（开/平仓），现金行取「总额变动 − Σ持仓变动」
  的残差；端点未定价的持仓不给贡献份额（只给警告）。
- Rationale: 总额曲线与持仓快照同一事务写入，端点日期必有快照；持仓缺失=期间开/平仓，
  对应资金走现金，残差行吸收后账目闭合。未定价端点若按 0 计入会伪造一次暴跌——份额
  也一并拒绝，这是"不给可误读的部分数字"原则。
- Consequence: 窗口内大额出入金会让"现金（含出入金）"行异常放大，文案已注明；
  快照功能上线前的历史只有总额，无法归因——UI 明说而不是假装算出来。

## 2026-09-05 - 冲正对与 CSV：导出打标记、导入拒绝，而不是静默跳过

- Choice: 导出器给冲正镜像行打「冲正·」类型前缀；导入器遇「冲正」前缀直接报错，
  已冲正原行按内容哈希识别并报错。两者都不允许跳过即通过。
- Rejected: 导出时干脆省略冲正对（账本导出应完整，省略=审计链断在 CSV 里）。
- Rationale: 镜像行单独导入会复活已取消的事实（重放无配对信息）；原行被冲正后再导入
  同样复活。静默跳过则用户不知道导出的账本没有被完整还原——报错并解释处理方式才是
  fail-closed。

## 2026-09-05 - 审核修复的三个口径决策

- Choice: TWR 快照约定=流后（当日全部流水重放后估值），当日流从该段增益剥离而非计入成本基。
- Rationale: 快照本来就是重放整日流水后存的，这是既成事实而非选择；文档化后，三个旧语义
  （首点当日流/区间流/末点当日流）都有确定归宿。端点当日流已含在点值内，必须从增益剥离，
  否则入金=升值（审核反例 +50%）。
- Consequence: 估值点恰逢流日的"入金后涨 vs 涨后入金"在单段内不可区分——约定选了前者，
  测试把这个边界写死。

- Choice: 裸代码（字母 ticker/4 位数字）市场歧义时拒绝解析并列出全部解读；账本标的表
  唯一命中（含代码部分匹配）视为合法消歧。
- Rationale: 市场决定币种/地区/时区/行情源，猜错一个会污染下游每个数字。用户自己的账本
  已经交易过这个标的，是比"猜"强得多的证据。
- Consequence: IBM 首次输入必须写 NYSE:IBM 或先在自选搜索；小白多打几个字，换取不猜错。

- Choice: 净到账提醒的费用模型存进规则本身（SpreadPct/FixedFee），而不是全局或按渠道。
- Rationale: 不同规则盯不同路径（银行 vs 券商费率不同）；审核要求"定义并持久化明确的
  费用模型"，规则级是最小持久化单位；无模型→拒绝评估，绝不默认零费用。
- Consequence: 设置 UI 净到账类型多两个必填项（至少其一）；后台链路按规则携带费用调用。

## 2026-09-08 - UI 补充验收边界

- Choice: 第四轮 R3-01/R3-02 维持关闭；第五轮单独登记测试环境/覆盖/诊断问题，不直接改生产 provider 优先级。
- Rationale: 当前证据是桌面冒烟无法隔离 fixture 和未选持有币种，未证实资金/日期计算回归，也未获得 422 响应体。
- Consequence: 下一开发包先做 Host 约束的测试隔离及路径断言，再重新验收；不通过覆写用户配置获得“全绿”。

## 2026-10-07 - 第七轮复审的关闭与重验边界

- Choice: 关闭已证实修复的原 P1 核心/标题契约/复制负例问题，仅将剩余输入、异步状态、路径子项列为 3 P2；本轮不扩大功能或代为整改。
- Rationale: 基础回归全绿而上轮明确的边界反例仍成立；不能因为重跑通过就漏掉，也不应把受控目录中的相对路径误报为新的正式账本污染。
- Consequence: 先完成 R7-01～03 再冻结候选，后续两次冒烟及显示/人验只签认最终代码；本轮未显示控件探针不代替 UI E2E。

## 2026-10-07 - 第八轮保持单项收尾范围

- Choice: 关闭 R7-01/R7-03 功能缺陷；保持 R7-02 一个 P2，集中处理响应归属和完整说明，不另开新功能开发包。
- Rationale: 原样反例显示两项修复有效，顺序失败也正确；仅移动标题更新时机没有处理乱序响应，正式桌面回归仍缺。
- Consequence: 先补剩余修复与长期测试再冻结；不停止非本轮拥有的 8765 核心来强行跑 UI 冒烟，待隔离前置条件满足后执行。

## 2026-10-07 - 从逐问题复审切换为阶段式交付

- Choice: 统筹提供一个 FA1 收尾任务包，执行 Agent 完成长周期测试、局部整改与证据整理，统筹按固定候选集中终审并直接处理少量遗留。
- Rationale: 代码实现已声称补齐，继续每次只交“已修未测”会重复读取和交接；长期回归与候选证据缺口应由执行阶段一次补齐。
- Consequence: 不为形式强行在大量未提交成果上创建旧基线 worktree；先保持唯一执行者，迁移/提交另循授权。真人/环境受阻不伪装通过，也不阻断无冲突可执行项。

## 2026-10-07 - FA1 终审直接修测试隔离，不改产品资源架构

- Choice: 普通 Application 加载编译时嵌入 App.xaml 的资源段；不创建生产 App，不为测试抽改产品 XAML；新增长期隔离回归。
- Rationale: Dispatcher 会执行 Application 构造时排队的启动流程，原宿主进入正式路径；单测通过不等于无副作用。资源来源保持单一，同时避免扩大产品变更和重新打包范围。
- Consequence: 原候选 + review-1 记录三项测试侧指纹，产品/UI/MSI 不变。关闭 R7，但保留启动/证据可靠性与人验门槛，不把未知原因改写为环境问题。

## 2026-10-08 - 以确定性等待与逐次证据收口启动问题

- Choice: 修复验收脚本的引导等待/窗口归属与报告覆盖，增加共用函数负例；不改产品启动架构，不凭旧丢失日志猜测 Python 故障。
- Rationale: 主窗口标题出现不等于引导已出现；未关闭的引导会挡住核心启动。每次独立证据才能区分失败与成功来源。
- Consequence: 明确版本变化后固定做五次冷启动和两次完整 UI，全部通过后关闭本批工程项；历史逐次根因不可恢复和人工验收边界继续如实保留。

## 2026-10-08 - TR1 分包推进，不停在修复汇报

- Choice: 用户明确授权后直接调度三个有限工作包，围绕现有产品试用准备，不扩张新金融能力；共享工作区以文件所有权隔离，统筹串行运行有共享资源的测试。
- Rationale: 当前成果未提交，从旧 HEAD 开 worktree 会丢失实际基线；让用户再次转发小修既重复成本又没有推进阶段。尚待人验不代表自动化/安全准备也必须停工。
- Consequence: 代码可并行，.NET 构建、桌面/核心启动必须串行；本轮改动需新验证，历史绿灯不得迁移为 TR1 通过。

## 2026-10-19 - FX 优先与 Android 迁移的审查建议（尚未实施）

- Choice: 保留不可变交易事实及 PC 普通资产/研究能力；FX 增加 CurrencyLot、LotAllocation、LotLineage、独立目标与情景；Android 倾向 Kotlin/Compose/Room，计算在本机纯领域模块。
- Rationale: 当前资金池没有 Lot 身份，五路径是历史成本比较而非未来情景；只移植 UI 或把 PC 当必需服务器均不满足用户目标。C# 与 Python 的正确公式/测试可移植，Windows 壳不应拖入移动运行时。
- Consequence: 普通证券仍可用移动加权法；FX 使用明确批次分配。统计本位币、成本币、目标币、quote base 分离。未知成本不阻断市场估值；历史原始投入不能在每一段重算时重复确认收益。
- Delivery boundary: 本轮仅审查；新的 schema、Kotlin 工程、行情适配、迁移工具尚未创建。实施建议及验收条件见审查报告第 7–10 节。
