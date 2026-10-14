# OMNIX-Caishenfolio 整改复审报告

> 日期：2026-09-06  
> 版本：`v0.13.3 / R5`  
> 审核依据：`.omx/development/remediation-plan-2026-09-06.md`  
> 审核范围：整改计划 WP1～WP6、生产调用链、自动化测试、后台真实链路、桌面启动冒烟  
> 结论：**暂不通过，仍有 4 项 P1 阻塞问题。**

## 1. 给修复 Agent 的要求

1. 开始前阅读 `AGENTS.md`、`.omx/development/current.md`、`.omx/development/handoff.md`、整改计划和本文。
2. 每个问题先补一个会经过真实生产调用链的失败反例，再修改实现。
3. 不得通过放宽断言、隐藏输出、把空值改成 `0` 或修改状态文案绕过问题。
4. 金额继续使用 `decimal/Money`；缺少费用、汇率、成本或权重切片时 fail-closed。
5. 不得新增实盘下单能力，不得在仓库中保存长期券商或交易所凭据。
6. 修复后更新 `current.md`、`handoff.md`、`quality-gates.md`、`worklog.md` 和相应反思/错误记录。

## 2. 验收摘要

本轮整改的内核方向基本正确：费用三态、逐笔外币成本、生产百分比解析器、分批性质测试和分频道日期结构均有实际改进。但是，若干测试只直接调用了 Python/C# 内核，没有覆盖桌面端到服务端的生产接线，导致自动化全绿时仍存在用户可见的错误。

当前不得声明“WP1～WP6 全部完成”或“15 条矩阵 14 条通过”。至少 WP1、WP2、WP4 和 WP6 的交付证据仍不完整。

## 3. P1 阻塞问题

### P1-01：证据卡生产请求没有传递资金来源币种

涉及文件：

- `src/Caishenfolio.Host/Python/AnalyticsCoreClient.cs`
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs`
- `python/caishenfolio_core/research/evidence_card.py`

Python 的现有现金买入算法需要 `funding_currency`：

```text
资金来源币种权重 -= candidate_weight
标的计价币种权重 += candidate_weight
```

但 `PostEvidenceCardAsync` 的参数和请求字典中都没有 `funding_currency`，桌面端调用也没有传入该值。Python 最终得到空字符串，默认的 `existing_cash` 路径会返回空的 `currency_weight_after`，并提示缺少资金来源币种。

直接调用 Python 的测试之所以通过，是因为测试手工注入了 `funding_currency="CNY"`，没有经过真实 C# 请求链。

要求：

1. 给 `PostEvidenceCardAsync` 增加明确的 `fundingCurrency` 参数并序列化为 `funding_currency`。
2. 桌面端根据实际资金来源传值；当前仅支持以本位币金额模拟时，至少明确传入 `portfolioCurrency`。
3. 客户端序列化测试必须断言该字段存在且值正确。
4. 增加 C# 请求 → Python 路由 → 卡片结果的集成反例，不能只直接调用 `assemble_card`。

完成标准：默认“现有 CNY 现金买 USD 资产”在真实请求链中返回 CNY 下降、USD 上升，币种权重合计为 1。

### P1-02：卖出超持仓时现金占比与地区权重不一致

涉及文件：

- `python/caishenfolio_core/research/evidence_card.py`
- `tests/python/test_evidence_card.py`

当前 `_sell_impact` 会把卖出权重限制为当前持仓：

```text
candidate_weight = min(requested_weight, current_position_weight)
```

但现金占比仍按用户原始输入金额增加：

```text
cash_after = (cash_available + amount_in_base) / portfolio_value
```

已复现：

```text
组合总额：100,000
现金：50,000（50%）
该标的持仓：20%
请求卖出：90,000（90%）

