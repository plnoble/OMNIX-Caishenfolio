# OMNIX-Caishenfolio 整改复审报告（第二轮）

- 日期：2026-09-07
- 版本：v0.13.3 / R5
- 复审对象：`code-review-2026-09-06-remediation-recheck.md` 中 P1×4、P2×5 的整改结果
- 复审方式：生产代码静态审查、生产函数反例、完整自动化测试、真实进程链路、桌面 UI 启动冒烟、Release MSI 构建
- 结论：**未通过复审**。原整改的大部分主链路已经落地，但仍有 **P1×2、P2×1、P3×1**。修完 P1/P2 并重新执行本文门禁后再验收。

> 本报告只记录复审结论和下一轮整改要求，没有修改产品实现。

## 1. 总体结论

这次整改不是无效返工。以下关键问题已经得到实质修复：

- `funding_currency` 已从桌面调用贯通到 C# HTTP 请求及 Python 核心，并有真实进程链路测试；
- 超卖请求已统一按“实际可卖权重”计算单一持仓、现金与地区变化，不再出现 140% 现金；
- 汇率路径请求模型已支持每条路径的比例成本、固定费和费用币种三态输入；
- “按账本填入”已经改为移动加权成本，并覆盖多批次、部分取出和来源不明；
- 证据卡已显示地区、货币和现金的交易前后值；
- 财报陈旧度已按季度跨年计算；
- 正式 Python 门禁已改用应用 managed venv，拼音测试也已可确定性运行；
- 质量门禁记录比上一轮更诚实，明确区分自动化覆盖与真人 UI 验收。

但是，新增的成本控件在实际 XAML 布局中互相覆盖；卖出侧仍可以生成不自洽或不守恒的权重结果。这两个问题会直接影响“小白可用且不误导”的核心目标，因此不能按“整改完成”验收。

## 2. 原问题回验

| 原编号 | 回验结果 | 说明 |
|---|---|---|
| P1-01 `funding_currency` 丢失 | 通过 | C# 参数、JSON、桌面传值和真实 C#→Python 测试均已补齐；CNY 60%→50%、USD 40%→50%。 |
| P1-02 卖出超卖不一致 | **部分通过** | 超卖统一实际可卖量已修复；但部分卖出的地区切片校验和货币权重输入校验仍有缺口，见 F-02。 |
| P1-03 汇率页固定费不可输入 | **部分通过** | 请求模型和控件均已增加；但比例费与固定费控件被放在同一 Grid 行，实际界面重叠，见 F-01。 |
| P1-04 账本成本预填取最近一次 | 通过 | 已复用 `HeldCurrencyCostCalculator` 做移动加权；来源不明时整笔成本留空。 |
| P2-01 UI 缺少完整前后对照 | 通过 | `region/currency/cash` 已带 before/after 并渲染。 |
| P2-02 非法日期未显式告警 | **部分通过** | `garbage` 等会告警；但紧凑日期被当成合法 ISO，且“只有异常财报期、没有行情日期”时仍静默，见 F-03。 |
| P2-03 财报期陈旧度跨年不准 | 通过 | 已使用连续季度索引并有跨年测试。 |
| P2-04 managed venv 拼音测试失败 | 通过 | managed venv 下 Python 434 项通过、1 项跳过、`-W error` 无警告。 |
| P2-05 质量门禁过度宣称 | 基本通过 | `quality-gates.md` 已区分自动化与真人 UI；修完本报告问题后应再更新“整改完成”状态。 |

## 3. 本轮发现

### F-01 [P1] 汇率页的比例成本与固定费输入区占用同一 Grid 行，控件会重叠

**证据**

- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:108-113`：父 Grid 只有 4 行（0、1、2、3）。
- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:155`：比例成本 `WrapPanel` 使用 `Grid.Row="2"`。
- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:172`：固定费 `WrapPanel` 也使用 `Grid.Row="2"`。
- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:195`：结果区使用 `Grid.Row="3"`。

WPF Grid 会把两个面板安排到同一单元格，后声明的固定费面板还会覆盖前一个面板的绘制和命中区域。编译和当前 UI 冒烟只证明页面能加载、导航能到达，不能证明这些输入框互不重叠、可见且可点击。

**影响**

净额比较要求同一路径的比例成本和固定费都显式填写；界面却无法可靠地同时操作这两组输入。P1-03 的后端契约虽然修好，核心用户路径仍可能不可用，尤其不适合小白。

**必须修复**

1. 给父 Grid 增加独立行，例如：比例成本放第 2 行、固定费放第 3 行、结果放第 4 行；也可以把两组成本放入同一个纵向 `StackPanel`，但必须保证自适应换行后仍不覆盖。
2. 检查 100%、125%、150% DPI 和窄窗口下的可见性、换行、Tab 顺序与点击命中。
3. 扩展 UI 验收：导航到“汇率”，确认 6 个成本输入框均可见且边界不相交；分别输入比例成本与固定费后能得到净额，而不是只显示毛额。
4. 不要只以 `dotnet build` 或“能导航到汇率页”作为该项完成证据。

