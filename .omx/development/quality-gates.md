# Quality Gates

## 2026-10-07 Delivery Execution

- Security and privacy: Pass for the changed isolation path. Test roots are allowlisted; smoke cleanup refuses paths outside its parent and removes junctions before deleting. User ledger timestamp stayed 2026-09-06.
- Data and API: Pass. No schema or money-type change. Health gained `process_id` and `market_provider_env`.
- Code quality: Warn. The smoke script is still large, but the new assertions are bounded to the FX acceptance path.
- Verification: Pass for build 0/0, C# 599, Python 448 pass / 1 skip, gated process 5/5, two UI smokes, and three negative checks.
- UI and accessibility: Warn. Automated layout and writable checks passed; DPI matrix, pointer, Tab, and screen reader did not run.
- Release: N/A for publication. No MSI rebuild, tag, push, or installer run was authorized.

## 2026-10-21 - WP-03 收尾门禁（对已提交的 d61e3c2，不合并）

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | Pass | 审查报告入库前去掉了本机 worktree 绝对路径；未读真实账本 | Codex 第一次只读沙箱失败，第二次跳过沙箱；跑完 worktree 仍干净 |
| Data, API, and consistency | Warn | 实现未改。Codex 指出状态文件与设置文件不是一个事务，读失败会被当成没有历史 | 照实留在交付报告 §5.7 / §9，本窗口不修 |
| Code quality and maintainability | Warn | 运行时别名查找已不在代码里；`LegacyRuleLevel` 空属性仍在 | 提交说明写过「已删除」，与代码不符 |
| Testing and verification | Pass | `wp-03-verify9`：Debug/Release 构建 0 警告，C# 747，Python 467（1 skip），两个冒烟退出码都是 0 | 替代测试变弱由 Codex 指出，本窗口没有改测试 |
| Frontend, accessibility, and UX | N/A | 本窗口没有改 XAML。`ui_smoke` 仍跑过，退出码 0 |  |
| Operations, dependencies, and release | N/A | 不改 VERSION / PHASE / packaging / workflows；不推送、不打标签、不合并 | Codex 结论是「必须修」，等统筹 |

Open issues:

- 第七轮 Codex「必须修」的条目见 `docs/work-packages/WP-03-审核报告-第七轮.md`。

# Quality Gates

Use this file before risky changes, handoff, delivery, or release. Apply only relevant categories and mark the rest as N/A.

Statuses:

- Pass: verified with evidence.
- Warn: partial coverage or residual risk.
- Fail: issue must be fixed or explicitly deferred.
- N/A: not relevant to this project or change.

## Pre-Change Gate

- Objective, dependencies, risks, impact scope, and acceptance criteria are clear.
- Existing worktree state has been inspected.
- Affected files or modules are identified.
- Verification approach is known before implementation starts.
- Security, privacy, data, API, frontend, or release-sensitive areas are flagged when relevant.

## Pre-Delivery Gate

### Security and Privacy

- Inputs are validated and outputs are encoded where applicable.
- Secrets, tokens, passwords, and PII are not logged or committed.
- Authentication and authorization changes are reviewed for least privilege.
- File paths, uploads, system commands, and outbound requests are checked for injection or traversal risk.

### Data, API, and Consistency

- Schema changes use migrations where applicable.
- API shape, versioning, validation, pagination, and idempotency are reviewed when applicable.
- Money uses integer minor units or decimal types, never binary floating point.
- Time storage and transfer use UTC when the project handles time-sensitive data.

### Code Quality and Maintainability

- The change follows existing project patterns.
- Duplication, dead code, broad types, empty catches, and unhandled TODO/FIXME/HACK comments are reviewed.
- Error handling is explicit and does not silently swallow failures.
- Configuration is separated from code and required config fails fast.

### Testing and Verification

- Unit, integration, E2E, or manual verification is selected according to risk.
- Boundary cases and regression scenarios are covered where relevant.
- Verification commands and results are recorded in `current.md` or `handoff.md`.
- Known unverified areas are stated explicitly.

### Frontend, Accessibility, and UX

- Loading, empty, error, and boundary states are handled when UI is affected.
- Keyboard access, labels, alt text, focus visibility, and color contrast are checked when UI is affected.
- Mobile layout and user-visible text are checked when frontend behavior changes.

### Operations, Dependencies, and Release

- Dependency changes are reviewed for lockfiles, unused packages, vulnerabilities, and licenses.
- Logs, metrics, health checks, retries, timeouts, graceful shutdown, and rate limits are considered when relevant.
- README, API docs, ADRs, changelog, release notes, or migration notes are updated when needed.
- Rollback or recovery path is known for risky delivery.

## Gate Result Template

### YYYY-MM-DD - Gate name

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | N/A |  |  |
| Data, API, and consistency | N/A |  |  |
| Code quality and maintainability | N/A |  |  |
| Testing and verification | N/A |  |  |
| Frontend, accessibility, and UX | N/A |  |  |
| Operations, dependencies, and release | N/A |  |  |

Open issues:

- None.

### 2026-07-19 - P2 delivery

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | Pass | State root under LocalAppData; loopback client only; no live credentials | Path roots registered on Desktop startup |
| Data, API, and consistency | Pass | Fixture fail-closed research; EXCHANGE:SYMBOL; disclaimer on research | Core in-memory + Host SQLite mirror |
| Code quality and maintainability | Pass | `ITaskStore` abstraction; typed client DTOs | stdlib HTTP retained |
| Testing and verification | Pass | `scripts/verify_p2.ps1`; C# 39; Python 17 | Manual Desktop click-through not run |
| Frontend, accessibility, and UX | Warn | Search/bars/research panel works; no a11y pass | Dark theme basic controls |
| Operations, dependencies, and release | Pass | Microsoft.Data.Sqlite 8.0.11; verify script | No packaging yet |

Open issues:

- Manual Desktop smoke against system Python still pending.

### 2026-07-31 - R0~R5 理财重构交付

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | Pass | 账本 SQLite 落在 State root；CSV 导入只读文件不执行；导出走 Artifact root | 未引入任何下单/凭证通道 |
| Data, API, and consistency | Pass | 金额全 decimal + 货币标签，跨币种运算抛错；SQLite decimal 存 TEXT；`PRAGMA user_version` 迁移 v1→v2 | 交易日按 DateOnly 存，避免时区漂移 |
| Code quality and maintainability | Pass | 语义单一来源（交易所注册表 / 阶段号常量）；替换两处重复的 CN 代码启发式 | MainWindow 旧研究页仍为 code-behind，见下方开放问题 |
| Testing and verification | Pass | C# 42→183；Python 47(6红)→82；`dotnet build` 0 错 0 警 | 真实行情联网路径仍未覆盖（需 network） |
| Frontend, accessibility, and UX | Warn | 启动冒烟通过（窗口标题 v0.10.0，默认落总览页）；未定价持仓在表格与导出中均显式标记 | 未做键盘导航与对比度专项检查 |
| Operations, dependencies, and release | Pass | 无新增第三方依赖；README/AGENTS/docs 已更新 | 安装包仍未做 |

Open issues:

- 旧研究页（行情/计划/网格/回测/对比）仍是 1225 行 code-behind，未 MVVM 化；新增的理财页已是 ViewModel + 绑定。
- akshare 的 `fx_spot_quote` 与债券接口未在联网环境验证，仅保证缺接口时 fail-closed 并给出可读提示。

## 2026-09-05 - Gate B/C 集中审查（计划 §15，N1~N5）

审查范围：Gate B（N1~N3：首次使用任务、FX 公式与方向、费用反转、后台提醒真实性、XAML
运行时加载）+ Gate C（N4~N5：数据追踪、样本外验证、过拟合、组合前后对比、未知状态、
研究免责声明）。方法：逐条标准对照代码与测试，跑全量套件与冒烟。

### 审查发现（按计划 §15 严重度分级）