当前结果：
实际卖出权重 = 20%
地区 cash = 70%
cash_weight_after = 140%
```

此外，卖出分支直接复制 `region_weights`，没有像买入分支一样验证初始合计是否为 1。输入地区权重合计 1.2 时仍会输出合计 1.2 的交易后结果。

要求：

1. 先得到实际可卖金额/权重，再让持仓、现金和地区变化全部使用同一个实际值。
2. 卖出分支复用统一的输入校验和输出不变量检查。
3. 对所有成功输出断言：每个权重有限且非负、合计为 1、`cash_weight_after == region["cash"]`。
4. 不得用 `max(..., 0)` 掩盖不一致输入；无法证明时返回未知并解释原因。

必须增加的反例：

- 请求卖出 90%，实际只持有 20%。
- 卖出时初始地区合计为 1.2。
- 标的地区切片小于该标的持仓权重。
- 正常部分卖出后现金、地区、币种三者一致。

### P1-03：汇率路径页面无法提供完整费用模型，净额实际上不可达

涉及文件：

- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml`
- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs`
- `src/Caishenfolio.Host/Python/AnalyticsCoreClient.cs`
- `python/caishenfolio_core/research/fx_paths.py`

整改后的费用契约要求：

```text
比例成本：空白=未知，0=确认没有，正数=实际输入
固定费：空白=未知，0=确认没有，正数=实际输入
```

但路径比较页面只提供三个“比例成本%”输入框，没有固定费和固定费币种输入。C# 请求只设置 `SpreadPercent`，`FixedFee` 永远为 `null`。因此即使用户把比例成本明确填写为 `0`，Python 仍会按固定费未知返回：

```text
status = gross_only
net_amount = None
```

也就是说，当前页面没有任何用户操作可以得到“净到账已计入成本”的结果。

同时，`TryParseOptional` 对非法输入返回 `false` 后，调用方只是忽略该成本字段并继续请求，没有给用户明确的输入错误。

要求：

1. 每条可换汇路径提供比例成本、固定费和固定费币种；或设计一个明确且可解释的共享费用模型。
2. 空白与显式 `0` 必须可以分别保存并序列化。
3. 非数字、负数或不可折算币种必须在提交前给出明确错误。
4. 单段、两段和分批路径继续使用同一费用语义。
5. 增加桌面表单 → C# DTO → Python 路由的集成测试。

完成标准：用户显式填写“比例成本 0、固定费 1 USD”时，页面能显示 `净额 = 毛额 - 1 USD`；只填固定费或只填比例成本时显示未知并说明缺项。

### P1-04：“按账本填入”仍使用最后一笔换汇汇率

涉及文件：

- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs`
- `src/Caishenfolio.Host/Portfolio/FxExchangeHistory.cs`
- `src/Caishenfolio.Host/Notifications/BackgroundExtendedCheck.cs`

新的 `HeldCurrencyCostCalculator` 已正确支持：

- 多批购汇移动加权；
- 部分提现/消费按比例释放；
- 外部入账、利息和来源不明流入进入未知池；
- 同日无关流水不再自动抵消。

后台提醒已经使用该计算器，但汇率页面的“按账本填入”仍调用 `MostRecentAcquisition`，并把全部当前余额套用到最后一笔汇率。

反例：

```text
100 CNY -> 2,000 JPY
100 CNY -> 2,500 JPY
当前余额 4,500 JPY

真实投入：200 CNY
当前页面：4,500 / 25 = 180 CNY
```

更严重的情况是余额中包含外部入账：页面仍可能把未知成本按最后汇率伪装成已知成本。

要求：

1. 汇率页面与后台提醒复用 `HeldCurrencyCostCalculator.CostOf`。
2. `BlendedCostInBase == null` 时保持成本输入为空，并明确展示“余额含未知成本部分”。
3. 不再把 `MostRecentAcquisition` 用作整笔当前余额的成本。
4. 增加两批购汇、部分提现、外部入账和同日无关流水的桌面预填测试。

## 4. P2 问题

### P2-01：组合影响界面仍未达到 WP1 展示要求

`EvidenceCardView.RenderImpact` 当前只显示：

- 单一标的交易前/交易后占比；
- 交易后现金占比；
- 交易后地区分布。

仍缺少：

- 地区分布交易前 → 交易后；
- 币种分布交易前 → 交易后；
- 现金占比交易前 → 交易后；
- 使用的资金来源币种；
- 对缺失字段的就地解释。

后端 DTO 目前也只返回 `region_weight_after` / `currency_weight_after`，没有完整的 before 结构，无法满足既定界面验收。

要求：补齐结构化 before/after 字段和桌面渲染测试。`ui_smoke.ps1` 只验证导航到证据卡页面，不能替代生成卡片并检查文字。

### P2-02：不可解析日期没有异常警告

已复现：

```text
quote.as_of = "garbage"
channel_as_of.quote = "garbage"
warnings = []
```

