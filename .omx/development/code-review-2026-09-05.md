# OMNIX-Caishenfolio 开发审核与修复清单

> 审核日期：2026-09-05  
> 审核范围：当前工作区未提交改动，重点覆盖 N0～N6 的账本、换汇、证据卡、提醒、风险分析与小白界面。  
> 审核结论：**本轮不通过。全部 P1 修复并补齐反例测试前，不应向金融小白发布。**  
> 产品边界：本软件只做账本、研究、模拟与提醒，不接入实盘下单，不输出确定性的买卖时点或收益承诺。

## 1. 给修复 Agent 的执行要求

1. 先阅读根目录 `AGENTS.md`、`.omx/development/current.md`、`.omx/development/handoff.md`，并遵循 `.claude/skills/agent-dev-protocol/SKILL.md`。
2. 按本文顺序修复：先 P1-01～P1-13，再处理 P2。
3. 每个问题必须先增加能复现本文最小反例的失败测试，再修改实现。
4. 不得通过放宽校验、返回 0、隐藏警告或修改断言来让测试变绿。
5. 金额继续使用 `decimal` / `Decimal`；未知价格、汇率、成本或持仓归因必须 fail-closed。
6. 对资金来源、费用口径、现金流时点等存在多种合法语义的地方，必须在契约和 UI 中显式区分，不得隐式猜测。
7. XAML 或资源字典改动完成后，除了编译和单测，还必须运行 `scripts\ui_smoke.ps1`。
8. 修复完成后更新 worklog、decisions、error-ledger、handoff，并逐条列出本文问题对应的测试名称。

## 2. P1 阻断问题

### P1-01 模拟买入混用了存量现金和新增资金口径

涉及文件：

