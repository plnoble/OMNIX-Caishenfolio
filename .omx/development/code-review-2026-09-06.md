# OMNIX-Caishenfolio 复审验收报告（2026-09-06）

> 复审对象：当前工作区（含未提交修改）  
> 当前版本：`v0.13.3`  
> 复审结论：**不通过。财务计算正确性门禁未通过，暂时不应把本版本交给金融小白作为操作或提醒依据。**

## 1. 给修复 Agent 的执行要求

1. 先阅读根目录 `AGENTS.md`、`.omx/development/current.md`、`.omx/development/handoff.md` 和本报告。
2. 不要因为当前全量测试为绿色就判断问题不存在；本报告中的最小反例已实际复现。
3. 每个问题先增加能够失败的生产路径测试，再修复实现。
4. 不得通过放宽校验、返回 0、隐藏警告、改变文案含义或削弱断言使测试变绿。
5. 金额继续使用 `decimal`；缺报价、缺费用或缺成本时必须 fail-closed，不得假设为零。
6. 不引入实盘下单接口，不在仓库保存长期交易凭据。
7. XAML 或资源改动后，除构建和单测外，必须运行 `scripts\ui_smoke.ps1`。

## 2. 当前验收结果

| 门禁 | 结果 | 说明 |
|---|---:|---|
| `.NET` 全量测试 | 通过 | 541 项通过：Host 520 + Desktop 21 |
| Python 全量测试 | 通过 | 391 项通过，跳过 1 项 |
| `git diff --check` | 通过 | 无空白错误；只有 LF/CRLF 提示 |
| 后台扩展检查 | 表面通过 | 1 项通过，但用例为空账本，未覆盖真实汇率提醒 |
| Windows UI 冒烟 | 通过 | 应用成功启动并遍历主要页面及偏好设置 |
| 财务计算正确性 | **失败** | 下列 P1 反例仍会产生错误结果 |
| 小白发布门禁 | **失败** | 可能输出错误路径、错误风险结论或错误提醒 |

已运行命令：

```powershell
dotnet test Caishenfolio.slnx --no-restore

$env:PYTHONPATH = (Resolve-Path "python").Path
$env:CAISHENFOLIO_MARKET_MODE = "fixture"
python -m unittest discover -s tests/python -p "test_*.py"

$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS = "1"
dotnet test tests\Caishenfolio.Host.Tests --no-restore --filter BackgroundExtendedCheckIntegrationTests

scripts\ui_smoke.ps1
```

Python 测试过程中仍出现多条 `ResourceWarning: unclosed database`，并出现 `requests` 依赖版本不匹配警告；见 P2。

---

## 3. 阻塞发布的 P1 问题

### P1-R01 跨币种买入模拟错误地抵消目标币种暴露

涉及文件：

- `src/Caishenfolio.Host/Portfolio/TradeWhatIfAnalyzer.cs:117-126`
- `tests/Caishenfolio.Host.Tests/PortfolioAnalysisN5Tests.cs:158-170`

当前公式：

```csharp
var currencyAfter = funding == FundingSource.ExistingCash
    ? currencyNow + amountBase - (currencyExposure == baseCcy
        ? amountBase
        : Math.Min(amountBase, CurrencySliceAmount(valuation, baseCcy)))
    : currencyNow + amountBase;
```

当用户使用 CNY 现金买入 USD 资产时，目标币种是 USD，但代码又从 USD 分子里扣除了 CNY 现金减少额。因此，常见情况下会得到：

```text
USD 新暴露 = USD 原暴露 + 买入额 - CNY 现金减少额 = USD 原暴露
```

即买入 USD 资产后，USD 占比可能完全不增加。

现有测试只覆盖“CNY 现金买 CNY 股票时 CNY 暴露不变”，没有覆盖跨币种情形。

修复要求：

- 目标币种暴露和资金来源币种暴露必须分别更新。
- 同币种现金转资产时，该币种总暴露不变。
- 跨币种买入时，目标币种暴露增加，资金来源币种暴露减少。
- 不得从目标币种分子中直接扣除其他币种的现金。
- 如果模拟无法确认究竟使用哪种现金，应显式要求资金币种或 fail-closed。

必须增加测试：