当前代码只是把不可解析日期排除在顶部日期比较之外，仍将原值交给桌面展示，没有按整改计划要求标记“日期格式异常”。

要求：保留原始 provenance，同时在数据质量中加入频道名和原始值，桌面端不得把异常字符串当作正常日期展示。

### P2-03：财务陈旧度没有按季度计算

代码注释称“落后 5 个季度”应提示，但实现只计算年份差：

```text
years_behind = market_year - financial_year
if years_behind >= 2: warning
```

反例：最新行情 `2026-12-31`、财务期 `2025Q1` 已相差约七个季度，但年份差只有 1，因此没有财务陈旧警告。

要求：解析 `YYYYQn` 为连续季度序号，例如 `year * 4 + quarter`，再按明确阈值判断；无法解析的报告期同样标记格式异常。

### P2-04：应用 managed venv 的全量测试并未通过

普通系统 Python 未安装 `pypinyin` 时，425 项测试通过；应用 managed venv 安装了 `market` extra，其中包含 `pypinyin`，测试结果为：

```text
Ran 425 tests in 3.154s
FAILED (failures=1, skipped=1)
```

失败测试：

```text
test_an_unknown_name_without_pypinyin_yields_no_pinyin
```

测试名称要求“没有 pypinyin”，但测试没有 mock 或注入该条件。生产 venv 中 `pypinyin` 必然存在，因此未知名称会被正常转成拼音，断言 `None` 必然失败。

要求：

1. 显式 mock `_pypinyin_converter()` 返回 `None`，或为 `pinyin_of` 增加可明确表示“禁用自动转换器”的注入方式。
2. 分别测试有转换器和无转换器两种环境。
3. 正式 Python 门禁必须使用应用 managed venv，不得只使用 PATH 上偶然缺少 extras 的系统 Python。

### P2-05：质量门禁记录存在过时和过度声明

`.omx/development/quality-gates.md` 前文仍引用已经不存在的 `test_a_cheapest_split_wins_the_comparison`；后文虽然做了更正，但同一文档内互相矛盾。

矩阵第 11 条声称“桌面 headline 渲染”已经验收，但当前没有桌面渲染测试，UI 冒烟只完成页面导航。

要求：删除或明确标记过时章节，门禁证据只引用当前真实存在且实际执行的测试。

## 5. 已验证通过的部分

- `PathCost` 已区分未知、显式零和正数。
- 单段 `_apply_costs` 在比例成本或固定费未知时会返回 `gross_only`。
- 净到账提醒要求比例成本与固定费都显式填写。
- `HeldCurrencyCostCalculator` 已取消同日净额推断，逐笔重放反例正确。
- 后台提醒已经使用移动加权外币成本。
- `market_as_of` 不再让 `2026Q1` 参与 ISO 日期排序。
- `PercentInputParser` 是桌面端和测试共用的生产实现。
- 原“分批最优”错误测试已改为线性模型下的正确性质。
- 应用 managed venv 中 `requests 2.34.2 / urllib3 2.7.0 / charset-normalizer 3.4.9` 直接导入无告警。

## 6. 本轮实际执行结果

### .NET

```text
dotnet build Caishenfolio.slnx
成功：0 警告，0 错误

dotnet test Caishenfolio.slnx --no-build
Host：549/549
Desktop：30/30
合计：579/579
```

### Python

PATH 上的系统 Python：

```text
python -W error -m unittest discover -s tests/python -p "test_*.py"
425 通过，1 skip
```

但该环境没有覆盖应用实际安装的全部 extras；直接 `import requests` 还会因系统中额外安装的 `chardet` 版本产生 `RequestsDependencyWarning`。这属于系统解释器污染，不是应用 managed venv 的产品缺陷，也说明不应把系统 Python 当正式门禁。

应用 managed venv：

```text
requests / urllib3 / charset-normalizer 导入无告警
全量测试：424 通过、1 失败、1 skip
```

### 后台与 UI

```text
BackgroundExtendedCheckIntegrationTests：2/2 通过
scripts/ui_smoke.ps1：通过
```

UI 冒烟验证了窗口、版本号、九页导航和设置窗口加载；没有生成证据卡或执行汇率路径比较，因此不能覆盖本文中的 UI 接线问题。

### Git 卫生

```text
git diff --check：退出码 0
```