**验收用例**

- 三条路径分别填入 `0` 比例费和 `0` 固定费，均被识别为“确认无费用”，可以计算净额。
- 至少一条路径填 `0` 比例费、`1 USD` 固定费，页面可见净额且为毛额减固定费折算值。
- 成本输入框在上述 DPI 下边界不重叠，每一个都能通过鼠标和键盘 Tab 获得焦点。

### F-02 [P1] 卖出侧只修了“超卖量统一”，没有完整执行 fail-closed 权重不变量

**证据 A：部分卖出绕过地区切片自洽校验**

- `python/caishenfolio_core/research/evidence_card.py:793` 用 `地区切片 < candidate_weight` 判断是否失败。
- 但该函数自己的错误说明（`evidence_card.py:796-797`）要求判断的是“地区切片是否小于该标的当前持仓”。
- 现有测试 `tests/python/test_evidence_card.py:942-949` 使用 90% 超卖请求，`candidate_weight` 被钳制成当前持仓 20%，恰好掩盖了部分卖出场景。

生产函数反例：

```text
组合 100000；标的当前权重 20%；卖出 5000（5%）
地区输入：cn=20%、us=10%、cash=70%；标的地区=us

实际成功输出：us=5%、single_position_after=15%
```

即“该标的还占组合 15%，但它所属的整个 US 地区只占 5%”。输入本来就不自洽，函数应该 fail-closed，却生成了不可能状态。

**证据 B：卖出货币权重未经守恒校验直接回传**

- `python/caishenfolio_core/research/evidence_card.py:802-805` 在有 `instrument_currency` 时直接复制 `currency_weights`。
- 没有检查值是否有限、非负、合计是否约等于 1，也没有检查标的权重是否可能落在该货币切片内。

生产函数反例：

```text
货币输入：CNY=80%、USD=40%（合计 120%）
实际输出：currency_weight_after 原样返回，合计仍为 120%，没有失败说明。
```

**影响**

证据卡可能以“交易后占比”的形式展示数学上不可能或不守恒的分布。对不懂金融的用户，这比不显示更危险，因为它具有确定数字的外观。

**必须修复**

1. 在买入和卖出路径共用同一个“分布输入校验器”：所有值必须为有限数、非负，合计在容差内等于 1。
2. 对卖出地区分布，先验证 `region_weights[instrument_region] >= current_weight_of_symbol - tolerance`，而不是只和本次 `candidate_weight` 比较；输入不自洽时返回空 after 分布并给明确 limitation。
3. 对卖出货币分布至少执行守恒/有限/非负校验；若产品语义认定标的完整归属于计价货币，也应校验该货币切片不小于当前持仓。
4. 计算后再次断言每个非空 after 分布的值均有限、非负且合计约等于 1；失败时清空该维度，不得把错误数字送到 UI。
5. 不要为“让测试通过”而归一化或裁零错误输入；本项目规则要求 fail-closed。

**必须新增的反例测试**

- 当前持仓 20%、所属地区 10%、只卖 5%：`region_weight_after == {}`，并包含“输入不自洽”。
- 卖出时货币权重合计 120%：`currency_weight_after == {}`，并包含“权重不守恒”。
- 卖出时任一权重为负数、`NaN` 或无穷：该维度 fail-closed。
- 参数化覆盖正常卖出、部分卖出、超卖三种情况；每个非空地区/货币 after 分布都满足非负、有限、合计 1。

### F-03 [P2] 日期校验仍有两个静默路径，且可能选错证据卡主日期

**证据 A：名为 strict 的校验并不严格**

- `python/caishenfolio_core/research/evidence_card.py:50-56` 注释声明只接受 `yyyy-mm-dd`，实现却只调用 `date.fromisoformat(value)`。
- 当前 Python 会接受基本格式 `20250719`；生产函数实测 `_is_iso_date("20250719") == True`。
- `evidence_card.py:1027-1032` 随后对这些原始字符串执行 `max()`，混合 `20250719` 与 `2025-07-20` 时是词典序而非日期序，可能把更早日期选为 `market_as_of`。

**证据 B：只有异常财报期时不告警**

- `evidence_card.py:1041-1045` 的外层条件排除了 `financials`；当 bars/quote/valuation 都没有日期时，循环不会进入。
- 生产函数反例：仅提供财报期 `2026XX`、没有行情日期，`channel_as_of.financials` 保留 `2026XX`，但 `warnings == []`。

**必须修复**

1. 使用完整格式检查，例如正则 `^\d{4}-\d{2}-\d{2}$` 后再 `date.fromisoformat`，并确认 round-trip 字符串一致。
2. 不要对日期字符串做 `max()`；先解析为 `date`，比较解析值，再保留对应的原始规范日期。
3. 财报期格式校验应独立运行，不依赖是否存在市场日期；没有市场日期时可以不计算陈旧季度数，但必须报告格式异常。