1. CNY 现金买 USD 资产，USD 权重正确增加。
2. CNY 现金买 JPY 资产，JPY 权重正确增加。
3. CNY 现金买 CNY 资产，CNY 总暴露不变。
4. 外部新增资金买 USD 资产，总资产分母和 USD 暴露均正确增加。
5. 只有其他币种现金、没有足够资金币种现金时不得误报“可行”。

### P1-R02 最大可承受损失存在百分比/小数单位错配

涉及文件：

- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml:54-58`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:109-114,139-156`
- `python/caishenfolio_core/research/evidence_card.py:876-900`
- `tests/Caishenfolio.Host.Tests/AnalyticsCoreClientTests.cs:173-205`
- `tests/python/test_evidence_card.py:519-549`

UI 明确告诉用户“20 表示 20%”，C# 解析后把 `20` 原样发送给 Python。Python 却把门限当成比例值，历史回撤先除以 100，再与门限比较：

```python
behaviour_dd = abs(max_drawdown_pct) / 100
if behaviour_dd >= max_loss_pct:
    risk_exceeded = True
```

实测结果：

```text
历史最大回撤：-60%
UI 输入：20（即 20%）
服务端收到：20
比较：0.60 >= 20 -> False
最终结论：positive
```

只有直接向服务端传入 `0.2` 才能触发 `risk_exceeded`。现有客户端测试也直接传 `0.15`，绕过了真实 UI 单位。

修复要求：

- 明确唯一契约：建议 API 内部统一使用 `0~1`，UI 显示 `0~100%`。
- C# 在请求前将 UI 输入除以 100；或统一调整服务端，但不得同时存在两种口径。
- UI 必须校验范围 `0 < x <= 100`。
- API 边界必须拒绝无法解释的值，不得静默猜单位。

必须增加测试：

1. 通过桌面输入转换函数验证 `20 -> 0.2`。
2. UI 契约端到端测试：60% 回撤、用户输入 20%，结论必须为 `risk_exceeded`。
3. 输入 0、负数、超过 100、非数字时有明确校验结果。

### P1-R03 最大损失超限时显示了错误原因

涉及文件：

- `python/caishenfolio_core/research/evidence_card.py:728-748`

`risk_exceeded` 同时承载“单一持仓超限”和“历史回撤超过最大损失”两类风险，但 `conclude()` 无条件输出：

```text
这笔候选投入会突破单一持仓限制。
```

实测以 `max_loss_pct=0.2` 正确触发历史回撤门禁后，顶部仍声称是单一持仓超限。这会让小白采取错误的修正动作，例如只减少买入金额，而没有理解标的本身的历史波动超过承受能力。

修复要求：

- 不要用一个无类型布尔值表示所有风险。
- 返回结构化风险原因，例如 `position_limit`、`max_loss_history`、`cash_shortfall`。
- 结论摘要和原因必须由实际触发项生成；多项同时触发时全部列出。

必须增加测试：

1. 仅持仓上限触发时只说明持仓上限。
2. 仅最大损失触发时只说明历史回撤与用户门限。
3. 两者同时触发时同时列出，且不遗漏具体数值。

### P1-R04 分批换汇的排名值量纲错误

涉及文件：

- `python/caishenfolio_core/research/fx_paths.py:501-565`
- `python/caishenfolio_core/research/fx_paths.py:611-650`
- `tests/python/test_fx_paths.py:406-448`

`from_held` 和 `from_base` 已经按照 `split_fraction` 拆分，但归一化代码又乘了一次比例，并且除以完整历史成本/完整新增金额：

```python
per_base_split += held_leg / held.cost_in_base * frac
per_base_split += base_leg / amount * (1 - frac)
```

这个值会直接参加 `best_path` 和 `diff_vs_best` 排名。

已复现：

```text
50/50 分批换汇真实净到账：13.0915246505 USD
net_per_base_amount：        0.0654576233
```

其他同规模路径的排名值约为 `13~15 USD`，因此分批路径被缩小约 200 倍，几乎不可能胜出。

现有测试只断言该值小于毛额，没有断言它与实际净到账的量纲和数值一致。

修复要求：

- 定义并写清 `net_per_base_amount` 的单位。
- 所有路径在相同 `base_amount` 下必须返回可直接比较的目标币金额。
- 分批路径不得重复应用 `split_fraction`。
- `net_amount`、`net_per_base_amount`、`best_path`、`diff_vs_best` 必须使用同一成本公式。

