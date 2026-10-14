# OMNIX-Caishenfolio 第四轮整改复审

- 日期：2026-09-07；版本：v0.13.3 / R5。
- 对照：`code-review-2026-09-07-remediation-recheck-3.md` 的 R3-01、R3-02 及验收边界。
- **结论：本整改批次的代码修复复审通过，R3-01（P1）、R3-02（P2）关闭；自动门禁通过，完整 UI DoD 未完成。**
- 本次范围内未发现新的阻塞性问题。该结论不等于整个产品已完成真人新手验收或全部后续开发。
- 实际执行：代码审查、独立生产函数反例、全量自动测试、开启门控的真实进程测试、项目 UI 冒烟、Release MSI 构建。产品代码未修改。

## 1. R3-01 外部新增资金持仓占比：通过并关闭

实现位于 `python/caishenfolio_core/research/evidence_card.py:687-690`，已改为：

```text
交易后单仓占比 = (旧单仓占比 × 旧总资产 + 新买入金额) / 交易后总资产
```

旧持仓和新增金额现在使用相同分母。已有错误测试也已纠正：`tests/python/test_evidence_card.py:208-211` 使用独立金额核算的 `40000 / 110000`。

本轮直接调用生产 `assemble_card` / `build_portfolio_impact`，独立核算得到：

| 场景 | 正确值 | 实际值 | 结果 |
|---|---|---|---|
| 旧组合 10 万，旧持仓 2 万，外部新增买入 1 万 | 30000/110000 = 27.2727% | 27.2727% | 通过 |
| 上述场景，单仓上限 28% | 不因单仓产生 risk_exceeded | 未产生风险超限 | 通过 |
| 上述场景，US 地区仅含该标的 | 单仓 after = US after | 均为 27.2727% | 通过 |
| 上述场景，旧现金 5 万 | 50000/110000 = 45.4545% | 45.4545% | 通过 |
| 旧持仓 3 万，外部新增买入 1 万 | 40000/110000 = 36.3636% | 36.3636% | 通过 |
| 旧持仓 2 万，用组合现有现金买入 1 万 | 30000/100000 = 30% | 30% | 通过 |
| 无旧持仓，外部新增买入 1 万 | 10000/110000 = 9.0909% | 9.0909% | 通过 |

新增真实进程测试 `ExternalFundingDilutesTheOldPositionThroughTheRealChain` 已检查 C# 请求传入 `funding=external`，断言实际响应约 27.2727% 且不是 `risk_exceeded`。本轮已显式开启门控并执行，不将未开启时的提前返回计作链路证据。

这里验证的是固定样例的公式与风险阈值，不是对某只股票作投资判断。

## 2. R3-02 非法日期经回退进入标题：通过并关闭

- `python/caishenfolio_core/research/evidence_card.py:1112` 已删除请求日期/原始 bars 日期回退：仅从合法市场日期中选取，全部不可用时返回空串。
- `src/Caishenfolio.Desktop/Wealth/EvidenceCardView.xaml.cs:249-252`：空日期时状态区显示“行情日期未知”，并指向数据质量区。
- 同文件 `:270-278`：结论元数据区也有对应的未知态。

本轮生产函数复验：

| 输入场景 | 最终 market_as_of | 来源与告警 | 结果 |
|---|---|---|---|
| 最后 bars 日期为 garbage，其他通道无有效日期 | 空 | channel_as_of 保留 garbage，有格式告警 | 通过 |
| 最后 bars 日期为 20250719 | 空 | 保留原值，有格式告警 | 通过 |
| 最后 bars 日期为 2026Q1 | 空 | 保留原值，有格式告警 | 通过 |
| 所有市场日期缺失，调用方 as_of=garbage | 空 | 不将调用方值冒充行情日 | 通过 |
| 所有市场日期缺失，调用方 as_of=2099-01-01 | 空 | 即便请求值格式合法，也不冒充行情日 | 通过 |
| 有合法行情日期 2025-07-19，调用方 as_of=garbage | 2025-07-19 | 采用真实通道日期 | 通过 |

精确口径：通道非法日期保留在 `channel_as_of` 并告警；被忽略的调用方 `as_of` 不是市场通道来源，无需声称它也被保存为通道数据。桌面未知态已通过代码检查；本次没有模拟错误行情后进行页面级截图验收。

## 3. 本轮独立执行的门禁

| 检查 | 实际结果 |
|---|---|
| dotnet build Caishenfolio.slnx --no-restore | 通过，0 警告、0 错误 |
| dotnet test Caishenfolio.slnx --no-build --no-restore | 587 通过：Host 552、Desktop 35 |
| 应用 managed venv，Python -W error 全量 | 449 项运行：448 通过、1 跳过；无失败 |
| 开启真实进程门控 | 5/5 通过：后台 2、资金币种 1、固定费 1、外部资金稀释 1 |
| scripts/ui_smoke.ps1 | 通过：真实启动、九页导航、设置窗口、6 个成本框边界不重叠 |
| Release MSI | 通过，v0.13.3，0 警告、0 错误 |
| git diff --check | 无空白错误 |
| 上述独立生产函数探针 | 全部符合金额核算和最终日期契约 |

本次 UI 报告：`.local/ui-smoke/recheck-4-ui-smoke-report.json`。报告记录 status=passed、failure=null；测试应用由冒烟脚本按默认流程关闭。

5 项链路复验须使用完整筛选条件：

```powershell
$env:CAISHENFOLIO_MARKET_PROVIDER='fixture'
$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS='1'
dotnet test tests\Caishenfolio.Host.Tests\Caishenfolio.Host.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~BackgroundExtendedCheckIntegrationTests|FullyQualifiedName~EvidenceCardFundingCurrencyChainTests|FullyQualifiedName~FxPathCostChainTests'
```

单独筛选 `BackgroundExtendedCheckIntegrationTests` 只验证后台两项，不能代替 5 项门禁。

## 4. 文档一致性检查

整改交付正文和 quality-gates 已采用“自动门禁通过，完整 UI DoD 未完成”的口径。但本轮开始检查时，`current.md` 的 Status 以及 `handoff.md` 顶部 Latest Review 仍显示第三轮“待修 R3-01/R3-02”，未随正文更新。

这属于交接记录滞后，不影响已验证的代码修复。本轮按项目协议更新当前状态、新增第四轮最新审核结论，并保留第三轮记录为历史。下一位 Agent 以本报告及新的 Latest Review 为准。

## 5. 下一步：完整 UI 验收

本批次无待返工的 P1/P2 代码问题。保留用户列明的人工验收工作，不将以下事项记为完成：

1. 默认及窄窗口下，六个成本输入框逐一可点击、可进入焦点；Tab 顺序合理且无焦点陷阱。
2. 从桌面分别输入“比例费 0 + 固定费 0”与“比例费 0 + 固定费 1 USD”，确认净额实际显示；费用留空时毛额/未知说明清晰。
3. 在 100%、125%、150% DPI 以及低分辨率/窄窗口中检查控件、状态提示和结果区是否被遮挡或裁剪。
4. 新手按既有产品任务清单无口头指导操作，记录实际卡住的位置与误解。

建议每项记录：窗口尺寸、DPI、输入、预期、实际、通过/未通过、截图或录屏。出现失败再给开发 Agent 定位返工；通过后另做完整 UI 验收结论。

项目中显式 CashTransfer、其他 N 系列后续能力仍按原计划管理，不属于本次两项缺陷关闭的范围。