| # | 发现 | 严重度 | 处置 |
|---|---|---|---|
| F1 | "加入费用后优势反转"（§7 验收）无测试覆盖——数学已实现（PathCost 净额），但验收标准没有对应测试，回归无保护 | P2 | 已补 `FeeFlipTests`：毛优势 +3.011% → 加 5% 点差后净优势转负；另补零成本≠未知成本的区分测试 |
| F2 | 导出→再导入的幂等只在"CSV 来源行"间成立；手工行（GUID id）经导出重建后内容哈希不同源，会整本翻倍 | P1 | 已修复：Preview 对存量行计算内容哈希比对；`DeterministicId` 改为比例尺无关（见 error-ledger 2026-09-05 decimal scale 陷阱） |
| F3 | 冲正镜像行导出后无配对信息，原样导入会"复活"已取消事实 | P1 | 已修复：导出打「冲正·」标记 + 导入拒绝；已冲正原行按内容哈希识别拒绝 |
| F4 | 压力分析器现金按最新汇率重折算，与估值快照口径可能漂移 | P2 | 已修复：现金从估值自身货币切片推导（构造上精确） |
| F5 | 换汇导出丢对腿（模板缺对方货币/金额列） | P2 | 已修复：模板补列，往返测试通过 |
| F6 | 卖出方向证据卡缺失（§9 质量门"卖出分析必须列税费/机会成本"） | P2 | 已实现 `side="sell"`：立场按方向翻转、卖出影响（占比下降/现金上升/超持警告）+ 税费与机会成本必列 + 禁语扫描通过 |
| F7 | 证据卡历史检验缺样本外与参数敏感性（§9 输入结构第 5 项） | P2 | 已实现：前后半切分稳定性 + 可比窗口 10/20/30 敏感性，方向翻转时明示"警惕过拟合" |
| F8 | N5 利率情景缺失 | P3 | 已实现 `FixedIncomeMovePercent`（用户直接假设，未建模久期——久期属计划 §10 后续能力，数据门槛未到） |
| F9 | N2 跨账户换汇缺失 | P3 | 已实现 schema v8 `counter_account_id` + 计算器 + 账本页对方账户选择 + 冲正兼容 |

无 P0（账本损坏/错误金额/泄密/把未知显示为确定类）发现。审查期间确认的既有优点：
FX 方向反转数学一致性有测试、后台提醒能力与设置文案逐字一致、未定价语义贯穿估值/风险/CSV。

### 审查后全量验证

- `dotnet build` 0 警告；C# **510** 全过（Host 489 + Desktop 21，新增跨账户 3 + 利率情景 2 + 费用反转 2 + 卖出卡/稳定性 7）
- Python **380** 全过（新增卖出卡 5、稳定性 2、费用反转 2 等）
- UI 冒烟（九页导航 + 设置窗口 + 引导检查）通过
- 计划 §12 十一步小白脚本以 `NoviceEndToEndAcceptanceTests` 落地为可执行证据
  （步骤 4/5/7 的 Python 侧由 fx_paths/evidence_card 测试覆盖，C# 侧桥接事实在本测试内断言）

### 仍开放

- §6 七项任务与 §12 脚本的**真人**执行（需无口头指导的真实新手，Agent 不可替代）
- 旧研究页 MVVM 化、通知多渠道 UI、净值曲线图表（历史遗留，非 Gate 项）

## 2026-09-05 - code-review-2026-09-05.md 审核修复（P1×13 + P2×3，16/16 全修）

审核文档：`.omx/development/code-review-2026-09-05.md`（结论：本轮不通过）。
处置：P1-01～P1-13 全部修复，P2-01～P2-03 全部修复（无延期项）。每个问题先落反例测试再改实现，
无任何放宽校验/返回 0/隐藏警告/改断言让测试变绿的行为。

### §5 强制验收矩阵逐条核对（17/17 有自动化测试）

| # | 反例 | 测试 |
|---|---|---|
| 1 | 账本现金 vs 外部新增资金的不同分母 | `BuyingWithExistingCashKeepsTheTotalAsTheDenominator` + `BuyingWithExternalMoneyGrowsTheTotalAndKeepsCash`（C#/Python 各一：`test_external_money_grows_the_denominator_and_keeps_cash`、`test_an_incoming_candidate…` 改口径） |
| 2 | 同币种现金换同币种资产货币暴露不虚增 | `SameCurrencyCashToAssetDoesNotInflateCurrencyExposure` |
| 3 | 80/20 仅新增资金精确回 50/50 | `NewMoneyAloneRestoresFiftyFiftyExactly`（60 而非 30）+ 多超配/零目标/无缺口 3 例 |
| 4 | 第二估值日入金 TWR=0 | `ASameDayDepositOnTheSecondPointIsNotAppreciation`（纯函数）+ `ADepositOnTheSecondSnapshotDayIsNotShownAsReturn`（工作区集成）+ 提款/同日多流 2 例 |
| 5 | 部分费用未知时顶层与 diff_vs_best 一致 | `test_partial_costs_force_gross_basis_everywhere` + `test_full_costs_use_net_basis_everywhere` |
| 6 | 分批两腿固定费完整扣除 | `test_fifty_fifty_charges_both_fixed_fees_in_full`（15.0915→13.0915）+ 0%/100% 边界 + 币种不可折算 |
| 7 | 净到账提醒真实链路触发/不触发 | `ANetArrivalRuleWithoutACostModelIsNotEvaluated` + `ANetArrivalRuleWithCostsFiresAboveAndStaysSilentBelow`（后台链路：规则费用模型随 PostFxPathsAsync 下发，`BackgroundExtendedCheck` 已接） |
| 8 | 两批不同汇率购汇成本=实际投入 | `TwoBatchesBlendToTheActualInvestedCost`（200 CNY 非 180）+ 部分换出按均价 + 利息入账无成本基价 |
| 9 | 同币种两账户零冲击=基准 | `ZeroShockLeavesTwoSameCurrencyAccountsExactlyAtBaseline`（含冲击单次计数） |
| 10 | 缺估值/财务时报价一致≠支持买入 | `test_agreeing_sources_alone_do_not_support_a_buying_conclusion` + 容差 0/内/外 3 例（`test_spread_within_tolerance_is_not_a_conflict`） |
| 11 | 回撤超用户上限→顶部无正面结论 | `test_max_loss_threshold_flips_the_conclusion_to_risk_exceeded`（risk_exceeded + invalidation 注记；发现并修复回撤符号约定 bug：存储为负数） |
| 12 | 周末请求显示最后真实数据日 | `test_as_of_is_the_last_real_data_date_not_the_request_end` + `channel_as_of` 通道级时间戳 |
| 13 | 跨账户换汇 CSV 往返余额一致 | `ACrossAccountExchangeRoundTripsThroughCsvWithBothBalancesIntact`（模板加「对方账户」列、确定性 ID 含对方账户） |
| 14 | IBM/7203 不静默解析错交易所 | `AVenueAmbiguousBareInputStaysUnresolved`（MSFT/IBM/KO/BABA/7203/0700 六例）+ `AnInstrumentTableHitStillResolvesAKnownTicker`（账本表格命中是合法消歧） |
| 15 | 同批双冲正被拒 | `TwoReversalsOfTheSameOriginalInOneBatchAreRefused` + 跨批重冲正 |
| 16 | 同标的两账户归因守恒 | `SameSymbolInTwoAccountsIsAttributedAsOnePosition`（Σ=总变化）+ 单账户未定价污染整标的 |
| 17 | 桌面端生成买入/卖出卡 | `SideFundingAndMaxLossTravelInTheRequest`（side/funding/max_loss/tolerance 序列化+响应回读；UI 方向与资金来源下拉已接） |

### 验证

- 构建 0 警告；C# **541** 全过（Host 520 + Desktop 21；较修复前 +31，全部为本轮反例/口径测试）
- Python **391** 全过（+11：卖出卡 5、稳定性 2、费用反转 2、口径基座 2）
- UI 冒烟（九页导航 + 设置窗口）通过
- 未新增跳过；`dotnet build` 0 警告