- `src/Caishenfolio.Host/Portfolio/TradeWhatIfAnalyzer.cs:106-160`
- `python/caishenfolio_core/research/evidence_card.py:545-614`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:105-139`
- `tests/Caishenfolio.Host.Tests/PortfolioAnalysisN5Tests.cs:124-150`
- `tests/python/test_evidence_card.py:134-184`

问题：

- C# 使用 `totalAfter = totalNow + amountBase`，把候选金额当作新增外部资金；同时又用 `cashAfter = CashValue - amountBase`，把它当作账本现有现金。
- Python 证据卡采用同样的混合口径。
- 对现有现金买入，总资产原则上保持不变（忽略或另计费用）；对外部新增资金买入，原有现金不应减少。
- 当前实现会低估买入后的单一持仓与货币集中度，可能漏掉风险上限突破。
- 估值不完整时仍可返回 `Feasible=true` 和“未超过任何上限”，不符合 fail-closed。

最小反例：

- 当前总资产 110,000 CNY，现金 50,000 CNY，某持仓市值 10,000 CNY。
- 使用现有现金再买入 20,000 CNY。
- 正确单一持仓占比为 `30,000 / 110,000 = 27.27%`。
- 当前代码按 `30,000 / 130,000 = 23.08%` 计算。

修复要求：

- 增加显式资金来源，例如 `ExistingCash` 与 `ExternalContribution`。
- `ExistingCash`：总资产不增加，现金减少；同币种现金换同币种资产时，货币暴露不应增加。
- `ExternalContribution`：总资产增加；若新增资金立即买入，原有现金不减少。
- 估值不完整或候选币种无法折算时，不得给出“未超限”的绿色结论。
- C# 与 Python 必须使用同一契约和同一组反例。

### P1-02 新增资金再平衡没有考虑新总资产分母

涉及文件：

- `src/Caishenfolio.Host/Portfolio/RebalanceComparisonAnalyzer.cs:68-129`
- `tests/Caishenfolio.Host.Tests/PortfolioAnalysisN5Tests.cs`

问题：

`deltaAmount = (target - current) * total` 只适用于总资产不变的买卖方案，不能用于新增资金方案。新增资金会改变权重分母，因此当前“补齐低配金额”和可行性结论均不正确。

最小反例：

- 当前组合 80/20，总资产 100，目标 50/50。
- 当前实现建议给低配项增加 30，结果为 80/50，即 61.54%/38.46%。
- 仅用新增资金回到 50/50，应增加 60，结果为 80/80。

修复要求：

- 先求能容纳所有现有资产且满足目标比例的新总资产：候选值可从 `currentValue_i / target_i` 推导。
- 再计算每一类 `target_i * newTotal - currentValue_i`。
- 明确区分“只新增资金方案”和“卖出并买入、总资产不变方案”。
- 增加 80/20→50/50、多个超配项、目标为 0、目标和不为 100% 等测试。

### P1-03 估值日当天入金会被计入 TWR 收益

涉及文件：

- `src/Caishenfolio.Host/Portfolio/PortfolioWorkspace.cs:295-325`
- `src/Caishenfolio.Host/Portfolio/ReturnMetrics.cs:118-155`
- `tests/Caishenfolio.Host.Tests/PortfolioSettingsTests.cs:212-246`
- `tests/Caishenfolio.Host.Tests/ReturnMetricsTests.cs:133-148`

问题：

- 快照由当日全部流水重放后生成，属于流后估值。
- `TryTwr` 却排除了所有出现在估值点日期上的现金流。
- 第二个估值日当天入金会被当作资产升值。
- 底层 `TimeWeighted` 的注释假定估值点现金流发生在估值之后，与快照生成语义不一致。

最小反例：

- 第一个快照：100,000 CNY。
- 第二个快照日存入 50,000 CNY，随后刷新得到 150,000 CNY。
- 正确收益为 0%；当前代码会得到 +50%。

修复要求：

- 明确快照是流前还是流后，并将该约定写入模型和文档。
- 正确处理首个估值点、区间中现金流、第二个端点当天现金流、提款以及同日多笔流。
- 增加上述最小反例的工作区集成测试，不能只测纯 `ReturnMetrics`。

### P1-04 换汇顶层最佳路径混合净额和毛额

涉及文件：

- `python/caishenfolio_core/research/fx_paths.py:197-209`
- `python/caishenfolio_core/research/fx_paths.py:248-265`
- `python/caishenfolio_core/research/fx_paths.py:585-623`
- `python/caishenfolio_core/research/fx_paths.py:721-726`

问题：

- `diff_vs_best` 正确地为全部可比较路径选择统一口径：全部有净额才比较净额，否则统一比较毛额。
- 顶层 `_best_key` 却对每条路径分别优先取净额、缺净额再取毛额。
- 同一响应可能出现 `best_path` 与所有 `diff_vs_best.best_path` 不一致。

已验证反例：

- 顶层返回 `best_path=split`。
- 各路径的差额对象同时认定 `best_path=held_to_target`。

修复要求：

- 只计算一次统一比较口径和最佳路径，并让顶层字段及所有差额对象复用该结果。
- 响应中增加明确的全局 `comparison_basis` 字段。
- 增加部分路径有成本、部分路径成本未知的回归测试。

### P1-05 分批换汇少扣固定手续费，排名值还会完全漏费

涉及文件：

- `python/caishenfolio_core/research/fx_paths.py:500-559`

问题：

- 两条分批路径的固定手续费被乘以各自份额。
- 固定费是每次转换发生的金额，不应因兑换份额变小而自动按比例缩小。
- `net_per_base_amount` 仅使用百分比成本，完全遗漏两个固定费用。

最小反例：

- 已持有路径固定费 1 USD，新资金路径固定费 1 USD，各执行 50%。
- 实际应扣 2 USD；当前展示净额只扣 1 USD。
- 当前 `net_per_base_amount` 甚至不扣这 1 USD，路径排名继续偏高。

修复要求：

- 每条实际执行的转换完整扣除其固定费用。
- 统一净到账和标准化排名值的费用公式，禁止两套口径。
- 覆盖 0%、50%、100% 分批，以及固定费币种无法折算的边界测试。

### P1-06 “净到账达目标”提醒在真实链路中不可达

涉及文件：

- `src/Caishenfolio.Host/Notifications/BackgroundExtendedCheck.cs:134-167`
- `src/Caishenfolio.Host/Notifications/FxAlertRuleEvaluator.cs:220-254`
- `src/Caishenfolio.Host/Notifications/FxAlertRuleEvaluator.cs:22-52`
- `src/Caishenfolio.Desktop/Wealth/PortfolioSettingsWindow.xaml.cs:324-455`
- `src/Caishenfolio.Host/Python/AnalyticsCoreClient.cs:256-298`

问题：

- 后台调用 `PostFxPathsAsync` 时没有传 `costs`。
- Python 对未知成本 fail-closed，返回 `NetAmount=null`。
- `EvaluateNetArrival` 遇到空净额后始终跳过。
- `FxAlertRule` 与设置界面都没有费用模型字段，但提醒文案声称“按你输入的费率模型”。
- 当前端到端测试只验证空账本启动，不覆盖任何 FX 规则。

修复要求：

- 为净到账规则定义并持久化明确的费用模型，或删除该规则直到费用模型可用。
- 不得默认为零费用。
- 增加真实后台链路测试：有外币余额、有报价、有费用、阈值上下各一个案例。

### P1-07 全部外币余额被套用最后一笔购汇成本

涉及文件：

- `src/Caishenfolio.Host/Notifications/BackgroundExtendedCheck.cs:190-216`
- `src/Caishenfolio.Host/Portfolio/FxExchangeHistory.cs`

问题：

代码汇总所有账户的当前外币余额，再使用最后一次购汇的隐含汇率反推全部成本。多批次购汇、部分消费、部分换出、跨账户转账或外部入账后，该成本不再成立，会直接改变路径优势和盈亏平衡提醒。

最小反例：

- 100 CNY 换 2,000 JPY。
- 另 100 CNY 换 2,500 JPY。
- 当前持有 4,500 JPY，实际投入 200 CNY。
- 代码按最后汇率反推为 180 CNY。

修复要求：

- 使用明确且有文档的外币成本法，建议维护剩余批次或移动加权成本。
- 必须处理部分支出、部分换出和非换汇来源；无法归因时返回未知，不得套最后一笔价格。

### P1-08 压力测试重复计算同币种多账户现金

涉及文件：

- `src/Caishenfolio.Host/Portfolio/PortfolioStressAnalyzer.cs:143-156`
- `src/Caishenfolio.Host/Portfolio/PortfolioStressAnalyzer.cs:190-210`

问题：

外层按每个 `CashBalance` 循环，但 `CashBaseIn` 忽略账户和该行金额，返回整个币种的现金总额。存在两个同币种账户时，同一币种总现金会被加入多次。

最小反例：

- 账户 A 有 20,000 CNY，账户 B 有 30,000 CNY，无持仓。
- 币种切片现金总额为 50,000 CNY。
- 当前循环会对两条余额各返回 50,000，压力结果变成 100,000。

修复要求：

- 优先按币种聚合后只计算一次；或使用每条现金余额自身金额和同一估值汇率折算。
- 增加零冲击不变量：任何账户拆分方式下，`StressedTotal == BaselineTotal`。

### P1-09 报价质量被当作支持买入的方向性证据

涉及文件：

- `python/caishenfolio_core/research/evidence_card.py:188-251`
- `python/caishenfolio_core/research/evidence_card.py:700-762`
- `python/caishenfolio_core/research/evidence_card.py:786-837`

问题：

- 两个以上报价源会生成 `stance=positive`，但数据一致只能说明数据质量，不能支持买入。
- 估值历史与财务摘要缺失不属于关键缺失，仍可输出正面结论。
- 任意 `spread > 0` 都被列为冲突，同时又显示“在容差内”，并未真正使用容差。

已验证反例：

- K 线存在，两个价格源完全一致。
- 估值历史缺失，财务摘要缺失。
- 唯一“正面证据”是两个报价源一致。
- 当前结论为 `positive / 证据偏向支持买入`。

修复要求：

- 数据质量证据只能是 `neutral` 或门禁状态，不能计入买卖方向票数。
- 对面向小白的正面/反面结论定义最低证据集合。
- 缺少估值和基本面时，股票证据卡原则上应停在“证据不足”，除非产品明确提供其他可审计方向依据。
- 引入真实报价容差参数并测试 0、阈值内、阈值外。

### P1-10 最大可承受损失没有参与结论门禁

涉及文件：

- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:98-141`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:153-188`
- `src/Caishenfolio.Host/Python/AnalyticsCoreClient.cs:200-238`

问题：

- UI 读取 `maxLossPct` 后没有传给服务端。
- 服务端先生成并返回结论，桌面端只在结论下方追加本地警告。
- 当历史最大回撤超过用户承受能力时，顶部仍可能显示醒目的“支持买入”。

修复要求：

- 将最大损失阈值作为可审计输入贯通 UI、C# 客户端、Python 服务和 Artifact。
- 风险上限应高于方向性结论：超过阈值时输出 `risk_exceeded`，或至少禁止 `positive`。
- 测试结论、说明文字和颜色状态必须一致。

### P1-11 证据卡用请求结束日冒充实际数据日期

涉及文件：

- `python/caishenfolio_core/server/app.py:845-903`
- `python/caishenfolio_core/research/evidence_card.py:782-785`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:195-212`

