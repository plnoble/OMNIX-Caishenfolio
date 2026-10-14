# OMNIX-Caishenfolio 第三轮整改复审

- 日期：2026-09-07；对象：v0.13.3 / R5 当前工作树。
- 对照报告：`code-review-2026-09-07-remediation-recheck-2.md`。
- 结论：**暂不通过完整验收，发现 P1 × 1、P2 × 1。** 上轮布局重叠和卖出权重反例已经关闭；本次发现新增资金买入的关联计算错误，以及日期校验的回退遗漏。
- 方式：实现审查、生产函数反例、全量测试、真实进程测试、项目 UI 冒烟、MSI 构建。产品代码未修改。

## 1. 上轮整改逐项回验

| 上轮项 | 本轮结论 | 证据与边界 |
|---|---|---|
| F-01 成本输入重叠 | 原缺陷已修复；完整 UI DoD 尚未完成 | 比例费 Row 2、固定费 Row 3、结果 Row 4。本轮新增布局冒烟通过，6 框边界不重叠。多 DPI、Tab/鼠标输入和页面净额结果尚无完整证据。 |
| F-02 卖出分布不自洽 | 已通过本项回验 | 当前持仓 20%、地区 10%、只卖 5% 现在清空地区 after；货币合计 120%、Infinity 现在清空货币 after。正常卖出正确。校验器检查有限、非负、合计 1，切片与完整持仓比较。 |
| F-03 严格日期及财报期告警 | 原列举反例已修复，整体仍有回退遗漏 | 紧凑日期、闰日、混合排序、仅异常财报期测试通过；全部市场日期不可用时，原始值再次进入标题，见 R3-02。 |
| F-04 tooltip 与费用提示 | 已修复所述问题 | tooltip 改移动加权；费用不完整提示在结果渲染后恢复。常规业务失败为 HTTP 422，客户端抛异常并显示失败，本轮未将其误报成“失败状态被覆盖”。 |

R3-01 是本轮检查同一组合影响函数时发现的既有缺陷，不应记成“卖出整改引入的新回归”。现有测试中也包含错误公式，须一并纠正。

## 2. R3-01 [P1] 新增资金买入没有稀释原持仓权重，导致错误占比和风险结论

### 位置与证据

- `python/caishenfolio_core/research/evidence_card.py:665`：外部资金使总资产变成旧总额加新增金额。
- `python/caishenfolio_core/research/evidence_card.py:681-682`：仍以 `current_weight_of_symbol + candidate_weight` 算交易后单一持仓。
- `python/caishenfolio_core/research/evidence_card.py:1189-1195`：用该错误值判断风险上限。
- `tests/python/test_evidence_card.py:208-209`：测试也把“旧占比 + 新增金额/新总额”当作正确值。
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:71`：桌面支持选择 `external`，属于可到达路径。

生产 `assemble_card` 已实际复现：

```text
旧组合总额：100000 CNY；旧标的市值：20000 CNY（20%）
外部新增资金：10000 CNY，全部买入该标的；单仓上限：28%