仅有 LF/CRLF 转换提示，没有 whitespace error。

## 7. 推荐修复顺序

1. **先修 P1-01 与 P1-02**：统一组合权重输入/输出契约和守恒检查。
2. **再修 P1-03 与 P1-04**：让汇率页能够输入完整成本，并与后台共用外币成本事实。
3. **完成 P2-01**：补齐 before/after DTO 与实际桌面展示。
4. **修复 P2-02/P2-03**：日期格式和季度陈旧度。
5. **修复 P2-04/P2-05**：确保真实 venv 全绿并清理质量门禁记录。
6. 最后执行本文第 9 节全部命令和第 10 节人工 UI 验收。

## 8. 必须新增或加强的自动化测试

1. C# 客户端请求序列化包含 `funding_currency=CNY`。
2. 真实 C#→Python 证据卡链路计算 CNY→USD 权重变化并守恒。
3. 卖出超过持仓时只使用实际可卖金额，现金占比不超过合理范围。
4. 卖出输入地区/币种合计不为 1 时 fail-closed。
5. 所有成功权重输出满足非负、有限、合计为 1、现金字段一致。
6. 汇率页面显式 `0` 比例成本与 `0` 固定费能够得到净额。
7. 汇率页面只填一种费用时保持 `gross_only` 并指出缺失字段。
8. 两批购汇的页面预填成本使用真实总投入 200 CNY，而不是最后汇率推导的 180 CNY。
9. 外币余额含未知成本时页面不得自动填入完整成本。
10. 桌面证据卡渲染断言地区、币种、现金的 before/after 文字均存在。
11. 不可解析的行情日期和财务报告期产生频道级异常警告。
12. 财务落后超过设定季度阈值时警告，跨年边界计算正确。
13. `pypinyin` 可用与不可用两种测试均确定性通过。
14. managed venv 下直接导入 requests 链并把警告视为错误。

## 9. 重新验收命令

```powershell
dotnet build Caishenfolio.slnx
if ($?) { dotnet test Caishenfolio.slnx --no-build }

$appPython = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    'Caishenfolio\state\.venv\Scripts\python.exe'

$env:PYTHONPATH="$PWD\python"
$env:CAISHENFOLIO_MARKET_PROVIDER="fixture"

& $appPython -W error -c `
    "import requests, urllib3, charset_normalizer; print(requests.__version__, urllib3.__version__, charset_normalizer.__version__)"

& $appPython -W error -m unittest discover -s tests/python -p "test_*.py"

$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS="1"
dotnet test tests\Caishenfolio.Host.Tests\Caishenfolio.Host.Tests.csproj `
    --no-build --filter BackgroundExtendedCheckIntegrationTests

scripts\ui_smoke.ps1
git diff --check
```

如果 managed venv 尚未创建，应先通过应用的 `PythonRuntimeProvisioner` 正常供给，不要临时使用系统 Python 冒充产品运行时。

## 10. 人工 UI 验收清单

1. 用现有 CNY 现金模拟买入 USD/US 资产，页面显示地区、币种、现金的交易前→交易后变化。
2. 同币种买入时，货币暴露保持不变。
3. 模拟卖出超过当前持仓，页面只按实际可卖部分计算并明确提示。
4. 输入比例成本 `0`、固定费 `1 USD`，路径卡显示毛额和正确净额。
5. 费用只填一项时，路径卡显示成本未知，不输出伪净额。
6. 账本录入两批不同汇率购汇后，“按账本填入”显示移动加权成本。
7. 再录入一笔来源不明的外币入账，页面清空完整成本并说明存在未知部分。
8. 证据卡同时显示 K线、报价、估值和财务期日期。
9. 人工构造异常日期时，页面显示格式异常，不把原始垃圾值当正常日期。

## 11. Definition of Done

只有同时满足以下条件才能重新声明整改完成：

- 本文 4 个 P1 全部修复，并各有生产链失败反例转绿；
- P2-01～P2-04 完成，质量门禁记录与真实执行一致；
- .NET、managed venv Python、真实后台链路、UI 冒烟全部通过；
- 第 10 节人工 UI 验收留下真实记录，不把导航冒烟写成人工视觉验收；
- `current.md`、`handoff.md` 和 `quality-gates.md` 不再包含过期或互相矛盾的完成声明；
- 没有用零值代替未知，没有引入实盘执行能力。