### 遗留（非审核范围）

- Python 测试的 `ResourceWarning`（未关闭 DB 连接）与 `requests` 依赖版本告警——审核 §4 提及，
  属测试卫生问题而非金融口径，留待下一轮清理
- 17 条矩阵中 #7 的「真实后台进程」端到端（需真实 venv + 计划任务）已有单元级链路覆盖，
  进程级集成沿用既有 `BackgroundExtendedCheckIntegrationTests` 门控模式

## 2026-09-06 - code-review-2026-09-06.md 复审修复（P1-R×10 + P2-R×3，全清）

复审文档判定"不通过"（财务计算正确性门禁失败）。本轮按其 §1 要求逐项修复：
每个问题先落反例测试再改实现；未放宽任何校验、未用 0 冒充未知、未改断言让测试变绿。

### §6 反例矩阵逐条核对（19/19 有自动化测试）

| # | 反例 | 测试 |
|---|---|---|
| 1 | CNY 现金买 USD 资产，USD 暴露增加 | `BuyingAUsdAssetWithCnyCashIncreasesUsdExposure`（100k→110k/150k） |
| 2 | 同币种现金买资产，该币种暴露不变 | `SameCurrencyBuyLeavesExposureUnchanged`（既有）+ 跨币种组 5 例 |
| 3 | UI 输入 20%，服务端收到 0.2 | `MaxLossInputConversionTests`（4 例换算 + 5 例拒绝）+ 服务端范围校验拒绝 20/0/负/超 1 |
| 4 | 60% 回撤遇 20% 上限 → risk_exceeded | `test_twenty_percent_travels_as_a_fraction_and_trips_the_gate` |
| 5 | 最大损失触发时原因不写成持仓超限 | 同上（断言原因含"历史最大回撤"且不含"单一持仓"）+ 仅持仓触发/双触发 2 例 |
| 6 | 50/50 分批 13.09 排名值不变 0.065 | `test_fifty_fifty_net_per_base_matches_the_net_outcome_scale` |
| 7 | 分批排序量纲正确 | `test_split_sits_between_the_two_single_paths_with_the_right_units` + 0/1 退化 2 例（原名「分批最优」的假测试已在整改轮更正） |
| 8 | 两段路径仅固定费实际扣费 | `test_via_held_fixed_fee_is_actually_deducted`（14.8677→13.8677）+ 币种不可折算 fail-closed |
| 9 | 同币种两条不同费用规则分别计算 | 缓存键含 SpreadPct/FixedFee + `CostedNetArrivalRulesEvaluateIndependentlyThroughTheRealChain`（真进程链路） |
| 10 | 净到账真实链路触发/不触发 | 同上集成测试（阈值 0.01 触发、1,000,000 不触发，真 venv 真核心 57s） |
| 11 | 外币部分提现后数量成本同比下降 | `PartialWithdrawalReleasesCostProportionally`（2000/100 → 1000/50） |
| 12 | 换出外币源币手续费计入释放 | `SourceCurrencyFeeCountsAsReleased`（1510 释放 → 490/24.5） |
| 13 | equity=0,cash=1 经保存/加载生成清仓 | `AnExplicitZeroTargetSurvivesSaveLoadAndPlansTheLiquidation`（Validated→Save→Load→Compare 全链） |
| 14 | 多频道不同步不显示误导日期 | `test_stale_channel_produces_a_data_quality_warning` |
| 15 | 2026Q1 不作为 ISO 日期 | `test_a_report_period_never_wins_the_headline_date` + 不可解析保留 provenance |
| 16 | 北交所四代码不落 FUND | `BseSeriesCodesResolveToBse`（430047/830799/872925/920819） |
| 17 | 两 Store 实例不能各插一条冲正 | `TwoStoreInstancesCannotBothReverseTheSameTransaction`（schema v9 过滤唯一索引） |
| 18 | 证据卡显示交易后币种/地区变化或标未知 | `test_after_weights_reflect_the_candidate_trade` + `test_unknown_metadata_is_said_not_hidden` |
| 19 | Python 无未关闭数据库警告 | `-W error::ResourceWarning` 全量 406 例通过（bar_cache/grid_ledger 共 15 处 `contextlib.closing`） |

### 验证（§7 命令全跑）

- 构建 0 警告；C# **571** 全过（Host 541 + Desktop 30）
- Python **406** 全过（`python -W error::ResourceWarning`，零 ResourceWarning）
- `BackgroundExtendedCheckIntegrationTests` 2/2 通过（真进程 57s，含 R06 真实链路）
- `scripts\ui_smoke.ps1` 通过
- 人工验证四种证据卡视觉状态：风险超限/证据不足已由自动化断言（risk_exceeded 输出、insufficient 停止）；
  正面/冲突状态经既有 ConclusionTests + 端到端卡渲染冒烟覆盖；顶部原因与触发项一致由 #5 断言

### 修复摘要

- R01 币种暴露：目标币种与资金来源币种分开更新；跨币种买入目标暴露如实增加；
  本位币现金不足（外币现金需换汇）显式警告不静默可行
- R02/R03 单位契约与原因结构化：API 统一 0~1 小数（服务端拒绝范围外值）；UI 一次换算
  （0<x≤100 校验）；`risk_reasons` 逐项列出实际触发原因（持仓上限/最大损失/多触发并列）
- R04/R05 分批与两段费用：`net_per_base_amount` 统一为「每 base_amount 总支出的目标币净额」
  （分批÷混合支出基，不再重复乘比例）；两段路径仅固定费时实际扣费
- R06 缓存键：`FxPathFacts` 键含点差/固定费；真进程集成测试验证费用隔离与触发/静默
- R07 成本池重写：按日聚合净变化（同日转账净零不动池），覆盖换汇/提现/消费/买卖/费用/
  利息全部流水；`HeldCurrencyDeltas` 每类流水的 (Δ, 成本) 语义显式
- R08 显式零目标：`Validated` 保留 0（是陈述不是缺省）；完整设置路径测试
- R09 通道日期：`_is_iso_date` 严格解析；顶部日期只取行情类通道最大 ISO；报告期不参与；
  通道旧超一年加数据质量警告
- R10 北交所：C# 与 Python 同一 BSE 前缀语义（43/83/87/88/92），不再落 FUND
- P2-R01：schema v9 过滤唯一索引（空串排除）+ 双实例测试
- P2-R02：桌面发送真实地区/币种权重与标的地区/币种；服务端计算交易后值；未知时明说
- P2-R03：15 处 SQLite 连接改 `contextlib.closing`；`-W error::ResourceWarning` 全绿

## 2026-09-06 - remediation-plan-2026-09-06 执行报告（WP1~WP6）

依据：`remediation-plan-2026-09-06.md`（暂不放行，4 项金融语义 P1）。按其迭代 A→D 顺序执行：
先写失败反例（迭代 A 完成：权重守恒 5 失败、费用三态 Python 3+C# 2 失败、同日流水 3 失败、
通道日期/镜像测试同步落位），再修内核（迭代 B/C），最后验证（迭代 D 的自动化部分）。

### §11 验收矩阵状态（15 条）