问题：

服务端把请求参数 `end` 强制作为 `as_of`。周末、停牌、提供方延迟或缓存旧数据时，界面可能显示“数据截至今天”，但最新 K 线和其他通道数据更早。

修复要求：

- 分开表示请求截止日与真实数据日。
- 顶部醒目日期应使用实际可用数据的日期，并展示各数据通道的独立时间戳。
- 若关键通道过旧，进入数据不足或陈旧状态。

### P1-12 跨账户换汇不能通过 CSV 正确往返

涉及文件：

- `src/Caishenfolio.Host/Portfolio/PortfolioReportExporter.cs:104-141`
- `src/Caishenfolio.Host/Portfolio/TransactionCsvImporter.cs:45-50`
- `src/Caishenfolio.Host/Portfolio/TransactionCsvImporter.cs:314-318`
- `src/Caishenfolio.Host/Portfolio/LedgerTransaction.cs:185-205`
- `src/Caishenfolio.Host/Portfolio/PositionCalculator.cs`

问题：

- 领域模型支持 `CounterAccountId`。
- 导出模板没有目标账户列，导入时也没有传入目标账户。
- 跨账户换汇导出再导入后，收到的外币会进入源账户，破坏账户级账本。
- 现有换汇往返测试只覆盖同账户兑换。