必须增加测试：

1. 50/50、无费用时，归一值与对应分批到账结果一致。
2. 50/50、两边各 1 USD 固定费时，归一值约为 `13.091525`，而不是 `0.065...`。
3. 构造分批路径真实最优的反例，断言 `best_path == "split"`。
4. `split_fraction` 为 0 和 1 时，结果分别退化为单一路径。

### P1-R05 两段换汇仅填写固定费时没有扣费

涉及文件：

- `python/caishenfolio_core/research/fx_paths.py:402-438`
- `python/caishenfolio_core/research/fx_paths.py:691-722`

两段路径在 `multiplicative_fraction is None` 且存在固定费用时直接执行：

```python
net3 = gross3
detail3 = {"status": "仅固定费用（比例成本未填）"}
```

固定费用没有折算，也没有从净额中扣除。

已复现：

```text
两段路径毛额：14.8676776687 USD
固定费：       1 USD
返回净额：     14.8676776687 USD
```

同时，`PathCost` 文档规定未填写的成本字段表示“未知而不是 0”，但单段路径和两段路径对“仅固定费”的处理并不一致。

修复要求：

- 先确定统一语义：仅填写固定费时，百分比成本究竟是显式 0 还是未知。
- 推荐由 UI/API 明确传入 `spread_pct=0` 表示用户确认无点差；缺失则 fail-closed。
- 无论采用哪种契约，两段路径必须实际扣除固定费，且所有路径保持一致。
- 若固定费币种无法换算到目标币，保持 `gross_only`，不得按 0 处理。

必须增加测试：

1. 两段路径固定费 1 USD 后，净额严格等于毛额减 1 USD（若费用定义为每条路径一次）。
2. 单段、两段、分批对“仅固定费”的状态与金额语义一致。
3. 固定费币种不可换算时所有路径 fail-closed。

### P1-R06 后台汇率提醒缓存复用了错误的费用模型

涉及文件：

- `src/Caishenfolio.Host/Notifications/BackgroundExtendedCheck.cs:125-182,199-203`
- `src/Caishenfolio.Host/Notifications/FxAlertRuleEvaluator.cs:232-276`
- `tests/Caishenfolio.Host.Tests/BackgroundExtendedCheckIntegrationTests.cs:52-63`

`BackgroundExtendedCheck` 按以下键缓存 `FxPathFacts`：

```text
base_currency / target_currency / held_currency
```

缓存键没有包含：

- 规则类型；
- 点差；
- 固定费；
- 阈值对应的费用模型。

因此，同一货币组合上的第二条规则会直接复用第一条规则生成的净到账。

典型错误：

1. `HeldAdvantageAbove` 排在前面且没有成本模型；
2. `NetArrivalAbove` 排在后面并填写了点差/固定费；
3. 第二条规则复用第一条的无成本事实，可能得到 `NetArrival=null`，永远不触发；
4. 两条净到账规则费用不同，也会共用第一条费用。

当前所谓“真实后台链路”测试只创建空账户，调用一次检查后断言返回值非空；没有外币余额、没有汇率规则，也没有断言提醒触发或不触发，因此没有覆盖 P1-06 的验收要求。

修复要求：

- 优先缓存与规则无关的原始报价/持仓事实，再针对每条规则独立计算成本。
- 如果继续缓存路径结果，键必须包含完整成本模型和所有影响结果的参数。
- 为费用模型实现稳定、明确的值语义，不使用不完整字符串拼接造成碰撞。

必须增加真实生产链路测试：

1. 创建有成本的 JPY 持仓和两条同币种不同类型规则。
2. 无费用模型的优势规则不得污染有费用模型的净到账规则。
3. 两条不同固定费的净到账规则得到不同净额。
4. 净额达到阈值时真实触发；差一点未达到时不触发。

### P1-R07 外币持仓成本仍未覆盖消费、提现与手续费

涉及文件：

- `src/Caishenfolio.Host/Portfolio/FxExchangeHistory.cs:143-226`
- `src/Caishenfolio.Host/Portfolio/PositionCalculator.cs:80-99`
- `tests/Caishenfolio.Host.Tests/FxExchangeHistoryTests.cs`