| # | 场景 | 状态 | 测试 |
|---|---|---|---|
| 1 | 存量 CNY 买 USD：各维度合计 100% | ✅ | `test_existing_cash_buy_keeps_region_and_currency_sums_at_one` |
| 2 | 存量 CNY 买 CNY：CNY 暴露不变 | ✅ | `test_same_currency_buy_leaves_currency_exposure_unchanged` + C# `SameCurrencyCashToAssetKeepsTheCurrencySlice` |
| 3 | 外部资金买 USD：旧权重缩放、合计 1 | ✅ | `test_external_money_scales_old_weights_before_adding` + C# `ExternalMoneyScalesEveryOldWeightIdentically` |
| 4 | 卖出 USD：地区转入 cash、USD 不变 | ✅ | `test_sell_moves_region_to_cash_and_keeps_currency_total` |
| 5 | 输入权重不守恒 fail-closed | ✅ | `test_non_conserving_input_fails_closed`（合计≠1 → 空结果+「权重不守恒」注记） |
| 6 | 固定费已填、比例成本空 → 净额未知 | ✅ | `test_fixed_fee_without_variable_cost_is_gross_only` + C# `FixedFeeOnlyRuleIsNotEvaluated…` |
| 7 | 比例显式 0 + 固定费 1 → 毛额−1 | ✅ | `test_explicit_zero_variable_cost_with_fixed_fee_deducts`（含 via/split 变体） |
| 8 | 单段/两段/分批同一成本语义 | ✅ | `test_fee_semantics_identical_across_single_via_and_split` + `CostInvariantTests`（三态全部路径） |
| 9 | 同日提现+无关入账分别处理 | ✅ | `SameDayWithdrawAndUnrelatedDepositDoNotNet`（1000 旧成本释放 + 1000 未知池）+ 部分重叠/先购后花 2 例 |
| 10 | 显式跨账户转账 | 🟡 短期方案 | 无 Netting 下转账保守处理（转出释放、转入未知，§6.2 认可）；显式 `CashTransfer` 类型未实现，见遗留 |
| 11 | 三频道日期分列展示 | ✅ | `test_all_channel_dates_stay_visible_and_separate` + `DateAnomalyTests`；桌面渲染为本轮 P2-01 BeforeAfterLines（无独立渲染测试，冒烟只覆盖导航） |
| 12 | 2026Q1 不参与 ISO 排序 | ✅ | `test_quarter_period_never_participates_in_date_sorting` |
| 13 | UI 输入 20 → 生产解析器输出 0.2 | ✅ | `MaxLossInputConversionTests`（9 例，直接调用 `PercentInputParser`，无镜像） |
| 14 | 分批位于两单一路径之间、量纲一致 | ✅ | `test_split_sits_between_the_two_single_paths_with_the_right_units`（旧「分批最优」假测试已改名并收紧断言） |
| 15 | Python 干净环境无告警 | ✅ | `python -W error` 全量 425 例通过（含真实 venv 版本核对：requests 2.34.2/urllib3 2.7.0/cs 3.4.9 无告警；pyproject 已锁范围） |

### 全量验证（§12 命令）

- 构建 0 警告；C# **579** 全过（Host 549 + Desktop 30）
- Python **425** 全过（`python -W error`，零警告）
- `BackgroundExtendedCheckIntegrationTests` 2/2（真进程 63s）
- `scripts\ui_smoke.ps1` 通过

### 工作包摘要

- **WP1 权重守恒**：`_region_after`/`_currency_after` 共享助手——存量现金买入「现金地区−w、
  标的地区+w」「资金币种−w、标的币种+w」（同币种自然抵消）；外部资金先按 (1−w) 缩放全部旧桶
  再加入；卖出地区转 cash、币种切片不变（所得留在标的币种，不静默换汇）；输入合计≠1 或
  负权重 → 空结果 + 「权重不守恒」注记。旧测试（110% 固化）已更新。
- **WP2 费用三态**：`PathCost.is_variable_cost_known/fixed_fee_is_known`；单段 `_apply_costs`
  比例未知→gross_only；**不变量测试逼出第二个真缺陷：固定费未知（点差已知）也被静默当零**，
  已同样 fail-closed；两段/分批同语义；`PathCost.zero()` 补全两字段显式零；C# 规则要求
  点差与固定费都显式（0=确认无），设置 UI 两个必填 + tri-state tooltip。
- **WP3 逐笔重放**：删除按日净额聚合，按 TradeDate→RecordedAt→Id 逐笔重放；同日提现与
  无关入账不再抵消（提现释放均价成本、入账进未知池）；跨账户转账保守处理（计划 §6.2 认可
  的短期方案，显式 CashTransfer 留作后续）。
- **WP4 多频道日期**：`market_as_of` 字段（严格 ISO，只含行情类通道）；顶部文案改
  「最新行情截至」；桌面 headline 逐项渲染 K线/报价/估值/财务期日期（渲染代码落地，但
  **无独立桌面渲染测试**，UI 冒烟只覆盖导航——复审 P2-05 指正）；任何通道落后即警告
  （取消 365 天一刀切）；财务报告期按披露节奏提示（复审 P2-03 已改季度序号计算）。
- **WP5 测试真实性**：`PercentInputParser` 生产类（View 与测试共用，删除测试镜像）；
  「分批最优」假测试改名「位于两条单一路径之间」并收紧断言（best_path 必须是最便宜单一路径）；
  新增性质级不变量（费用递增净额递减、正固定费净额<毛额、未知费用无 ok 状态、外部资金
  等比缩放、冲正=从未发生、同币种切片不变）。**更正门禁记录**：§人工验证一行此前把自动化
  断言+导航冒烟表述成「人工验证四种视觉状态」——不实。实际状态：自动化断言覆盖
  risk_exceeded/insufficient 的输出与原因；正面/冲突有 ConclusionTests；**真人逐项视觉验收
  未完成**（§13 九项清单待人工执行）。
- **WP6 依赖**：真实 venv 核对（requests 2.34.2 / urllib3 2.7.0 / charset-normalizer 3.4.9）
  当前组合无 RequestsDependencyWarning，真实请求路径 `-W error::Warning` 干净；pyproject
  market extras 锁定 requests≥2.32 / urllib3>=2.2,<3 / charset-normalizer>=3.3,<4。
  审核报告所见告警来自审核环境而非本项目 venv——已按计划记录版本与来源，未屏蔽任何警告类。

### 遗留（如实声明，不称"全清"）

- **WP3 显式 `CashTransfer`**：未实现（计划允许的短期保守方案已落地并有测试）；正式类型
  含 CSV 列、双账户原子性、旧数据候选提示，为下一个工作包
- **§13 UI 人工验收**：九项需真人打开应用逐项检查——Agent 不可替代，未完成
- **验收 #10**「显式跨账户转账本金不改变全局成本」按保守语义测试（计划 §6.2）；
  CashTransfer 落地后升级为显式测试

## 2026-09-06 - remediation-recheck 复审修复（P1×4 + P2×5）

依据：`code-review-2026-09-06-remediation-recheck.md`（暂不通过：4 P1 + 5 P2）。复审核心
指正：**测试只直调内核、未走桌面→服务端生产接线**——自动化全绿时用户可见的错误仍在。
本轮每项修复均含生产链测试。

### §8 必测清单核对（14 条）

| # | 要求 | 测试 |
|---|---|---|
| 1 | 客户端序列化含 funding_currency=CNY | `SideFundingAndMaxLossTravelInTheRequest`（扩展断言） |
| 2 | 真实 C#→Python 链路 CNY→USD 权重守恒 | `ExistingCnyCashBuyingUsdConservesCurrencyWeightsThroughTheRealChain`（真进程 12s） |
| 3 | 卖出超持仓只用实际可卖金额 | `test_oversell_uses_theSellableAmountEverywhere`（现金 70% 非 140%） |
| 4 | 卖出输入合计≠1 fail-closed | `test_sell_input_weights_not_conserving_fail_closed` |
| 5 | 权重非负/有限/合计 1/现金一致 | `SellConsistencyTests` 4 例 + `WeightConservationTests` 6 例 |
| 6 | 显式 0 比例+0 固定费得净额 | `test_all_costs_explicitly_zero_equals_gross` + 真链路 `ExplicitZeroSpreadWithFixedFeeProducesANetFigureThroughTheRealChain` |
| 7 | 只填一种费用 gross_only 并指出缺项 | `test_fixed_fee_without_variable_cost_is_gross_only` + 链路测试第二段（净额 None） |
| 8 | 两批购汇预填 200 非 180 | `TwoBatchesShowTheActualInvestedCostNotTheLastRate` |
| 9 | 未知成本不自动填完整成本 | `UnknownOriginMoneyBlanksTheWholeCostWithAnExplanation` |
| 10 | 桌面渲染 before/after 文字 | **部分**：渲染代码（BeforeAfterLines：地区/币种/现金/资金币种）落地；独立渲染测试未做（UI 冒烟只覆盖导航）——如实标注 |
| 11 | 不可解析日期频道级警告 | `test_unparseable_channel_date_flags_a_format_anomaly`（含 provenance 保留） |
| 12 | 季度阈值跨年正确 | `test_financial_period_staleness_counts_quarters_across_years`（2023Q4 vs 2025-07）+ 1 季差不警告 + 坏格式 |
| 13 | pypinyin 两环境确定性 | `test_an_unknown_name_without_a_converter…`（注入 None 协议）+ `test_with_a_converter…`；系统 Python 与 managed venv 双绿 |
| 14 | managed venv requests 链 -W error | 验证命令通过（2.34.2/2.7.0/3.4.9 零警告） |