**必须新增的反例测试**

- `20250719` 被判为格式异常，不能参与主日期排序。
- `2025-07-20` 与非法紧凑格式 `20250719` 同时存在时，主日期必须是 `2025-07-20`。
- 没有 bars/quote/valuation 日期、只有财报期 `2026XX` 时仍产生“格式异常”警告。
- 合法闰日 `2024-02-29` 通过，非法日期 `2025-02-29` 失败。

### F-04 [P3] 两处界面文案已与整改后的真实行为不一致

**证据**

- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:149` 的按钮提示仍写“最近一次换汇的实际成本”，实际实现已改为移动加权成本。
- `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:339-344` 设置“费用不完整，将按毛额”的提示后，`FxCarryView.xaml.cs:347` 立即用“正在取报价”覆盖；请求完成后 `FxCarryView.xaml.cs:425` 又改成通用成功状态。该预检查提示实际不可见。

**建议修复**

- 把 tooltip 改为“按账本移动加权成本；来源不明时成本留空并说明”。
- 将费用完整性状态保存下来，在结果状态或独立告警区持续显示；不要写一段同一同步流程里必然被下一行覆盖的状态文本。
- 后端 warnings 已显示时，也应保证“净额不可用的原因”在净额结果附近清楚可见。

## 4. 推荐整改顺序

1. **先修 F-01**：它阻断汇率净额比较的实际输入路径，修改小、验证明确。
2. **再修 F-02**：抽出共用权重校验器，补齐部分卖出和异常货币分布反例；不要局部打补丁复制逻辑。
3. **再修 F-03**：统一日期解析与比较，补齐无市场日期场景。
4. **最后修 F-04**：同步 tooltip 与可持续状态提示。
5. 更新 `.omx/development/current.md`、`handoff.md`、`quality-gates.md`、`worklog.md` 和 `error-ledger.md`；在 P1/P2 全绿前，不要写“整改全部完成”。

## 5. 下一轮完成定义（DoD）

下一轮仅在以下条件全部满足后可交回复审：

- F-01、F-02、F-03 的生产代码修复和上述反例测试全部落地；
- 汇率页 6 个成本输入框在常用 DPI 下可见、可点、可 Tab，且成功展示含固定费的净额；
- 所有非空地区/货币 after 分布均满足有限、非负、合计约等于 1；异常输入 fail-closed；
- 严格日期格式、混合格式排序、仅异常财报期三个反例通过；
- `dotnet build Caishenfolio.slnx --no-restore`：0 warning / 0 error；
- `dotnet test Caishenfolio.slnx --no-build --no-restore`：全部通过；
- managed venv：`python -W error -m unittest discover -s tests/python -p "test_*.py"` 全部通过；
- 真实进程门禁 4/4 全部通过；
- `scripts/ui_smoke.ps1` 通过，并增加或附上汇率成本控件互不重叠的可复核证据；
- Release MSI 构建通过；
- 只提交本轮必要改动，不回退或覆盖现有用户改动。

## 6. 本轮已执行的验证

| 验证 | 结果 |
|---|---|
| `dotnet build Caishenfolio.slnx --no-restore` | 通过，0 warning / 0 error |
| `dotnet test Caishenfolio.slnx --no-build --no-restore` | 通过，Host 551 + Desktop 35 = 586 |
| managed venv Python `-W error` 全量测试 | 通过，434 tests，1 skipped |
| managed venv `requests` 导入 | 通过，2.34.2 |
| `BackgroundExtendedCheckIntegrationTests` | 通过，2/2 |
| `EvidenceCardFundingCurrencyChainTests` + `FxPathCostChainTests` | 通过，2/2 |
| 真实进程门禁合计 | 通过，4/4 |
| `scripts/ui_smoke.ps1` | 通过：应用启动、版本、九页导航、设置窗口 |
| Release MSI | 通过，生成 v0.13.3 安装包，0 warning / 0 error |
| 卖出部分地区不自洽反例 | **复现缺陷**：成功返回 `single=15%`、`us=5%` |
| 卖出货币权重合计 120% 反例 | **复现缺陷**：原样返回 120% |
| `_is_iso_date("20250719")` | **复现缺陷**：返回 `True` |
| 只有 `financials=2026XX`、无行情日期 | **复现缺陷**：warnings 为空 |

## 7. 给整改 Agent 的边界提醒

- 本轮是个人财富分析工作台，不接入真实券商下单或交易所执行。
- 权重异常必须 fail-closed，禁止静默归一化、裁零或伪造数据。
- 金额继续使用 decimal/Money 语义，不引入 `double` 金额运算。
- XAML 修改后必须真实启动应用；本次问题正说明“构建通过 + 页面能导航”不等于布局可用。
- 保留 research/simulation disclaimer，不把分析结果写成确定性买卖指令。