`HeldCurrencyCostCalculator` 只读取 `FxExchangeHistory.Records()`，因此不处理外币的：

- `Withdraw`；
- 独立 `Fee` / `Tax`；
- 其他会减少当前外币余额的合法流水。

此外，换出外币时账本扣除：

```csharp
txn.CashAmount + txn.Fee
```

但成本计算器只释放：

```csharp
var released = record.FromAmount;
```

最终对账逻辑只在 `currentBalance > calculatedBalance` 时补“未知余额”，不处理当前余额更小的情况。

最小反例：

```text
100 CNY 买入 2,000 JPY
随后支出/提现 1,000 JPY
当前余额应为 1,000 JPY，成本约 50 CNY
当前计算仍可能保留 2,000 JPY、成本 100 CNY
```

这会把真实成本率从约 20 JPY/CNY 错算成 10 JPY/CNY，直接改变路径优势、盈亏平衡和提醒触发。

修复要求：

- 成本池必须按完整、按时间排序的现金流水变化维护，而不是只看换汇记录。
- 换出时把源币手续费纳入释放数量。
- 普通外币消费/提现按移动平均成本释放。
- 外部直接入账或无法解释的余额进入 `UnpricedAmount`，不得套用已有成本。
- 跨账户转账如果只改变账户位置、不改变总币种余额，不应产生或释放成本。

必须增加测试：

1. 多批次购汇后的移动加权平均。
2. 部分换出并包含源币手续费。
3. 外币提现/消费后按比例释放成本。
4. 外部直接入账形成未定价池。
5. 定价池与未定价池混合后部分支出。
6. 跨账户转账不改变全局成本。

### P1-R08 再平衡的 0% 目标在真实设置路径中失效

涉及文件：

- `src/Caishenfolio.Host/Portfolio/PortfolioSettings.cs:113-130`
- `src/Caishenfolio.Host/Portfolio/RebalanceComparisonAnalyzer.cs:68-121`
- `tests/Caishenfolio.Host.Tests/PortfolioAnalysisN5Tests.cs:814-823`

设置校验会删除所有权重为 0 的目标：

```csharp
if (weight > 0m)
{
    targets[asset.ToCode()] = weight;
}
```

再平衡分析器又只遍历 `targets` 中存在的键。因此用户设置：

```text
股票 0%
现金 100%
```

保存后只剩 `cash=1`。分析器看不到股票目标为 0，不会生成清空股票的卖出计划，并可能声称“没有需要卖出的超配品种”。

现有“零目标清仓”单元测试直接向分析器传入 `equity=0`，绕过了 `PortfolioSettings.Validated()` 和保存/加载生产路径。

修复要求：

- 方案 A：保留用户显式填写的零目标；或
- 方案 B：分析器对“当前存在品种”和“目标品种”取并集，缺失目标明确解释为 0%。
- 必须区分“用户没有配置目标”和“用户明确配置为 0%”。

必须增加测试：

1. 设置 `equity=0, cash=1`，经过 `Validated -> Save -> Load -> Compare` 后必须生成卖出全部股票的方案。
2. 完全未配置目标仍应阻止计算，而不是把全部品种默认为 0。

### P1-R09 证据卡把不同格式、不同新鲜度的日期混成一个顶部日期

涉及文件：