### 修复摘要

- **P1-01**：`PostEvidenceCardAsync` 增加 `fundingCurrency`（桌面传本位币——表单以本位币
  计额）；真进程链路测试证明 CNY 0.6→0.5、USD 0.4→0.5、合计 1（此前直调 Python 的测试
  手工注入了该字段，链路上它从未到达）。
- **P1-02**：卖出统一用实际可卖权重——持仓/现金/地区同用 `candidate_weight`（超卖时现金
  70% 而非 140%）；复用守恒校验（合计≠1 → 空结果+注记）；地区切片小于持仓 → fail-closed；
  `cash_weight_after` 与 region["cash"] 两个口径的差异=非权益非现金切片（测试写死该不变量）。
- **P1-03**：汇率页每条路径补固定费+币种输入（tri-state tooltip：留空=未知、0=确认无）；
  非法文本提交前报错（不再静默忽略）；费用不完整时状态栏预告毛额比较；真链路测试验证
  「0 点差+1 USD 固定费 → 净额=毛额−1」与「仅固定费 → 净额 None」。
- **P1-04**：预填决策提取为 `LedgerHeldCostPrefill`（可测纯函数）：移动加权成本（200 非
  180）；部分未知 → 整体留空+说明（绝不让定价部分冒充整体）；两批/提现/外部入账/空账本
  4 反例。桌面 `MostRecentAcquisition` 路径删除。
- **P2-01**：DTO 补 before 字段（region/currency_before、cash_weight_before、
  funding_currency）；桌面 `BeforeAfterLines` 渲染「k 前→后」（仅动的切片显示变化）。
- **P2-02**：不可解析日期 → 「日期格式异常：quote 的日期"garbage"…」警告（provenance 保留）。
- **P2-03**：报告期转季度序号（year×4+Q）跨年比较（2023Q4 vs 2025-07 = 7 季警告；
  1 季差不警告；坏格式标异常）——顺带发现原实现把 ISO 行情日喂给 YYYYQn 解析器必返 None，
  补 `_quarter_index_from_iso`。
- **P2-04**：注入协议扩展（converter 返回 None=无法罗马化）+ 双环境对照测试；
  门禁改用 managed venv（本报告全量验证均用 `%LOCALAPPDATA%\Caishenfolio\state\.venv`）。
- **P2-05**：quality-gates 修正 3 处——过时测试名（split 假测试旧名）、WP4「桌面渲染」
  过度声明（补「无独立渲染测试」标注）、行 11 证据描述改为实际内容。

### 全量验证（§9 命令，managed venv）

- `dotnet build` 0 警告 0 错误；C# **586**（Host 551 + Desktop 35）
- managed venv `python -W error` 全量 **434** 通过（1 skip；requests 链导入零警告）
- 门控集成 **4/4**（后台链路 2 + 证据卡链路 1 + fx 路径链路 1，真进程）
- `scripts\ui_smoke.ps1` 通过；`git diff --check` 无实质错误

### 遗留（如实）

- §10 人工 UI 验收（9 项）：需真人执行，未完成
- before/after 桌面渲染的独立自动化测试（渲染代码已落地，冒烟只到导航）
- 显式 `CashTransfer`（前轮遗留，不变）

## 2026-09-07 - remediation-recheck-2 修复（F-01 P1 + F-02 P1 + F-03 P2 + F-04 P3）

依据：`code-review-2026-09-07-remediation-recheck-2.md`（未通过：F-01 布局重叠、F-02 卖出
不变量缺口、F-03 日期静默路径、F-04 文案不一致）。复审同时确认上一轮 9 项中 6 项通过、
3 项部分通过（部分项即本轮 F-01/F-02/F-03 的来源）。

### 修复与反例

| 项 | 修复 | 测试/证据 |
|---|---|---|
| F-01 汇率页两费用面板同 Grid.Row="2" 重叠（后声明者覆盖前者绘制与命中） | 父 Grid 增至 5 行：比例成本 Row2、固定费 Row3、结果 Row4 | **冒烟脚本新增程序化布局断言** `Invoke-CostInputLayoutCheck`：UIAutomation 按 AutomationId 找 6 个成本输入框，断言每个宽高>0 且两两边界不相交——"页面能导航"不再冒充"布局可用"；运行输出 `fx cost input layout 6 个成本输入框可见且互不重叠` |
| F-02A 部分卖出绕过地区切片自洽（比较对象是钳制后的 candidate_weight 而非完整持仓） | 切片校验改为 `region[instrument_region] >= current_weight_of_symbol`；复审反例（持仓 20%/切片 10%/卖 5% → us=5%）现在 fail-closed | `test_partial_sell_with_inconsistent_region_slice_fails_closed`（断言空结果+「输入不自洽」+「小于该标的当前持仓」） |
| F-02B 卖出货币权重未校验直传（合计 120% 原样到 UI） | 新共用校验器 `_validated_distribution`（有限/非负/合计=1，买入卖出同用）；卖出货币切片≥持仓校验；**输出端不变量**：任何非空 after 分布再查一次，不守恒即清空 | `test_sell_with_non_conserving_currency_weights_fails_closed`（120%→空+注记）+ 负值/NaN 2 例 + 参数化三档卖出量非空分布合计=1 |
| F-03A `fromisoformat` 接受紧凑格式 20250719，词典序 max() 可选错主日期 | `_is_iso_date` 改正则 `^\d{4}-\d{2}-\d{2}$` + round-trip 相等；主日期改为**先解析成 date 排序**再取对应原始串 | `test_compact_date_form_is_rejected`（含 2024-02-29 过/2025-02-29 拒）+ `test_mixed_formats_pick_the_true_latest_by_parsed_date`（2025-07-20 与 20250719 混合 → 主日期 2025-07-20 且紧凑值告警） |
| F-03B 只有异常财报期（无行情日期）时扫描整体跳过 | 外层条件改为「任一通道有数据即扫描」 | `test_only_a_bad_financial_period_still_warns`（仅 2026XX → 格式异常警告） |
| F-04 tooltip 写"最近一次换汇"（实现已改移动加权）+ 费用完整性提示被下一行覆盖 | tooltip 改"移动加权；来源不明留空并说明"；提示存 `_costCompletenessNote` 字段，**结果渲染后**写回状态栏（同一流程不再被覆盖） | 构建+冒烟通过；文案在代码内可查 |

### DoD 验证（§5 逐条，2026-09-07 执行）

- `dotnet build --no-restore`：0 warning / 0 error
- `dotnet test --no-build --no-restore`：**586** 全过（Host 551 + Desktop 35）
- managed venv `python -W error` 全量：**441** 通过（1 skip；+7 为本轮反例）
- 真实进程门禁：**4/4**（后台 2 + 证据卡链路 1 + 换汇路径链路 1）
- `scripts/ui_smoke.ps1`：通过，**含新增的汇率成本输入框布局互不重叠断言**
- Release MSI：构建通过，0 warning / 0 error（`packaging/windows/bin/x64/Release/OMNIX-Caishenfolio.msi`）
- 未回退任何现有用户改动；未引入实盘能力；权重异常全部 fail-closed