修复要求：

- 模板增加目标账户 ID/名称列和别名解析。
- 确定性 ID 必须包含目标账户，避免不同跨账户记录碰撞。
- 增加跨账户导出、导入、重放后两边余额完全一致的测试。

### P1-13 裸证券代码会静默猜错交易所

涉及文件：

- `src/Caishenfolio.Host/Portfolio/SymbolResolver.cs:45-163`
- `python/caishenfolio_core/market/symbol_index.py`

问题：

- 所有字母 ticker 都直接映射为 NASDAQ，因此 IBM、KO、BABA 等 NYSE 标的会被识别错误。
- 四位数字默认映射港股，因此日股 7203 会被识别为 `HKEX:07203`。
- 市场识别错误会连带影响币种、地区、时区、行情源和估值。
- 这与“无法唯一识别时不得猜测”的小白安全原则冲突。

修复要求：

- 优先使用权威标的索引或提供方搜索结果。
- 裸 ticker/数字命中多个市场时返回歧义列表，并要求用户确认。
- 未确认前不得写入账本或启动研究。
- 增加 IBM、KO、BABA、AAPL、7203、0700、00700 等测试。

## 3. P2 问题

### P2-01 同一批次可以对同一流水插入两次冲正

涉及文件：

- `src/Caishenfolio.Host/Portfolio/PortfolioStore.cs:217-312`

问题：

整批冲正在任何插入前统一校验，校验只查询数据库，不检查批内重复目标。两个不同 ID、指向同一原流水的冲正放入同一个 `AddTransactions` 调用时都会通过。

修复要求：

- 检查批内 `ReversesTransactionId` 重复。
- 增加数据库级过滤唯一索引，保证每个原流水最多一条冲正。
- 增加双冲正同批、跨批、并发写入测试。

### P2-02 回撤归因丢弃同一标的的其他账户

涉及文件：

- `src/Caishenfolio.Host/Portfolio/DrawdownAttributionAnalyzer.cs:70-169`
- `src/Caishenfolio.Host/Portfolio/DrawdownAttributionAnalyzer.cs:192-196`

问题：