- `python/caishenfolio_core/research/evidence_card.py:232-255,823-827`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:210,225-227`
- `tests/python/test_evidence_card.py:551-559`

频道日期可能包括：

```text
bars:       2025-07-19
quote:      2024-12-30
valuation:  2025-12-31
financials: 2026Q1
```

代码直接执行字符串 `max(actual_dates)`，已实测顶部 `as_of` 变成：

```text
2026Q1
```

问题不只是格式：取“最晚频道日期”并显示为“数据截至”还会掩盖其他关键频道已经过期。最新报价不能让用户误以为旧财报和旧估值也更新到了今天。

现有测试只验证请求结束日在周末时不冒充 K 线日期，没有覆盖多频道不同步和报告期格式。

修复要求：

- 财务报告期与行情日期分字段表达，不做字符串大小比较。
- 对 ISO 日期进行严格解析，无法解析的值不得进入日期排序。
- 顶部建议显示“行情截至 X；估值截至 Y；财务期 Z”，或使用最保守的共同截止日并明确说明。
- 频道明显不同步时增加数据质量警告，不能只显示最新日期。

必须增加测试：

1. `2026Q1` 不得成为 ISO 数据截止日期。
2. 行情今天、估值一年前时，顶部不得笼统声称全部数据截至今天。
3. 无法解析的频道日期 fail-closed，并保留原始 provenance。

### P1-R10 北交所裸代码被识别为场外基金

涉及文件：

- `src/Caishenfolio.Host/Portfolio/SymbolResolver.cs:160-184`
- `src/Caishenfolio.Host/Data/ExchangeRegistry.cs:25-38`
- `python/caishenfolio_core/data/markets.py:270-320`
- `tests/Caishenfolio.Host.Tests/SymbolResolverTests.cs`

项目已经正式支持 `BSE`，但 C# 裸代码规则只识别：

- `6` 开头：SSE；
- `0/3` 开头：SZSE；
- 其他六位代码：FUND。

因此典型北交所代码会被错误解析：

```text
830799 -> FUND:830799
430047 -> FUND:430047
872925 -> FUND:872925
920819 -> FUND:920819
```

这会连带污染资产类别、行情通道、交易日历、币种和风险分析。代码注释声称“4/8 为北京”，但实现没有对应分支。

修复要求：

- 不要在解析器里继续复制市场规则；提取 C#/Python 一致的中国证券代码映射。
- 至少覆盖项目 Python 侧已有的 BSE 前缀语义。
- 如果代码可能同时是基金与证券，不得静默猜测；优先使用标的表唯一命中，否则返回歧义。

必须增加测试：

1. `430047`、`830799`、`872925`、`920819` 的 BSE 解析。
2. 已存在标的表唯一命中时正确消歧。
3. 真正有歧义时 `Resolved=null`，并列出候选市场。

---

## 4. P2 问题

### P2-R01 冲正缺少数据库级唯一约束

涉及文件：

- `src/Caishenfolio.Host/Portfolio/PortfolioStore.cs:278-323,1102-1109`

本轮已经增加批内重复检查和写入前查询，这是有效改进。但数据库迁移仍然只创建普通索引：

```sql
CREATE INDEX IF NOT EXISTS ix_transactions_reverses
ON transactions(reverses_transaction_id);
```

没有保证一个原流水最多只能被冲正一次。多个 `PortfolioStore` 实例、未来新增写入路径或数据库旁路仍可能破坏该不变量。

修复要求：

- 增加数据库迁移和过滤唯一索引，空字符串不参与唯一约束。
- 迁移前检查既有重复数据，并给出明确失败信息，不得静默删除。
- 将 SQLite 唯一约束异常转换成用户可理解的 `LedgerException`。

建议形式：

```sql
CREATE UNIQUE INDEX ...
ON transactions(reverses_transaction_id)
WHERE reverses_transaction_id <> '';
```

必须增加测试：两个独立 `PortfolioStore` 实例指向同一数据库，尝试重复冲正时数据库最终只允许一条。

### P2-R02 证据卡的地区/币种组合影响没有真正贯通

涉及文件：

- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:116-154`
- `python/caishenfolio_core/research/evidence_card.py:570-648`

桌面端只发送组合总额、现金和单一持仓权重，没有发送地区权重、币种权重或标的真实地区。Python 即使收到权重，也只是原样返回，没有根据候选交易生成“交易后”权重。

因此“组合影响（交易前后）”目前主要是单一持仓与现金影响，不是完整的地区/币种仓位分析。

修复要求：

- 明确该卡片是否承诺地区/币种影响；如果承诺，桌面端必须提供真实资产语义，服务端必须计算交易后值。
- 不得用组合本位币冒充标的计价币种。
- 无法取得资产地区或币种时显示未知，不返回空字典伪装成没有影响。

### P2-R03 Python 测试存在数据库资源泄漏和依赖警告

全量 Python 测试虽然通过，但输出了多条：

```text
ResourceWarning: unclosed database
```

同时 `requests` 报告 `urllib3` 与字符检测依赖版本不匹配。短测试中可能不失败，但长期运行可能造成句柄、文件锁和连接资源累积。

修复要求：

- 定位所有 SQLite 连接的所有权，确保成功、异常和测试清理路径都会显式关闭。
- 对数据库生命周期增加重复调用/异常路径测试。
- 固定兼容的 Python 依赖版本，并在干净环境重新安装验证。