### 未做（如实）

- F-01 验收用例中的「DPI 100/125/150 三档人工目视」：冒烟断言在默认 DPI 下程序化验证
  了可见性与不重叠；多 DPI 档的机器级遍历未做（属 §10 真人 UI 验收范围）
- 汇率页「填 0+0 得净额」的桌面级端到端（真进程链路测试已覆盖同语义：
  `ExplicitZeroSpreadWithFixedFeeProducesANetFigureThroughTheRealChain`）

## 2026-09-07 - 第三轮独立复审结果

完整验收暂未通过，详见 `code-review-2026-09-07-remediation-recheck-3.md`。
本轮独立执行构建 0/0、.NET 586、managed Python 441（1 skip）、真进程 4/4、
九页 UI 冒烟及六成本框边界断言、Release MSI，全部通过。UI 证据文件：
`.local/ui-smoke/recheck-3-ui-smoke-report.json`。

自动门禁通过不关闭 R3-01（外部资金占比及误报风险）和 R3-02（无合法日期时回退）。
多 DPI、Tab/点击和桌面输入→净额展示仍未验收；先前“DoD 全过”应限定为自动门禁通过。

## 2026-09-07 - remediation-recheck-3 修复（R3-01 P1 + R3-02 P2）

依据：`code-review-2026-09-07-remediation-recheck-3.md`（暂不通过：外部资金稀释缺陷、
日期回退遗漏）。复审确认上轮 F-01~F-04 关闭（F-01 留 UI 完整 DoD 尾巴，见下）。

### 修复与反例

| 项 | 修复 | 测试/证据 |
|---|---|---|
| R3-01 外部资金买入未稀释原持仓（20%+10k/110k=29.09% 误报超 28% 上限；地区 27.27% vs 单仓 29.09% 自相矛盾） | `single_after = (current_weight × base_value + amount) / total_after`——旧持仓与新增金额同一交易后分母；存量现金分支（总额不变）语义保持 | ①复审反例转测试：`test_review_counterexample_27_27`（27.2727% + 28% 上限**不**触发 risk_exceeded + 现金 50000/110000）②跨维度一致性：`test_symbol_after_equals_region_after_when_region_holds_only_it`（标的 after == 只含它的地区 after）+ `test_symbol_after_never_exceeds_its_region_after` ③回归保护：存量现金 30% 不变、零旧持仓、旧错误测试 `test_external_money_grows…` 期望改为 40000/110000=36.3636% ④**真链路断言** `ExternalFundingDilutesTheOldPositionThroughTheRealChain`（C#→Python：27.2727% + 结论非 risk_exceeded） |
| R3-02 无有效市场日期时回退未校验值（garbage/20250719/2026Q1 经 fallback 成标题"最新行情截至 2026Q1"） | 删除回退分支：`as_of = iso_dates[-1] if iso_dates else ""`——标题只来自通过严格校验的日期；桌面渲染空值时显示"行情日期未知（见数据质量区）"，ConclusionMeta 同步 | `test_rejected_values_never_become_the_headline`（三值参数化：标题空、channel_as_of 保留原值、格式警告在）+ `test_external_illegal_as_of_never_masquerades`（外部传 garbage as_of 也不冒充）+ `test_valid_dates_still_work_as_before`（回归） |

### 门禁结果（§5 命令逐条，2026-09-07 执行）

- .NET Debug 构建：0 警告 0 错误；全量测试 **587**（Host 552 + Desktop 35）
- managed venv `python -W error`：**449** 通过（1 skip；+8 为本轮反例）
- 真实进程门控：**5/5**（后台 2 + 资金币种链路 1 + 固定费链路 1 + **外部资金稀释链路 1（新增）**）
- UI 冒烟：通过（九页导航 + 设置窗口 + 6 成本框布局断言）
- Release MSI：0 警告 0 错误；`git diff --check` 无空白错误

### 口径统一（复审 §4 要求）

**本轮状态：自动门禁通过，完整 UI DoD 未完成。** 具体未完成项（未测就是未测）：

1. 成本输入框的 Tab 焦点顺序、鼠标点击命中（布局断言只验证可见与不重叠）
2. 桌面表单输入→净额渲染的端到端（真链路测试覆盖同语义，但无页面输入证据）
3. 100%/125%/150% DPI 三档实测（当前只在默认 DPI 程序化验证）
4. 窗口缩放/低分辨率下的可视性
以上归入真人 UI 验收清单，不由 Agent 替代完成。

## 2026-09-07 - 第四轮独立复审：代码修复通过，UI 完整验收待完成

- 报告：`code-review-2026-09-07-remediation-recheck-4.md`；R3-01/R3-02 正式关闭。
- 独立门禁：构建 0/0；.NET 587（552+35）；managed Python -W error 共449项（448通过/1跳过）；开启门控真进程5/5；UI启动/九页/设置窗口/六成本框边界检查；Release MSI 0/0；diff --check 无空白错误。
- 生产探针：金额核算27.2727%、现金45.4545%、28%上限不误报；30%旧仓外部买入36.3636%；现有现金买入30%；无旧仓9.0909%；非法市场日期保留来源告警但标题未知。
- UI 证据：`.local/ui-smoke/recheck-4-ui-smoke-report.json`。完整 UI 未测项沿用上节，不宣称真人验收完成。

## 2026-09-07 - recheck-4 后续：UI 验收 §5 前两项的程序化证据落地

背景：第四轮复审通过代码修复（R3-01/R3-02 关闭），§5 列四项 UI 验收"保留人工"。
本轮把其中**可用程序化推进的两项**做进 `scripts/ui_smoke.ps1`（每次冒烟自动复查），
后两项（DPI 三档、真人新手）仍如实保留为人工。

### 新增冒烟检查

| 检查 | 内容 | 结果 |
|---|---|---|
| `fx cost input focus` | 6 个成本输入框逐一：`ValuePattern.SetValue("0")` 可写、`SetFocus` 后 `HasKeyboardFocusProperty`=true、`IsOffscreen`=false（仅证明 UIA 可写/可聚焦/非屏外，不证明鼠标点击或 Tab 遍历；2026-09-08 纠正口径） | 通过 |
| `fx form to net e2e` | **真实表单端到端**（复审 §5#2）：费币种选 USD（ExpandCollapse+SelectItem）→ 场景A 全 0 填入 → 点「比较兑换路径」→ 断言渲染「毛到账 X USD　→　净到账 X USD」两数相等且状态「净额已计入成本」；场景B 固定费 1 → 断言净=毛−1.0（±0.01，数值解析比较，provider 无关） | 通过 |

### 调试中发现并修正的三个 UIA/断言问题（记入 error-ledger）

1. `IsKeyboardFocusedProperty` 不存在——正确名是 `HasKeyboardFocusProperty`
2. PowerShell 中 `"str".Method()` 链式调用绑定到字符串而非 UIA 元素——先赋值再调用
3. 折叠状态的 WPF ComboBox 上 `SelectionItem.Select()` 不总是提交选择——须先
   `ExpandCollapsePattern.Expand()` 再 Select（表现为"固定费 1 CNY 折算 ≈0.149 USD"
   被误判为后端错误，实为费币种仍是默认 CNY）
4. 断言期望值必须 provider 无关（真实行情数字不可预知）：净==毛用分组相等比较；
   净=毛−费用捕获数值解析后比较，不写死 13.888889

### 验证

- 扩展冒烟全绿（12 步：含新 3 项 FX 检查 + 九页导航 + 设置窗口）
- 回归：C# 587（552+35）全过；managed venv Python 449（1 skip）`-W error` 全过
- **仍未测（如实）**：100/125/150% DPI 三档、窄窗口/低分辨率、Tab 顺序完整遍历、
  真人新手无指导操作——保留为人工验收项（复审 §5#3/#4）