`BestRow` 按 symbol 只选择市值最大的一条快照。相同标的分布在多个账户时，其他账户变化会消失并被错误吸收到现金残差。

修复要求：

- 按 symbol 聚合全部账户快照。
- 正确合并本币市值、基础币市值和定价完整性。
- 增加同标的双账户、某账户中途清仓、一个账户未定价的测试。

### P2-03 卖出证据卡没有贯通桌面端

涉及文件：

- `python/caishenfolio_core/research/evidence_card.py:765-837`
- `python/caishenfolio_core/server/app.py:867-902`
- `src/Caishenfolio.Host/Python/AnalyticsCoreClient.cs:200-238`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs`

问题：

Python 支持 `side=sell`，但 C# 客户端没有 `side` 参数，请求体也不发送；桌面界面没有买入/卖出选择。因此用户永远只能生成买入证据卡。

修复要求：

- 方向作为显式输入贯通 UI、客户端、服务端、Task、Artifact 和 Audit。
- 卖出卡必须展示税费未知、机会成本、卖后仓位与现金变化。
- 增加 UI/客户端请求序列化测试和卖出端到端测试。

## 4. 测试与质量问题

本轮已执行：

- `dotnet test Caishenfolio.slnx --no-restore`：510 项通过，0 失败。
- Python `unittest discover`：运行 380 项，0 失败，1 项跳过。
- `CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS=1` 的后台扩展检查：1 项通过。
- `scripts\ui_smoke.ps1`：通过；应用成功启动并遍历首页、证据卡、持仓、账本、估值、打新、汇率、风险、总览。
- `git diff --check`：无补丁格式错误，存在 LF→CRLF 警告。

测试输出中的附加问题：

- Python 测试多次出现未关闭数据库连接的 `ResourceWarning`，应修复资源生命周期。
- `requests` 输出 `urllib3/chardet/charset-normalizer` 版本兼容警告，应锁定兼容依赖。
- 现有测试全绿不能证明金融口径正确：至少模拟买入测试固化了错误的 130,000 总资产分母。

## 5. 修复后的强制验收矩阵

修复 Agent 必须补齐并通过以下反例：

1. 使用账本现金买入与外部新增资金买入得到不同、正确的交易后分母。
2. 同币种现金换同币种资产时，货币暴露不应凭空增加。
3. 80/20 组合仅靠新增资金准确回到 50/50。
4. 第二个估值日当天入金，TWR 仍为 0。
5. 部分路径费用未知时，顶层最佳路径与 `diff_vs_best` 完全一致。
6. 两条分批路径各有固定费时，完整扣除两笔费用。
7. 净到账提醒通过真实后台链路触发一次，并在阈值不足时不触发。
8. 两批不同汇率购汇后，持仓历史成本等于实际投入；部分换出后仍正确。
9. 两个同币种现金账户在零冲击压力测试中严格等于基准。
10. 缺少估值和财务数据时，报价源一致不得产生“支持买入”。
11. 历史回撤超过用户最大损失时，顶部不得显示正面结论。
12. 周末请求证据卡时，顶部显示最后真实数据日而非周末日期。
13. 跨账户换汇 CSV 往返后，各账户、各币种余额完全一致。
14. IBM 与 7203 不得被静默解析成错误交易所。
15. 同批双冲正被拒绝。
16. 同一标的分布在两个账户时，回撤归因金额之和仍与总变化守恒。
17. 桌面端能明确生成并显示买入卡和卖出卡。

## 6. 最终完成条件

只有同时满足以下条件，才能提交复审：

- P1-01～P1-13 全部修复。
- P2-01～P2-03 已修复，或在 handoff 中给出经确认的延期理由和隔离措施。
- 上述 17 个反例均有自动化测试。
- 全量 .NET 与 Python 测试通过，无新增跳过。
- 后台换汇提醒真实集成测试不再只是空账本启动。
- UI 冒烟通过，并人工确认正面、冲突、风险超限、证据不足四种视觉状态。
- 所有金融结论都展示口径、数据日期、成本是否完整和“非投资建议”声明。
- `git diff --check` 无实质错误，工作日志与 handoff 已更新。

复审时请让审核 Agent重点核对计算公式和新增反例，不要仅依据现有测试数量或测试全绿作出通过结论。