---

## 5. 已确认有效的改进，修复时不得回退

以下部分相较上一轮确有改进：

- 全量 .NET 与 Python 测试均能通过。
- 桌面应用能够正常启动，BAML 加载和主要页面导航通过。
- 存量现金与外部新增资金的总资产分母已经拆分。
- 同币种现金买入同币种资产的暴露口径已有测试。
- TWR 当日现金流处理增加了工作区测试。
- 分批换汇的实际 `net_amount` 已能扣除两条已执行路径的固定费。
- 净额/毛额顶层比较口径已有统一处理。
- 报价一致性不再直接投票支持买入。
- CSV 已增加跨账户换汇的对方账户字段。
- 同批次双冲正已经增加批内拒绝检查。
- 回撤归因已按同一标的聚合多个账户。
- 卖出证据卡已经贯通桌面端。
- 压力测试的同币种多账户现金重复计算问题已有修正。

这些改进不能抵消本报告的 P1，但修复时应保持现有行为和测试。

## 6. 修复后必须通过的反例矩阵

修复 Agent 完成后，至少提交以下自动化反例映射：

1. CNY 现金买 USD 资产，USD 暴露增加。
2. 同币种现金买资产，该币种总暴露不变。
3. UI 输入最大损失 20%，服务端收到 0.2。
4. 60% 历史回撤遇 20% 上限，顶部结论为风险超限。
5. 最大损失触发时原因不得写成单一持仓超限。
6. 50/50 分批净到账 13.09 时，排序值不得变成 0.065。
7. 构造分批路径最优样例，顶层 `best_path` 必须正确。
8. 两段路径仅固定费时实际扣费，或按统一未知成本契约 fail-closed。
9. 同币种组合的两条不同费用提醒分别计算。
10. 净到账提醒通过真实后台链路触发一次，并在阈值不足时不触发。
11. 外币部分提现后，持有数量和成本同比下降。
12. 换出外币并支付源币手续费时，手续费计入释放量。
13. `equity=0, cash=1` 经过设置保存/加载后生成清仓计划。
14. 多频道日期不同步时不显示误导性的统一最新日期。
15. `2026Q1` 不得作为 ISO 日期出现在“数据截至”。
16. 北交所四个代表代码不会落到 `FUND`。
17. 两个 Store 实例不能为同一流水各插入一条冲正。
18. 证据卡能显示候选交易后的币种/地区变化，或明确标为未知。
19. Python 全量测试不再输出未关闭数据库警告。

## 7. 重新验收命令

```powershell
git diff --check

dotnet build Caishenfolio.slnx --no-restore
dotnet test Caishenfolio.slnx --no-restore

$env:PYTHONPATH = (Resolve-Path "python").Path
$env:CAISHENFOLIO_MARKET_MODE = "fixture"
python -W error::ResourceWarning -m unittest discover -s tests/python -p "test_*.py"

$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS = "1"
dotnet test tests\Caishenfolio.Host.Tests --no-restore --filter BackgroundExtendedCheckIntegrationTests

scripts\ui_smoke.ps1
```

还必须人工验证以下四种证据卡视觉状态，而不只是确认页面能打开：

1. 证据偏正面；
2. 证据冲突；
3. 风险超限；
4. 证据不足。

人工检查风险超限状态时，要确认顶部原因与真正触发的风险一致，百分比单位正确，频道日期没有制造“全部数据都很新”的错觉。

## 8. Definition of Done

满足以下全部条件后才能再次申请验收：

- P1-R01～P1-R10 全部修复。
- P2-R01～P2-R03 已修复，或在 handoff 中给出经确认的延期理由、隔离措施和不影响小白用户的证据。
- 第 6 节反例均有自动化测试，且测试经过真实生产调用路径。
- .NET、Python、扩展检查和 UI 冒烟全部通过。
- Python 不再出现未关闭数据库的资源警告。
- `.omx/development/current.md`、`quality-gates.md` 和 `handoff.md` 的状态、测试数量和实际结果一致。
- 不得继续宣称“P1×13 + P2×3 全清”，除非本报告的问题也已逐项关闭并提供测试映射。
- 软件仍保持研究/模拟定位，不输出确定性收益承诺，不接入实盘下单。