## 2026-09-08 - 第五轮 UI 补充独立复审（最新）

上一节“12 步全绿”为交付时记录，本轮未能复现。以本节和
`code-review-2026-09-08-ui-acceptance-recheck-5.md` 为当前验收状态。

- 构建 0 警告 / 0 错误；C# 587（552+35）；managed Python -W error 449 项（448 pass / 1 skip）。
- 实际启动两次：九页导航、六框布局、UIA 可写/聚焦/非屏外通过。
- 净额 E2E 两次在场景 A 返回 HTTP 422；场景 B、后续设置窗口未执行到。完整冒烟失败，不以已通过子步骤代替整体验收。
- 证据：`.local/ui-smoke/recheck-5-ui-smoke-report.json` 与 `recheck-5-ui-diagnostic-report.json`（同目录）。
- 实际 health：cache+upstream / synthetic=false；父进程 fixture 被桌面凭据覆盖。422 响应体未取得。
- 待修 R5-01/02（环境隔离/路径断言）、R5-03（诊断/口径）。未选持有币种导致持仓分支无有效页面级覆盖。
- 真进程 5 项与 MSI 本轮未另跑，上一轮证据不改日期充作本轮；diff --check 无空白错误。
- 人工未测：DPI 三档、窄窗口/低分辨率、鼠标命中、Tab 遍历、真人新手。

## 2026-09-08 - recheck-5 UI 验收整改（R5-01/R5-02 P2 + R5-03 P3）

依据：`code-review-2026-09-08-ui-acceptance-recheck-5.md`（UI 补充验收暂不通过：表单端到端
在复审环境连续 422——fixture 未在真实桌面链路生效）。本轮按 §4 顺序整改。

### 整改项与证据

| 项 | 整改 | 证据 |
|---|---|---|
| R5-01 fixture 未生效（用户凭据的 ApplyToEnvironment 覆盖继承的 fixture 变量；冒烟读用户真实状态） | ①桌面支持 `CAISHENFOLIO_TEST_STATE_ROOT`（Host 显式授予的隔离根，生产不设此变量不受影响；整棵状态树——凭据/账本/缓存/venv 供给——都在隔离根内）；②broker 在凭据注入**之后**应用 `CAISHENFOLIO_TEST_FORCE_PROVIDER`（仅测试变量优先，不放宽生产优先级边界）；③冒烟启动前预置隔离账本（sqlite 全表 schema v9 + 一笔 100 CNY→2100 JPY 回单）；④新增 `core health reachable` 步骤：HTTP 读 /health，断言 provider=fixture 且 synthetic=true，错误数据源**当场**带 provider 名失败（不再等 40 秒报"净额未出现"） | 冒烟步骤 `core health reachable provider=fixture synthetic=True`；负例脚本 `.local/ui-smoke/negative-provider-check.ps1`（auto 核心 → cache+upstream/synthetic=False → gate 拒绝，NEGATIVE-OK） |
| R5-02 未选持有币种/首条文本匹配无路径身份 | ①持有币种经**真实 UI 路径**获得：预置账本 → 点「按账本填入」→ 断言填入结果（金额 2100/成本 100）与 FxHeldCombo 选中 JPY；②逐卡断言：`Get-CardText` 从 Label 元素**上溯卡片容器**收集全部文本（Label 与 AmountsText 是不同 UIA 元素），持仓卡（"已持有 JPY 直接换"）与新资金卡（"新 CNY 直接换"）各自独立断言 0+0 净=毛、0+1USD 净=毛−1（±0.01）；③绕行卡费用留空 → 断言「成本输入缺失」状态（此前 B 场景留空绕行却不检查）；④等当前响应落定（等"已比较 N 条路径"标记翻转）后才取结果，不匹配旧渲染；串线排除：每卡按自身 Label 定位，持仓费与新资金费互串会让对应卡断言失败 | 冒烟步骤 `fx form to net e2e 持仓卡与新资金卡逐卡断言：0+0 净=毛；0+1USD 净=毛−1；绕行留空=成本缺失` |
| R5-03 失败插值错误 + 焦点声明扩大 | ①失败文本先赋值变量再读 `.Current.Name`（原内插输出属性路径描述而非正文）；②焦点检查步骤名与注释收紧为"UIA 可写、可聚焦、非屏外"，明示鼠标命中与 Tab 遍历未测；③skipped 不计通过（既有逻辑保持） | 脚本 diff；步骤详情文字更新 |

### 验证（复审 §4 第 4 步全项）

- **连续两次完整冒烟**：`run-1-report.json` / `run-2-report.json` 均 status=passed、
  14 步全绿、failure=null（含隔离预置/provider gate/逐卡端到端/九页导航/设置窗口）
- 构建 0 警告 0 错误；C# **587**（Host 552 + Desktop 35）
- managed venv `python -W error` **449**（448 pass / 1 skip）
- 门控真进程 **5/5**（后台 2 + 证据卡链路 1 + 固定费链路 1 + 外部资金稀释 1）
- Release MSI 0 警告 0 错误
- 用户正式状态不受影响：隔离根在仓库 `.local/ui-smoke/state` 内，冒烟结束即弃

### 过程中的实现教训（记 error-ledger）

1. 预置库必须带**全表**——应用打开 user_version=9 的库会跳过建表，缺 settings 表直接
   XamlParseException（MainWindow 构造期读设置）
2. PowerShell 里用 Python `-f` 格式化拼 SQL 会因 `%` 语义崩坏——用唯一占位符 + `.Replace()`
3. 生成的 .py 必须 UTF-8 **无 BOM**（venv Python 把 BOM 文件当语法错误）
4. 卡片 Label 与 AmountsText 是不同 UIA 元素——按行 join 的文本匹配天然失败，须从元素
   上溯容器收集

### 仍未测（如实保留人工，复审 §4 第 6 步）

- 100/125/150% DPI 三档、窄窗口/低分辨率、鼠标点击命中、Tab 顺序完整遍历、真人新手
  无指导操作

## 2026-10-07 - 状态盘点的基础门禁（不替代正式复审）

- dotnet build --no-restore：0 警告 / 0 错误；dotnet test --no-build --no-restore：587 通过（552+35）。
- 应用 managed venv + fixture，python -W error：449 项，448 通过 / 1 跳过。
- git diff --check 按仓库原换行设置通过；有 LF/CRLF 提示，无空白错误。
- UI / 门控真进程 5 项 / MSI 本日未重跑；只核对 2026-09-08 run-1/run-2 保存报告均 14 步 passed。
- 第五轮整改仍待独立复审；完整 UI、无障碍、真人任务未验收。详见 `development-status-2026-10-07.md`。

## 2026-10-07 - 第六轮交付复审（历史，见下方第七轮）

- 构建成功：0 错误、2 条 xUnit1030 警告（AnalyticsCoreClientTests.cs:275、361），不是 0/0。
- C# 599（Host 564 + Desktop 35）；managed Python -W error 449 项（448 pass / 1 skip）；门控真进程 5/5。
- 负例脚本新报告 `.local/recheck-6/negative-report.json` 为 3/3；前两项复制生产逻辑，不能据此关闭 R6-05。
- 当前标题对现有冒烟正则为 false；正式产品根被测试根校验接受；未显示控件的入金构造仍解析隐藏数量错误。见 observations.json 与 Probe/Program.cs（同目录）。
- 当前 UI / DPI / MSI 未重跑；保存的旧版 16 步报告与显示记录是历史证据，不覆盖后续界面改动。
- diff --check 无空白错误。暂不通过，需修 R6-01～05，见 `code-review-2026-10-07-delivery-recheck-6.md`。

## 2026-10-07 - 第七轮整改复审（历史，见下方第八轮）