正确：30000 / 110000 = 27.2727%
当前：20% + 10000 / 110000 = 29.0909%
当前 US 地区 after：27.2727%（该地区只有此标的）
当前结论：risk_exceeded，声称买入后 29.1%，超过上限 28.0%
```

它既误触发风险上限，又出现“单一 US 标的 29.09%，全部 US 资产 27.27%”的矛盾。分布合计为 100% 不能证明单仓值也正确。

### 修复要求

让原持仓与新增金额使用同一个交易后分母：

```text
single_after = (current_weight_of_symbol * base_value + amount_in_base) / total_after
```

采用项目已有金额精度约定。现有现金买入分母仍为旧总额；外部新增资金分母包含新增金额；缺汇率等情况保留未知/拒绝语义。禁止调高风险上限或将错误值裁到地区权重来掩盖问题。

### 必须纠正和补充的验收

1. 修正 `test_external_money_grows_the_denominator_and_keeps_cash`：旧持仓 30%、组合 100000、新增买入 10000，应为 `40000/110000 = 36.3636%`，不是 `0.3 + 10000/110000`。
2. 本报告反例：旧持仓 20%、外部买入 10000，应为 27.2727%；现金为 `50000/110000`。
3. 在无其他风险触发的固定数据集上，28% 上限不得因该单仓比例产生 `risk_exceeded`。
4. 现有现金买入仍应为 20% + 10% = 30%；零旧持仓新增买入也要正确。
5. 地区仅有此标的时，标的 after 应等于地区 after；一般情况下标的 after 不应大于所属地区/计价货币 after。
6. 至少增加一项 C# 请求到 Python 的 `funding="external"` 链路断言，验证占比和风险结论。

## 3. R3-02 [P2] 严格日期校验后的回退又使用未校验日期

### 位置与证据

`python/caishenfolio_core/research/evidence_card.py:1099`：

```python
as_of = iso_dates[-1] if iso_dates else (as_of or _bar_date(bars[-1]) if bars else "")
```

`src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:248` 直接把 `market_as_of` 显示为“最新行情截至”。

给有效价格序列提供非法 bars 日期、quote/valuation 无有效日期，生产函数结果为：

| 原始日期 | 实际 market_as_of | 质量警告 |
|---|---|---|
| `garbage` | `garbage` | 有 |
| `20250719` | `20250719` | 有 |
| `2026Q1` | `2026Q1` | 有 |

这次已经有格式警告，不再是上轮的完全静默问题；但被拒绝的日期仍经回退重新用作标题，可能显示“最新行情截至 2026Q1”。

### 修复要求与验收

1. 无有效市场日期时，`market_as_of` 返回契约约定的未知值（字符串契约可用空串），标题显示“行情日期未知”；不得回退未经验证的 bars 日期。
2. 若支持传入 `as_of` 作候选，先验证严格格式及来源，不能使用请求日期或当前日期冒充数据日期。
3. 非法值继续保存在 `channel_as_of` 并保留格式警告。
4. 上述三种值及“外部传入非法 as_of”分别加反例，同时断言主日期未知、原始值保留、警告存在。
5. 保留本轮通过的紧凑格式、闰日、混合排序和仅异常财报期测试。

## 4. UI 剩余验收与文档口径

新增 `Invoke-CostInputLayoutCheck` 本轮确实运行通过。它检查的是六个输入框宽高和两两边界交集，没有验证 `IsOffscreen`、可点击点、键盘焦点、结果区可见性，也未执行表单输入→结果渲染。

`quality-gates.md` 末尾已明确“多 DPI 未做”和“桌面级 0+0 净额端到端未做”，但整改交付时 `current.md`、`handoff.md` 顶部仍称“DoD 全过”。应统一为：**自动门禁通过，完整 UI DoD 未完成。** 本轮已在状态与交接记录中更正复审结论，保留历史记录。

下一轮补充：

- 默认及窄窗口逐一确认成本框可进入焦点，Tab 顺序可完成填写，并检查父容器裁剪/移出可视区。
- 成本全填 0、比例 0 + 固定费 1 USD 两种场景，从桌面输入后观察净额。Client→Python 测试不能代替页面输入和渲染证据。
- 100%、125%、150% DPI 的实际结果、截图或明确未测列表，不能仅凭分行推断全过。

额外图形复核未完成：computer-use 的 Node 运行时因 `helper_unknown_error: setup refresh had errors` 初始化失败，重置后仍退出。项目 UI 冒烟正常执行；这是复核工具故障，不是应用启动失败。没有据此宣称完成多 DPI 或真人体验验收。

## 5. 本轮独立门禁结果

| 验证 | 实际结果 |
|---|---|
| .NET Debug 构建 | 通过，0 警告、0 错误 |
| .NET 全量测试 | 586 通过（Host 551、Desktop 35） |
| 应用 managed venv Python `-W error` | 441 项运行，440 通过、1 跳过 |
| 开启门控的真实进程测试 | 4/4 通过（后台 2、资金币种 1、固定费 1） |
| 项目 UI 冒烟 | 九页导航、设置窗口、6 成本框边界不重叠，通过 |
| Release MSI | 通过，0 警告、0 错误，v0.13.3 |
| git diff --check | 无空白错误（LF/CRLF 提示不视为代码失败） |
| 旧卖出反例及 Infinity 探针 | 正确 fail-closed |
| 外部资金占比及 28% 阈值探针 | 复现 R3-01 |
| 无合法市场日期探针 | 复现 R3-02 |

UI 证据：`.local/ui-smoke/recheck-3-ui-smoke-report.json`。本轮已关闭自己启动的桌面进程。

复验命令（PowerShell，仓库根目录）：

```powershell
dotnet build Caishenfolio.slnx --no-restore
dotnet test Caishenfolio.slnx --no-build --no-restore
$env:PYTHONPATH="$PWD\python"
$env:CAISHENFOLIO_MARKET_PROVIDER='fixture'
$reviewPython="$env:LOCALAPPDATA\Caishenfolio\state\.venv\Scripts\python.exe"
& $reviewPython -W error -m unittest discover -s tests/python -p 'test_*.py'
$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS='1'
dotnet test tests\Caishenfolio.Host.Tests\Caishenfolio.Host.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~BackgroundExtendedCheckIntegrationTests|FullyQualifiedName~EvidenceCardFundingCurrencyChainTests|FullyQualifiedName~FxPathCostChainTests'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui_smoke.ps1
dotnet build packaging\windows\Omnix.Installer.wixproj -c Release --no-restore
```

## 6. 给开发 Agent 的执行顺序

1. 修 R3-01 的金额/分母关系，纠正已有错误测试，补阈值及跨维度一致性断言。
2. 修 R3-02 的最后回退分支，补“所有候选无效”反例和标题未知态。
3. 补汇率页表单→净额的桌面证据、键盘/窗口尺寸/DPI 验收；未测就保留为未测。
4. 执行对应测试及完整门禁，更新质量记录和交接，再提交复审。

无需重做已关闭的卖出和布局修复。本轮经验：测试期望要来自独立的金额核算；日期校验要验证最终输出；分布合计 100% 还需要配合单仓与分布间的一致性检查。