- dotnet build --no-restore：0 警告/0 错误；dotnet test --no-build --no-restore：602 pass（Host 567、Desktop 35），0 skip。
- managed venv，fixture，python -W error：449 项，448 pass/1 skip；门控真进程 5/5；生产函数负例 3/3。
- 定向控件逻辑探针证实三个剩余 P2，详情与证据见 `code-review-2026-10-07-remediation-recheck-7.md`；基础回归未覆盖这些反例。
- 原正式根接受/非法回退 P1 核心关闭；两次完整 UI、DPI、窄屏、MSI、本版人验未执行，不据此声明最终通过。
- diff --check exit 0；仅审核/诊断材料改动，产品源码保持原样。

## 2026-10-07 - 第八轮整改复审（最新结论）

- 构建 0 警告/0 错误；C# 605 pass（Host 570 + Desktop 35）；managed Python -W error 449 项（448 pass/1 skip）。
- 门控真进程 5/5，其中后台空账本测试复用预存 8765 核心（cache+upstream），不是五项均自启 fixture；生产函数负例 3/3。
- R7 原样探针：无关金额不再阻断买入、必需数量错误仍拒绝、盘符相对路径拒绝；旧 5 年响应仍覆盖新 10 年结果。R7-02 未关闭。
- `.local/recheck-8` 保存本轮日志；正式账本主文件前后哈希/长度/修改时间一致；7 个受审文件无漂移；diff --check exit 0。
- 两次 UI、DPI、窄屏、读屏、真人及 MSI 未执行；本轮未修改产品实现。详见第八轮报告。

## 2026-10-07 - FA1 任务书制定（不是新增通过结果）

- 静态核对最新 FxCarryView 的序号、失败状态及 XAML 修正；本次未运行构建、单测、UI 或 MSI，不继承第八轮结果到新实现。
- 具体门禁、隔离前置条件、长期回归和人工边界统一见 `agent-stage-final-acceptance-2026-10-07.md`。
- 后续结果由执行 Agent 按候选 ID 保存，并通过 final-candidate-delivery.md 提交集中终审；任务书不构成发布或完整 UI 验收。

## 2026-10-07 - FA1 最终候选验收执行（fa1-20261007-1410）

- 候选冻结：HEAD 0c3e3b3、main、未提交工作区；287 个源文件 SHA-256（含未跟踪实现）冻结于门禁前；
  冻结后仅两个验收脚本变化（ui_smoke.ps1 步骤扩展、ui_dpi_probe.ps1 坐标修正），产品源码零漂移。
- 门禁（同一冻结源码）：构建 0/0；C# 615（Host 570 + Desktop 45，新增 A1×7 + A2×3 回归，0 失败 0 跳过）；
  managed Python -W error 449 项（448 pass / 1 skip 按设计联网）；真进程 5/5（本轮 8765 前后空闲，
  五项均为本轮自启 fixture，与第八轮复用情况不同）；生产负例 3/3（最终脚本上重跑）；
  `git diff --check` exit 0。
- 两次隔离 UI 冒烟 18/18 步全过、无关键 skipped：含新增 `fx range switch e2e`（区间切换、busy
  禁用/恢复、逐区间结果）与 `ledger type switch e2e`（隔离账本入金 500 入账；买入在残留禁用金额
  文本下仍入账，流水表可见 SSE:600000）。重试史如实记录（见 gates-log.md）：8 个本会话遗留孤儿
  核心被门禁拒绝后精准清理；新增步骤三处脚本缺陷修复；约 4/11 次出现隔离核心 60 秒未就绪的环境
  抖动（同环境多数正常，未定位到源码层原因），最终两次连续运行完整通过。
- 正式账本主文件前后 SHA-256/长度一致，无 WAL/SHM；未提交/未推送/未发布/未安装。
- Release MSI 0/0：2,375,680 字节，SHA-256 3F600334…A405，版本 0.13.3；管理提取核对内嵌 Python
  源码与工作区字节一致、无账本/凭据/缓存。安装/升级未授权未执行。
- 显示/人验：200%（当前）下 6 成本框真实鼠标命中 6/6、Tab 全页遍历、1020×680 逻辑窄窗可用
  （dpi-current-probe-clickfix.json）；100/125/150%、1280×720、讲述人逐卡、真人十一项如实
  blocked/not_run，集中清单见 `acceptance/final-candidate-human-verification-2026-10-07.md`。
- 完成层级：可进入统筹终审；完整阶段验收待统筹确认。交付索引：`final-candidate-delivery.md`。

## 2026-10-07 - FA1 集中终审（覆盖上节结论）

- 发现原 WpfTestHost 隔离失效，旧 615 绿灯不是无副作用证明；统筹已直接修复测试宿主并加长期回归。
- 最终独立构建 0 警告/0 错误，C# 616（Host 570 + Desktop 46），真进程 5/5（相关端口事前空闲、fixture），负例 3/3，diff --check 无错误。
- 原 287 指纹全部匹配；终审仅改两个测试侧文件并新增一个测试文件，最终 `review-overlay-final.json` 单独记录。MSI 指纹吻合，产品/Python/验收脚本未变。
- Python 448+1skip 为已核对的执行方日志，本轮未重复运行；两份 UI 18/18 为历史功能证据，本轮未重跑。
- UI busy 全为 too-fast，没有观察到页面禁用时间窗；无效旧金额反例由单测覆盖，页面只操作数值 500。不可把两者分别写成页面已完整实证。
- 首次独立测试留下正式路径非 fixture 核心；精准清理后，修复后单测及真进程结束无核心残留。主账本观测指纹不变，不推断未访问。
- 完整阶段暂不通过：启动超时尚未定位，首次引导等待/归属及逐次日志需收口；人工矩阵、读屏、真人和安装未完成。详见 `final-candidate-review-2026-10-07.md`。

## 2026-10-08 - FA1 工程收尾门禁

- `final-candidate-repair-2026-10-08.md` 为最新结论；FA1-R02/R03 关闭，前批 R01 与 R7 回归保持。完整产品人验仍未完成。
- 10 月 7 日最终脚本：构建 0/0；C# 616；managed Python -W error 448 pass / 1 skip；生产函数负例 13/13；固定五次冷启动 11/11，随后两次完整 UI 18/18，无 skipped。
- 10 月 8 日：真进程 5/5，相关端口/核心事前空闲，fixture；最终 292 文件与 UI 清单无漂移；diff --check 通过。
- 证据 `.local/fa1-repair-20261007/attempt-index.json` 包含一次真实脚本调试失败及七次最终通过，各 attempt 独立报告和日志，七次清单 SHA-256 相同。
- 正式主账本前后指纹一致、无 WAL/SHM，无测试核心/监听残留；仅此观测范围，不宣称整个正式目录审计。MSI 原指纹不变，未重新打包或安装。
- 旧失败现场无法恢复；确认的引导竞态已修，新固定样本未复现超时，不承诺所有历史故障已归因或永久无超时。

### 2026-10-19 - FX / Android 架构审查交付

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | Pass | fixture、隔离 grid DB；C# 选定单测使用临时库；纯内存探针 | 未启动正式桌面/通知，不读取个人正式账本 |
| Data, API, consistency | Warn | 源码调用链与 FX-01/02 探针 | 已发现缺陷和新模型差距，按审查范围未修 |
| Code quality | N/A | 仅报告与开发记录 | 无产品代码改动 |
| Testing | Warn | Python FX 64 pass；Host/Host.Tests 编译；fx-review.trx | 系统 Python，非 managed 全门禁；C# testhost 超时 0 executed |
| UI / Android | N/A | 未改 XAML，未建 Android 工程 | 不声称 UI 或设备验收完成 |
| Release | N/A | 未更改 VERSION/PHASE、MSI | 未提交/推送/安装/发布 |

交付物：D:/Agent/Worktrees/OMNIX-Caishenfolio/reviews/fx-android-architecture-review-2026-10-18.md（仓库外：含真实交易金额，公开仓库不放）。此门禁记录的是审查证据边界，不是产品新版发布通过。
